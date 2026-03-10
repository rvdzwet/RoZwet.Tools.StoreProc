using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RoZwet.Tools.StoreProc.Application.Agents;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Domain;

namespace RoZwet.Tools.StoreProc.Application.Pipeline;

/// <summary>
/// Orchestrates the full ingestion pipeline for 5,500+ stored procedures.
/// Durable: resumes from the last committed checkpoint if interrupted.
///
/// Two-speed architecture:
/// <list type="bullet">
///   <item>
///     <term>Fast path (Tier-1)</term> — sequential; deterministic parser runs at
///     memory-speed.  Successful parses are embedding-enriched and batched to Neo4j.
///     The committed index advances continuously.
///   </item>
///   <item>
///     <term>Background path (Tier-2)</term> — files that fail Tier-1 are immediately
///     recorded as failures in the checkpoint (durable), then dispatched as
///     fire-and-forget tasks.  Each background task clears the failure and commits
///     its result to Neo4j independently when it completes.
///   </item>
/// </list>
///
/// Thread safety: <see cref="_checkpointLock"/> (SemaphoreSlim(1,1)) serialises all
/// concurrent <see cref="IngestionCheckpoint.CommitAsync"/> calls from background tasks
/// and the main loop.
/// </summary>
internal sealed class PipelineOrchestrator
{
    private readonly SqlAnalysisAgent _analysisAgent;
    private readonly INeo4jRepository _repository;
    private readonly IngestionCheckpoint _checkpoint;
    private readonly ILogger<PipelineOrchestrator> _logger;
    private readonly int _batchSize;

    private readonly SemaphoreSlim _checkpointLock = new(1, 1);
    private volatile int _totalProcessed;

    public PipelineOrchestrator(
        SqlAnalysisAgent analysisAgent,
        INeo4jRepository repository,
        IngestionCheckpoint checkpoint,
        IConfiguration config,
        ILogger<PipelineOrchestrator> logger)
    {
        _analysisAgent = analysisAgent;
        _repository    = repository;
        _checkpoint    = checkpoint;
        _logger        = logger;
        _batchSize     = int.TryParse(config["Ingestion:BatchSize"], out var b) && b > 0 ? b : 10;
    }

    /// <summary>
    /// Runs the full ingestion pipeline over all SQL files in <paramref name="sqlDirectory"/>.
    /// Resumes automatically from the last persisted checkpoint.
    /// </summary>
    public async Task RunAsync(
        string sqlDirectory,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sqlDirectory))
            throw new DirectoryNotFoundException($"SQL source directory not found: '{sqlDirectory}'");

        var sqlFiles = Directory.GetFiles(sqlDirectory, "*.sp", SearchOption.AllDirectories)
                                .OrderBy(f => f)
                                .ToArray();

        if (sqlFiles.Length == 0)
        {
            _logger.LogWarning("No .sp files found in '{Directory}'.", sqlDirectory);
            return;
        }

        _logger.LogInformation(
            "Discovered {Total} stored procedure files in '{Directory}'.",
            sqlFiles.Length, sqlDirectory);

        await _checkpoint.LoadAsync(cancellationToken);

        int startIndex = _checkpoint.LastCommittedIndex + 1;

        if (startIndex >= sqlFiles.Length && _checkpoint.FailedFilePaths.Count == 0)
        {
            _logger.LogInformation("All {Total} files already processed. Nothing to do.", sqlFiles.Length);
            return;
        }

        await _repository.EnsureSchemaAsync(cancellationToken);

        _totalProcessed = Math.Max(0, _checkpoint.LastCommittedIndex + 1);

        _totalProcessed = await RetryPreviouslyFailedFilesAsync(_totalProcessed, cancellationToken);

        if (startIndex >= sqlFiles.Length)
        {
            _logger.LogInformation("Main scan already complete. Only retry pass was executed.");
            return;
        }

        _logger.LogInformation(
            "Starting ingestion from file index {Start} / {Total}.",
            startIndex, sqlFiles.Length);

        var batchBuffer = new List<StoredProcedure>(batchSize);

        for (int i = startIndex; i < sqlFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath   = sqlFiles[i];
            var fileNumber = i + 1;

            _logger.LogInformation(
                "[{Current}/{Total}] Parsing: {File}",
                fileNumber, sqlFiles.Length, Path.GetFileName(filePath));

            var tier1 = await _analysisAgent.TryTier1Async(filePath, cancellationToken);

            if (tier1.Success && tier1.Procedure is not null)
            {
                var proc = await _analysisAgent.ApplyEmbeddingAsync(
                    tier1.Procedure, tier1.OriginalSql, cancellationToken);

                batchBuffer.Add(proc);

                bool isLast     = i == sqlFiles.Length - 1;
                bool batchReady = batchBuffer.Count >= batchSize;

                if (batchReady || isLast)
                {
                    await _repository.UpsertBatchAsync(batchBuffer.AsReadOnly(), cancellationToken);
                    _totalProcessed += batchBuffer.Count;

                    _logger.LogInformation(
                        "Batch committed: {BatchCount} procedures. Total committed: {Total}/{Grand}.",
                        batchBuffer.Count, _totalProcessed, sqlFiles.Length);

                    batchBuffer.Clear();
                    await CommitCheckpointSafeAsync(i, _totalProcessed, cancellationToken);
                }
                else
                {
                    await CommitCheckpointSafeAsync(i, _totalProcessed, cancellationToken);
                }
            }
            else
            {
                _checkpoint.RecordFailure(filePath);
                await CommitCheckpointSafeAsync(i, _totalProcessed, cancellationToken);

                _ = RepairInBackgroundAsync(filePath, tier1, cancellationToken);
            }
        }

        if (batchBuffer.Count > 0)
        {
            await _repository.UpsertBatchAsync(batchBuffer.AsReadOnly(), cancellationToken);
            _totalProcessed += batchBuffer.Count;
            batchBuffer.Clear();

            await CommitCheckpointSafeAsync(sqlFiles.Length - 1, _totalProcessed, cancellationToken);
        }

        _logger.LogInformation(
            "Ingestion scan complete. {Total} procedures committed to Neo4j (background repairs may still be running).",
            _totalProcessed);
    }

    /// <summary>
    /// Background fire-and-forget Tier-2 repair task.
    /// On success: upserts to Neo4j, clears the failure entry, commits checkpoint.
    /// On failure: leaves the file recorded as failed for the next retry pass.
    /// </summary>
    private async Task RepairInBackgroundAsync(
        string filePath,
        Tier1Result tier1,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "[BG-REPAIR] Starting repair for '{File}'.", Path.GetFileName(filePath));

            var proc = await _analysisAgent.RepairAndCompleteAsync(
                filePath, tier1.NormalizedSql, tier1.Errors, tier1.OriginalSql, cancellationToken);

            if (proc is null)
            {
                _logger.LogWarning(
                    "[BG-REPAIR] Repair exhausted for '{File}'. File remains in FailedFiles.",
                    Path.GetFileName(filePath));
                return;
            }

            await _repository.UpsertBatchAsync([proc], cancellationToken);

            await _checkpointLock.WaitAsync(cancellationToken);
            try
            {
                _checkpoint.ClearFailure(filePath);
                int total = Interlocked.Increment(ref _totalProcessed);
                await _checkpoint.CommitAsync(_checkpoint.LastCommittedIndex, total, cancellationToken);
            }
            finally
            {
                _checkpointLock.Release();
            }

            _logger.LogInformation(
                "[BG-REPAIR] Committed repaired procedure '{File}'.", Path.GetFileName(filePath));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "[BG-REPAIR] Cancelled for '{File}'. File remains in FailedFiles for next run.",
                Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[BG-REPAIR] Unhandled exception for '{FilePath}'. File remains in FailedFiles.",
                filePath);
        }
    }

    /// <summary>
    /// Thread-safe wrapper around <see cref="IngestionCheckpoint.CommitAsync"/>.
    /// Ensures background tasks and the main loop never corrupt the checkpoint file.
    /// </summary>
    private async Task CommitCheckpointSafeAsync(
        int lastCommittedIndex,
        int totalProcessed,
        CancellationToken cancellationToken)
    {
        await _checkpointLock.WaitAsync(cancellationToken);
        try
        {
            await _checkpoint.CommitAsync(lastCommittedIndex, totalProcessed, cancellationToken);
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    /// <summary>
    /// Re-processes every file recorded as failed in the current checkpoint.
    /// Uses both Tier-1 and Tier-2 (awaited) since this pass is already the slow path.
    /// Files that succeed are cleared; files that still fail are re-recorded.
    /// </summary>
    private async Task<int> RetryPreviouslyFailedFilesAsync(
        int totalProcessed,
        CancellationToken cancellationToken)
    {
        var failedPaths = _checkpoint.FailedFilePaths.ToList();
        if (failedPaths.Count == 0)
            return totalProcessed;

        _logger.LogInformation(
            "Retrying {Count} previously failed file(s) from checkpoint.",
            failedPaths.Count);

        int recovered    = 0;
        int stillFailing = 0;

        foreach (var fp in failedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(fp))
            {
                _logger.LogWarning(
                    "Previously failed file no longer exists: '{FilePath}'. Removing from failures.", fp);
                _checkpoint.ClearFailure(fp);
                await CommitCheckpointSafeAsync(_checkpoint.LastCommittedIndex, totalProcessed, cancellationToken);
                continue;
            }

            _logger.LogInformation("[RETRY] {File}", Path.GetFileName(fp));

            StoredProcedure? procedure = null;

            try
            {
                var tier1 = await _analysisAgent.TryTier1Async(fp, cancellationToken);

                if (tier1.Success && tier1.Procedure is not null)
                {
                    procedure = await _analysisAgent.ApplyEmbeddingAsync(
                        tier1.Procedure, tier1.OriginalSql, cancellationToken);
                }
                else if (tier1.Errors.Count > 0)
                {
                    procedure = await _analysisAgent.RepairAndCompleteAsync(
                        fp, tier1.NormalizedSql, tier1.Errors, tier1.OriginalSql, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RETRY] Failed to analyze '{FilePath}'.", fp);
            }

            if (procedure is not null)
            {
                await _repository.UpsertBatchAsync([procedure], cancellationToken);
                totalProcessed++;
                _checkpoint.ClearFailure(fp);
                recovered++;

                _logger.LogInformation("[RETRY] Recovered '{File}'.", Path.GetFileName(fp));
            }
            else
            {
                _checkpoint.RecordFailure(fp);
                stillFailing++;
            }

            await CommitCheckpointSafeAsync(_checkpoint.LastCommittedIndex, totalProcessed, cancellationToken);
        }

        _logger.LogInformation(
            "Retry pass complete: {Recovered} recovered, {StillFailing} still failing.",
            recovered, stillFailing);

        return totalProcessed;
    }
}
