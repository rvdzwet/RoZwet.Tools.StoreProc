using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RoZwet.Tools.StoreProc.Application.Pipeline;

/// <summary>
/// Persists and recovers ingestion progress to a local JSON file.
/// Atomic write via temp-file rename guarantees no corrupt state on crash.
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

    /// <summary>
    /// Loads existing checkpoint state from disk.
    /// If no checkpoint file exists, initializes to zero.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        //if (!File.Exists(_checkpointFilePath))
        //{
        //    _logger.LogInformation("No checkpoint found at '{Path}'. Starting from index 0.", _checkpointFilePath);
        //    _state = new CheckpointState();
        //    return;
        //}

        //try
        //{
        //    var json = await File.ReadAllTextAsync(_checkpointFilePath, cancellationToken);
        //    _state = JsonSerializer.Deserialize<CheckpointState>(json, SerializerOptions)
        //             ?? new CheckpointState();

        //    _logger.LogInformation(
        //        "Checkpoint loaded. Resuming from index {Index} (total processed: {Total}).",
        //        _state.LastCommittedIndex + 1, _state.TotalProcessed);
        //}
        //catch (JsonException ex)
        //{
        //    _logger.LogError(ex, "Checkpoint file corrupted at '{Path}'. Starting from index 0.", _checkpointFilePath);
        //    _state = new CheckpointState();
        //}
    }

    /// <summary>
    /// Commits progress after a successful batch, persisting atomically.
    /// </summary>
    public async Task CommitAsync(int lastCommittedIndex, int totalProcessed, CancellationToken cancellationToken = default)
    {
        _state = new CheckpointState
        {
            LastCommittedIndex = lastCommittedIndex,
            TotalProcessed = totalProcessed,
            LastCommittedUtc = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(_state, SerializerOptions);
        var tempPath = _checkpointFilePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _checkpointFilePath, overwrite: true);

        _logger.LogDebug(
            "Checkpoint committed: index={Index}, total={Total}.",
            lastCommittedIndex, totalProcessed);
    }

    /// <summary>
    /// Resets the checkpoint file from disk.
    /// </summary>
    public void Delete()
    {
        if (File.Exists(_checkpointFilePath))
        {
            File.Delete(_checkpointFilePath);
            _logger.LogInformation("Checkpoint file deleted at '{Path}'.", _checkpointFilePath);
        }

        _state = new CheckpointState();
    }

    private sealed class CheckpointState
    {
        public int LastCommittedIndex { get; init; } = -1;
        public int TotalProcessed { get; init; }
        public DateTime LastCommittedUtc { get; init; }
    }
}
