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
/// Files are processed in configurable-width concurrent windows
/// (<c>Ingestion:MaxConcurrency</c>, default 4).  Within each window all
/// <see cref="SqlAnalysisAgent.AnalyzeAsync"/> calls — including any background
/// AI repair — run in parallel.  Batch writes to Neo4j and checkpoint commits
/// occur after each completed window, guaranteeing that the committed index
/// always represents a contiguous, fully-resolved range of files.
/// </summary>
internal sealed class PipelineOrchestrator
{
    private readonly SqlAnalysisAgent _analysisAgent;
    private readonly INeo4jRepository _repository;
    private readonly IngestionCheckpoint _checkpoint;
    private readonly ILogger<PipelineOrchestrator> _logger;
    private readonly int _maxConcurrency;

    public PipelineOrchestrator(
        SqlAnalysisAgent analysisAgent,
        INeo4jRepository repository,
        IngestionCheckpoint checkpoint,
        IConfiguration config,
        ILogger<PipelineOrchestrator> logger)
    {
        _analysisAgent   = analysisAgent;
        _repository      = repository;
        _checkpoint      = checkpoint;
        _logger          = logger;
        _maxConcurrency  = int.TryParse(config["Ingestion:MaxConcurrency"], out var c) && c > 0 ? c : 4;
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
            "Discovered {Total} stored procedure files in '{Directory}'. Concurrency={Concurrency}.",
            sqlFiles.Length, sqlDirectory, _maxConcurrency);

        await _checkpoint.LoadAsync(cancellationToken);

        int startIndex = _checkpoint.LastCommittedIndex + 1;

        if (startIndex >= sqlFiles.Length && _checkpoint.FailedFilePaths.Count == 0)
        {
            _logger.LogInformation("All {Total} files already processed. Nothing to do.", sqlFiles.Length);
            return;
        }

        await _repository.EnsureSchemaAsync(cancellationToken);

        var totalProcessed = Math.Max(0, _checkpoint.LastCommittedIndex + 1);

        totalProcessed = await RetryPreviouslyFailedFilesAsync(totalProcessed, cancellationToken);

        if (startIndex >= sqlFiles.Length)
        {
            _logger.LogInformation("Main scan already complete. Only retry pass was executed.");
            return;
        }

        _logger.LogInformation(
            "Starting ingestion from file index {Start} / {Total}.",
            startIndex, sqlFiles.Length);

        var batchBuffer = new List<StoredProcedure>(batchSize);

        for (int i = startIndex; i < sqlFiles.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int windowSize = Math.Min(_maxConcurrency, sqlFiles.Length - i);
            var windowTasks = new Task<StoredProcedure?>[windowSize];

            for (int w = 0; w < windowSize; w++)
            {
                var filePath   = sqlFiles[i + w];
                var fileIndex  = i + w;
                var fileNumber = fileIndex + 1;
                _logger.LogInformation(
                    "[{Current}/{Total}] Queuing: {File}",
                    fileNumber, sqlFiles.Length, Path.GetFileName(filePath));
                windowTasks[w] = AnalyzeFileSafeAsync(filePath, cancellationToken);
            }

            var windowResults = await Task.WhenAll(windowTasks);

            for (int w = 0; w < windowSize; w++)
            {
                var procedure = windowResults[w];
                var filePath  = sqlFiles[i + w];

                if (procedure is not null)
                {
                    batchBuffer.Add(procedure);
                }
                else
                {
                    _checkpoint.RecordFailure(filePath);
                }
            }

            int lastIndexInWindow = i + windowSize - 1;
            bool isLastWindow     = lastIndexInWindow == sqlFiles.Length - 1;

            if (batchBuffer.Count >= batchSize || isLastWindow)
            {
                if (batchBuffer.Count > 0)
                {
                    await _repository.UpsertBatchAsync(batchBuffer.AsReadOnly(), cancellationToken);
                    totalProcessed += batchBuffer.Count;

                    _logger.LogInformation(
                        "Batch committed: {BatchCount} procedures. Total committed: {Total}/{Grand}.",
                        batchBuffer.Count, totalProcessed, sqlFiles.Length);

                    batchBuffer.Clear();
                }

                await _checkpoint.CommitAsync(lastIndexInWindow, totalProcessed, cancellationToken);
            }

            i += windowSize;
        }

        _logger.LogInformation("Ingestion complete. {Total} procedures processed.", totalProcessed);
    }

    /// <summary>
    /// Wraps <see cref="SqlAnalysisAgent.AnalyzeAsync"/> with exception isolation
    /// so that a single file failure does not abort the entire concurrent window.
    /// </summary>
    private async Task<StoredProcedure?> AnalyzeFileSafeAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _analysisAgent.AnalyzeAsync(filePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze file '{FilePath}'. Skipping.", filePath);
            return null;
        }
    }

    /// <summary>
    /// Re-processes every file that is recorded as failed in the current checkpoint.
    /// Files that succeed are cleared from the failure list; files that still fail are
    /// re-recorded so they remain visible in the checkpoint for operator inspection.
    /// The scan-head index (<see cref="IngestionCheckpoint.LastCommittedIndex"/>) is
    /// not advanced during this pass — only <c>FailedFiles</c> and
    /// <paramref name="totalProcessed"/> are updated.
    /// Retry analyses also run concurrently up to <c>MaxConcurrency</c>.
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

        var retryBuffer = new List<StoredProcedure>(failedPaths.Count);
        int recovered   = 0;
        int stillFailing = 0;

        for (int i = 0; i < failedPaths.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int windowSize = Math.Min(_maxConcurrency, failedPaths.Count - i);
            var windowTasks = new Task<(string Path, StoredProcedure? Procedure)>[windowSize];

            for (int w = 0; w < windowSize; w++)
            {
                var fp = failedPaths[i + w];

                if (!File.Exists(fp))
                {
                    _logger.LogWarning(
                        "Previously failed file no longer exists on disk: '{FilePath}'. Removing from failures.", fp);
                    _checkpoint.ClearFailure(fp);
                    windowTasks[w] = Task.FromResult<(string, StoredProcedure?)>((fp, null));
                    continue;
                }

                _logger.LogInformation("[RETRY] {File}", Path.GetFileName(fp));
                windowTasks[w] = RetryFileAsync(fp, cancellationToken);
            }

            var windowResults = await Task.WhenAll(windowTasks);

            foreach (var (fp, procedure) in windowResults)
            {
                if (procedure is not null)
                {
                    retryBuffer.Add(procedure);
                    _checkpoint.ClearFailure(fp);
                    recovered++;

                    if (retryBuffer.Count >= _maxConcurrency)
                    {
                        await _repository.UpsertBatchAsync(retryBuffer.AsReadOnly(), cancellationToken);
                        totalProcessed += retryBuffer.Count;
                        retryBuffer.Clear();
                        await _checkpoint.CommitAsync(_checkpoint.LastCommittedIndex, totalProcessed, cancellationToken);
                    }
                }
                else if (!_checkpoint.FailedFilePaths.Contains(fp))
                {
                    _checkpoint.RecordFailure(fp);
                    stillFailing++;
                    await _checkpoint.CommitAsync(_checkpoint.LastCommittedIndex, totalProcessed, cancellationToken);
                }
            }

            i += windowSize;
        }

        if (retryBuffer.Count > 0)
        {
            await _repository.UpsertBatchAsync(retryBuffer.AsReadOnly(), cancellationToken);
            totalProcessed += retryBuffer.Count;
        }

        await _checkpoint.CommitAsync(_checkpoint.LastCommittedIndex, totalProcessed, cancellationToken);

        _logger.LogInformation(
            "Retry pass complete: {Recovered} recovered, {StillFailing} still failing.",
            recovered, stillFailing);

        return totalProcessed;
    }

    private async Task<(string Path, StoredProcedure? Procedure)> RetryFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var procedure = await _analysisAgent.AnalyzeAsync(filePath, cancellationToken);
            return (filePath, procedure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RETRY] Failed to analyze '{FilePath}'.", filePath);
            return (filePath, null);
        }
    }
}
