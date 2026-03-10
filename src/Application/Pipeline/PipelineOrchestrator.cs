using Microsoft.Extensions.Logging;
using RoZwet.Tools.StoreProc.Application.Agents;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Domain;

namespace RoZwet.Tools.StoreProc.Application.Pipeline;

/// <summary>
/// Orchestrates the full ingestion pipeline for 5,500 stored procedures.
/// Durable: resumes from the last committed checkpoint if interrupted.
/// Processes files in configurable batches, writing each batch to Neo4j
/// before committing progress to disk.
/// </summary>
internal sealed class PipelineOrchestrator
{
    private readonly SqlAnalysisAgent _analysisAgent;
    private readonly INeo4jRepository _repository;
    private readonly IngestionCheckpoint _checkpoint;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        SqlAnalysisAgent analysisAgent,
        INeo4jRepository repository,
        IngestionCheckpoint checkpoint,
        ILogger<PipelineOrchestrator> logger)
    {
        _analysisAgent = analysisAgent;
        _repository = repository;
        _checkpoint = checkpoint;
        _logger = logger;
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

        _logger.LogInformation("Discovered {Total} stored procedure files in '{Directory}'.", sqlFiles.Length, sqlDirectory);

        await _checkpoint.LoadAsync(cancellationToken);

        int startIndex = _checkpoint.LastCommittedIndex + 1;

        if (startIndex >= sqlFiles.Length)
        {
            _logger.LogInformation("All {Total} files already processed. Nothing to do.", sqlFiles.Length);
            return;
        }

        _logger.LogInformation(
            "Starting ingestion from file index {Start} / {Total}.",
            startIndex, sqlFiles.Length);

        await _repository.EnsureSchemaAsync(cancellationToken);

        var totalProcessed = _checkpoint.LastCommittedIndex + 1;
        var batchBuffer = new List<StoredProcedure>(batchSize);

        for (int i = startIndex; i < sqlFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = sqlFiles[i];
            _logger.LogInformation("[{Current}/{Total}] Processing: {File}", i + 1, sqlFiles.Length, Path.GetFileName(filePath));

            StoredProcedure? procedure = null;

            try
            {
                procedure = await _analysisAgent.AnalyzeAsync(filePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze file '{FilePath}'. Skipping.", filePath);
            }

            if (procedure is not null)
                batchBuffer.Add(procedure);

            bool isBatchFull = batchBuffer.Count >= batchSize;
            bool isLastFile = i == sqlFiles.Length - 1;

            if ((isBatchFull || isLastFile) && batchBuffer.Count > 0)
            {
                await _repository.UpsertBatchAsync(batchBuffer.AsReadOnly(), cancellationToken);

                totalProcessed += batchBuffer.Count;
                await _checkpoint.CommitAsync(i, totalProcessed, cancellationToken);

                _logger.LogInformation(
                    "Batch committed: {BatchCount} procedures. Total committed: {Total}/{Grand}.",
                    batchBuffer.Count, totalProcessed, sqlFiles.Length);

                batchBuffer.Clear();
            }
        }

        _logger.LogInformation("Ingestion complete. {Total} procedures processed.", totalProcessed);
    }
}
