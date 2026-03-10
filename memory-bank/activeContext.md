# Active Context — v1.6.0

## Current State
All planned features through v1.6.0 are implemented and building clean (0 errors, 0 warnings).

## Completed Milestones

### v1.4.0 — Streaming Reasoning Output
- `ChatService.AskStreamingAsync` added; tool-call rounds use `GetResponseAsync`, final answer streams via `GetStreamingResponseAsync`
- `TextReasoningContent` → `[Thinking]` (DarkGray), `TextContent` → `Assistant:` (default color)
- `Microsoft.Extensions.AI` upgraded 9.5.0 → 10.3.0 for `TextReasoningContent`
- `Program.cs` `RunChatAsync` uses streaming callback with per-chunk flush

### v1.5.0 — Tier-1 Preprocessor + Background Retry Commit
- `LegacySqlPreprocessor`: added `RAISERROR` all-variant pattern + Sybase multi-assign `SET→SELECT`
- `PipelineOrchestrator`: retry pass now commits per-recovery to Neo4j (was missing)
- `PipelineOrchestrator`: `IConfiguration` injected for `Ingestion:MaxConcurrency`

### v1.6.0 — Fire-and-Forget Background AI Repair
- `SqlAnalysisAgent` refactored into three focused methods:
  - `TryTier1Async` — deterministic parse only (no AI), fast
  - `ApplyEmbeddingAsync` — embedding only, called after successful Tier-1
  - `RepairAndCompleteAsync` — full Tier-2 AI repair loop + embedding, designed for background dispatch
- `PipelineOrchestrator` rewritten with two-speed architecture:
  - **Fast path**: sequential Tier-1 parsing; successful files batch-committed immediately; checkpoint advances file-by-file
  - **Background path**: files failing Tier-1 recorded as failures in checkpoint (durable), then dispatched via `_ = RepairInBackgroundAsync(...)` (fire-and-forget)
  - `_checkpointLock` (`SemaphoreSlim(1,1)`) serialises all `CommitAsync` calls from main loop + concurrent background tasks
  - `_totalProcessed` incremented via `Interlocked.Increment` in background tasks
  - `RetryPreviouslyFailedFilesAsync` uses both Tier-1 and awaited Tier-2 (sequential, already slow path)

## Active Configuration (appsettings.json)
```json
"Ingestion": {
  "SqlSourceDirectory": "C:\\SP\\sp",
  "CheckpointFile": "./checkpoint-1454.json",
  "BatchSize": 1,
  "MaxConcurrency": 4
}
```
Note: `MaxConcurrency` is no longer used by the main scan loop (replaced by fire-and-forget). It is still read for potential future use.

## Known Failures (FailedFiles in checkpoint)
Background repair tasks are fire-and-forget — files failing AI repair remain in `FailedFiles` and will be retried on next run.

## Next Potential Actions
- Monitor `[BG-REPAIR]` log entries during ingestion for repair success rate
- Tune `BatchSize` for throughput vs. commit granularity
- Consider bounded concurrency for background repair tasks if API rate limits are hit (add `SemaphoreSlim` around `RepairAndCompleteAsync` calls)
