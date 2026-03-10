using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RoZwet.Tools.StoreProc.Application.Agents;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Domain;

namespace RoZwet.Tools.StoreProc.Application.Pipeline;

/// <summary>
/// Orchestrates the full ingestion pipeline over all SQL source files.
/// Safe to run on a daily schedule — only files whose content has changed
/// (detected via SHA-256 hash comparison) are re-processed.
///
/// Two-speed architecture:
/// <list type="bullet">
///   <item>
///     <term>Fast path (Tier-1)</term> — sequential; deterministic parser runs at
///     memory-speed.  Successful parses are embedding-enriched and batched to Neo4j.
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
    /// Runs the incremental ingestion pipeline over all SQL files in <paramref name="sqlDirectory"/>.
    /// Files whose SHA-256 content hash matches the stored checkpoint hash are skipped.
    /// Previously-failed files are always retried.
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
        await _repository.EnsureSchemaAsync(cancellationToken);

        // Retry previously-failed files first (always, regardless of hash).
        await RetryPreviouslyFailedFilesAsync(cancellationToken);

        // Main incremental scan.
        int skipped   = 0;
        int processed = 0;

        var batchBuffer = new List<StoredProcedure>(batchSize);
        var batchPaths  = new List<string>(batchSize);

        for (int i = 0; i < sqlFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = sqlFiles[i];

            // Skip files that are already known-failed (handled in retry pass).
            if (_checkpoint.FailedFilePaths.Contains(filePath))
                continue;

            // Incremental change detection: skip if hash matches.
            var currentHash = SqlAnalysisAgent.ComputeContentHash(
                await File.ReadAllTextAsync(filePath, cancellationToken));

            var storedHash = _checkpoint.GetStoredHash(filePath);
            if (storedHash is not null && string.Equals(storedHash, currentHash, StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            _logger.LogInformation(
                "[{Current}/{Total}] Parsing (changed): {File}",
                i + 1, sqlFiles.Length, Path.GetFileName(filePath));

            var tier1 = await _analysisAgent.TryTier1Async(filePath, cancellationToken);

            if (tier1.Success && tier1.Procedure is not null)
            {
                var proc = await _analysisAgent.ApplyEmbeddingAsync(
                    tier1.Procedure, tier1.OriginalSql, cancellationToken);

                batchBuffer.Add(proc);
                batchPaths.Add(filePath);
                processed++;

                bool isLast     = i == sqlFiles.Length - 1;
                bool batchReady = batchBuffer.Count >= batchSize;

                if (batchReady || isLast)
                {
                    await _repository.UpsertBatchAsync(batchBuffer.AsReadOnly(), cancellationToken);

                    // Stage all successfully-upserted files with their hashes.
                    foreach (var (path, p) in batchPaths.Zip(batchBuffer))
                        _checkpoint.RecordProcessed(path, p.ContentHash ?? currentHash);

                    batchBuffer.Clear();
                    batchPaths.Clear();

                    await CommitCheckpointSafeAsync(cancellationToken);

                    _logger.LogInformation(
                        "Batch committed. Total changed+processed this run: {Processed}.",
                        processed);
                }
            }
            else
            {
                _checkpoint.RecordFailure(filePath);
                await CommitCheckpointSafeAsync(cancellationToken);

                _ = RepairInBackgroundAsync(filePath, tier1, currentHash, cancellationToken);
            }
        }

        // Flush any remaining buffer.
        if (batchBuffer.Count > 0)
        {
            await _repository.UpsertBatchAsync(batchBuffer.AsReadOnly(), cancellationToken);

            foreach (var (path, p) in batchPaths.Zip(batchBuffer))
                _checkpoint.RecordProcessed(path, p.ContentHash ?? string.Empty);

            batchBuffer.Clear();
            batchPaths.Clear();

            await CommitCheckpointSafeAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Ingestion run complete. Skipped (unchanged): {Skipped}. " +
            "Processed (new/changed): {Processed}. " +
            "Background repairs may still be running.",
            skipped, processed);
    }

    /// <summary>
    /// Background fire-and-forget Tier-2 repair task.
    /// On success: upserts to Neo4j, clears the failure entry, commits checkpoint.
    /// On failure: leaves the file recorded as failed for the next retry pass.
    /// </summary>
    private async Task RepairInBackgroundAsync(
        string filePath,
        Tier1Result tier1,
        string contentHash,
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
                _checkpoint.RecordProcessed(filePath, proc.ContentHash ?? contentHash);
                await _checkpoint.CommitAsync(cancellationToken);
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
    private async Task CommitCheckpointSafeAsync(CancellationToken cancellationToken)
    {
        await _checkpointLock.WaitAsync(cancellationToken);
        try
        {
            await _checkpoint.CommitAsync(cancellationToken);
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    /// <summary>
    /// Re-processes every file recorded as failed in the current checkpoint.
    /// Uses both Tier-1 and Tier-2 (awaited) since this pass is already the slow path.
    /// Files that succeed are cleared and recorded as processed; files that still fail
    /// are re-recorded.
    /// </summary>
    private async Task RetryPreviouslyFailedFilesAsync(CancellationToken cancellationToken)
    {
        var failedPaths = _checkpoint.FailedFilePaths.ToList();
        if (failedPaths.Count == 0)
            return;

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
                await CommitCheckpointSafeAsync(cancellationToken);
                continue;
            }

            _logger.LogInformation("[RETRY] {File}", Path.GetFileName(fp));

            StoredProcedure? procedure = null;
            string contentHash = string.Empty;

            try
            {
                var tier1 = await _analysisAgent.TryTier1Async(fp, cancellationToken);
                contentHash = tier1.OriginalSql.Length > 0
                    ? SqlAnalysisAgent.ComputeContentHash(tier1.OriginalSql)
                    : string.Empty;

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
                _checkpoint.ClearFailure(fp);
                _checkpoint.RecordProcessed(fp, procedure.ContentHash ?? contentHash);
                recovered++;

                _logger.LogInformation("[RETRY] Recovered '{File}'.", Path.GetFileName(fp));
            }
            else
            {
                _checkpoint.RecordFailure(fp);
                stillFailing++;
            }

            await CommitCheckpointSafeAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Retry pass complete: {Recovered} recovered, {StillFailing} still failing.",
            recovered, stillFailing);
    }
}
