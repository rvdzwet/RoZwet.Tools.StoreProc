using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RoZwet.Tools.StoreProc.Application.Pipeline;

/// <summary>
/// Persists and recovers ingestion progress to a local JSON file.
/// Atomic write via temp-file rename guarantees no corrupt state on crash.
///
/// Tracks two orthogonal dimensions of state:
/// <list type="bullet">
///   <item>
///     <term>ProcessedFiles</term> — maps each successfully-ingested file path to its
///     SHA-256 content hash.  Used by the daily incremental run to skip unchanged files.
///   </item>
///   <item>
///     <term>FailedFiles</term> — paths of files that were skipped with a warning
///     (parse failure, AI repair exhaustion, missing CREATE PROCEDURE).
///     Never silently discarded — retried on every run.
///   </item>
/// </list>
/// </summary>
internal sealed class IngestionCheckpoint
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _checkpointFilePath;
    private readonly ILogger<IngestionCheckpoint> _logger;
    private CheckpointState _state;
    private readonly Dictionary<string, string> _pendingProcessed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingFailures             = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingClears               = new(StringComparer.OrdinalIgnoreCase);

    public IngestionCheckpoint(string checkpointFilePath, ILogger<IngestionCheckpoint> logger)
    {
        if (string.IsNullOrWhiteSpace(checkpointFilePath))
            throw new ArgumentException("Checkpoint file path cannot be empty.", nameof(checkpointFilePath));

        _checkpointFilePath = checkpointFilePath;
        _logger = logger;
        _state = new CheckpointState();
    }

    /// <summary>
    /// Maps each successfully-ingested file path to its last-known SHA-256 content hash.
    /// Files present in this map with a matching hash are skipped on incremental runs.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProcessedFiles => _state.ProcessedFiles;

    /// <summary>Set of file paths that were skipped due to analysis warnings in previous runs.</summary>
    public IReadOnlySet<string> FailedFilePaths => _state.FailedFiles;

    /// <summary>
    /// Returns the stored content hash for a file path, or <see langword="null"/> when
    /// the file has never been successfully processed.
    /// </summary>
    public string? GetStoredHash(string filePath) =>
        _state.ProcessedFiles.TryGetValue(filePath, out var hash) ? hash : null;

    /// <summary>
    /// Stages a successfully-processed file with its content hash.
    /// Flushed to disk on the next <see cref="CommitAsync"/> call.
    /// </summary>
    public void RecordProcessed(string filePath, string contentHash)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && !string.IsNullOrWhiteSpace(contentHash))
            _pendingProcessed[filePath] = contentHash;
    }

    /// <summary>
    /// Records a file as failed (warning-level skip) so that it is persisted in the
    /// next <see cref="CommitAsync"/> call.  Failures are cumulative across all batches.
    /// </summary>
    public void RecordFailure(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
            _pendingFailures.Add(filePath);
    }

    /// <summary>
    /// Marks a previously-failed file as recovered so that it is removed from
    /// <c>FailedFiles</c> in the next <see cref="CommitAsync"/> call.
    /// Call this when a retry of a previously-failed file succeeds.
    /// </summary>
    public void ClearFailure(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _pendingClears.Add(filePath);
            _pendingFailures.Remove(filePath);
        }
    }

    /// <summary>
    /// Loads existing checkpoint state from disk.
    /// If no checkpoint file exists or the file uses the legacy format (index-based),
    /// initialises to an empty hash-map state (triggers a full re-ingest on first run).
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_checkpointFilePath))
        {
            _logger.LogInformation("No checkpoint found at '{Path}'. Starting fresh.", _checkpointFilePath);
            _state = new CheckpointState();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_checkpointFilePath, cancellationToken);

            // Detect legacy format: contains "lastCommittedIndex" property.
            if (json.Contains("\"lastCommittedIndex\"", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Legacy index-based checkpoint detected at '{Path}'. " +
                    "Migrating to hash-map format — a full re-ingest will run once.",
                    _checkpointFilePath);
                _state = new CheckpointState();
                return;
            }

            _state = JsonSerializer.Deserialize<CheckpointState>(json, SerializerOptions)
                     ?? new CheckpointState();

            _logger.LogInformation(
                "Checkpoint loaded. {Processed} previously processed, {Failures} known failures.",
                _state.ProcessedFiles.Count, _state.FailedFiles.Count);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Checkpoint file corrupted at '{Path}'. Starting fresh.", _checkpointFilePath);
            _state = new CheckpointState();
        }
    }

    /// <summary>
    /// Commits all pending changes — processed hashes, new failures, and cleared failures — to disk.
    /// Atomic write via temp-file rename guarantees no corrupt checkpoint on crash or power loss.
    /// </summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var mergedProcessed = new Dictionary<string, string>(_state.ProcessedFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in _pendingProcessed)
            mergedProcessed[path] = hash;

        var mergedFailures = new HashSet<string>(_state.FailedFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var f in _pendingFailures)
            mergedFailures.Add(f);
        foreach (var f in _pendingClears)
            mergedFailures.Remove(f);

        _pendingProcessed.Clear();
        _pendingFailures.Clear();
        _pendingClears.Clear();

        _state = new CheckpointState
        {
            LastRunUtc    = DateTime.UtcNow,
            ProcessedFiles = mergedProcessed,
            FailedFiles   = mergedFailures
        };

        var json     = JsonSerializer.Serialize(_state, SerializerOptions);
        var tempPath = _checkpointFilePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _checkpointFilePath, overwrite: true);

        _logger.LogDebug(
            "Checkpoint committed: {Processed} processed, {Failures} failures.",
            _state.ProcessedFiles.Count, _state.FailedFiles.Count);
    }

    /// <summary>
    /// Deletes the checkpoint file and resets all in-memory state.
    /// </summary>
    public void Delete()
    {
        if (File.Exists(_checkpointFilePath))
        {
            File.Delete(_checkpointFilePath);
            _logger.LogInformation("Checkpoint file deleted at '{Path}'.", _checkpointFilePath);
        }

        _state = new CheckpointState();
        _pendingProcessed.Clear();
        _pendingFailures.Clear();
        _pendingClears.Clear();
    }

    private sealed class CheckpointState
    {
        public DateTime LastRunUtc { get; init; }

        [JsonConverter(typeof(DictionaryStringConverter))]
        public Dictionary<string, string> ProcessedFiles { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        [JsonConverter(typeof(HashSetStringConverter))]
        public HashSet<string> FailedFiles { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Custom JSON converter so <c>ProcessedFiles</c> round-trips as a plain JSON object
    /// while retaining OrdinalIgnoreCase key comparison internally.
    /// </summary>
    private sealed class DictionaryStringConverter : JsonConverter<Dictionary<string, string>>
    {
        public override Dictionary<string, string> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options) ?? [];
            return new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Dictionary<string, string> value,
            JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value.OrderBy(kvp => kvp.Key)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value), options);
        }
    }

    /// <summary>
    /// Custom JSON converter so <c>FailedFiles</c> round-trips as a plain JSON array
    /// while remaining a mutable <see cref="HashSet{T}"/> internally.
    /// </summary>
    private sealed class HashSetStringConverter : JsonConverter<HashSet<string>>
    {
        public override HashSet<string> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var list = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? [];
            return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }

        public override void Write(
            Utf8JsonWriter writer,
            HashSet<string> value,
            JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value.OrderBy(x => x), options);
        }
    }
}
