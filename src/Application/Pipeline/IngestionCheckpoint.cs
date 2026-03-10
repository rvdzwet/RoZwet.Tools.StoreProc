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
///   <item><term>LastCommittedIndex</term> — sequential scan progress (which file index was last committed).</item>
///   <item><term>FailedFiles</term> — paths of files that were skipped with a warning (parse failure, AI repair exhaustion, missing CREATE PROCEDURE). These are never silently discarded.</item>
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
    private readonly HashSet<string> _pendingFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingClears   = new(StringComparer.OrdinalIgnoreCase);

    public IngestionCheckpoint(string checkpointFilePath, ILogger<IngestionCheckpoint> logger)
    {
        if (string.IsNullOrWhiteSpace(checkpointFilePath))
            throw new ArgumentException("Checkpoint file path cannot be empty.", nameof(checkpointFilePath));

        _checkpointFilePath = checkpointFilePath;
        _logger = logger;
        _state = new CheckpointState();
    }

    /// <summary>The index of the last file committed to Neo4j (0-based, inclusive).</summary>
    public int LastCommittedIndex => _state.LastCommittedIndex;

    /// <summary>Set of file paths that were skipped due to analysis warnings in previous runs.</summary>
    public IReadOnlySet<string> FailedFilePaths => _state.FailedFiles;

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
    /// If no checkpoint file exists, initialises to zero.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_checkpointFilePath))
        {
            _logger.LogInformation("No checkpoint found at '{Path}'. Starting from index 0.", _checkpointFilePath);
            _state = new CheckpointState();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_checkpointFilePath, cancellationToken);
            _state = JsonSerializer.Deserialize<CheckpointState>(json, SerializerOptions)
                     ?? new CheckpointState();

            _logger.LogInformation(
                "Checkpoint loaded. Resuming from index {Index} (total processed: {Total}, known failures: {Failures}).",
                _state.LastCommittedIndex + 1, _state.TotalProcessed, _state.FailedFiles.Count);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Checkpoint file corrupted at '{Path}'. Starting from index 0.", _checkpointFilePath);
            _state = new CheckpointState();
        }
    }

    /// <summary>
    /// Commits progress after a successful batch, persisting atomically.
    /// Any pending failures recorded via <see cref="RecordFailure"/> are merged
    /// into the checkpoint and cleared from the pending set.
    /// </summary>
    public async Task CommitAsync(int lastCommittedIndex, int totalProcessed, CancellationToken cancellationToken = default)
    {
        var mergedFailures = new HashSet<string>(_state.FailedFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var f in _pendingFailures)
            mergedFailures.Add(f);
        foreach (var f in _pendingClears)
            mergedFailures.Remove(f);

        _pendingFailures.Clear();
        _pendingClears.Clear();

        _state = new CheckpointState
        {
            LastCommittedIndex = lastCommittedIndex,
            TotalProcessed = totalProcessed,
            LastCommittedUtc = DateTime.UtcNow,
            FailedFiles = mergedFailures
        };

        var json = JsonSerializer.Serialize(_state, SerializerOptions);
        var tempPath = _checkpointFilePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _checkpointFilePath, overwrite: true);

        _logger.LogDebug(
            "Checkpoint committed: index={Index}, total={Total}, failures={Failures}.",
            lastCommittedIndex, totalProcessed, _state.FailedFiles.Count);
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
        _pendingFailures.Clear();
        _pendingClears.Clear();
    }

    private sealed class CheckpointState
    {
        public int LastCommittedIndex { get; init; } = -1;
        public int TotalProcessed { get; init; }
        public DateTime LastCommittedUtc { get; init; }

        [JsonConverter(typeof(HashSetStringConverter))]
        public HashSet<string> FailedFiles { get; init; } = [];
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
