# Active Context — v1.8.0

## Current State
All planned features through v1.8.0 are implemented and building clean (0 errors, 0 warnings).

## Completed Milestones

### v1.8.0 — Multilingual Query Support
- **Problem:** Neo4j vector embeddings are generated from Dutch text. Non-Dutch queries produce
  embedding vectors that are semantically misaligned with the stored Dutch vectors, causing poor
  search recall.
- **Solution:** Query-translation layer injected into `HybridSearchService` — zero re-indexing required.
- Changes:
  - `HybridSearchService`: injected `IChatClient`; added `TranslateToDataLanguageAsync` which calls
    the LLM with a strict single-sentence system prompt to translate the query to Dutch before
    `EmbeddingProvider.GenerateAsync`. Falls back to the original query if translation fails.
  - `ChatService.SystemPrompt`: added final rule instructing the LLM to always reply in the
    user's original language while keeping procedure/table identifiers unchanged.
- Both the interactive `chat` path and the MCP `SearchProcedures` tool path benefit automatically.


### v1.7.0 — MCP HTTP Server
- Replaced `ModelContextProtocol` with `ModelContextProtocol.AspNetCore` 1.1.0.
- Added `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (provides Hosting, Configuration.Json, Logging.Console transitively — explicit package refs removed).
- New file: `src/Application/McpServer/StoreProcTools.cs`
  - `[McpServerToolType]` class; DI-injected via `HybridSearchService` + `INeo4jRepository`.
  - Four tools: `SearchProcedures`, `GetProcedureSql`, `ExpandCallChain`, `GetTableUsage`.
  - All `[McpServerTool]` + `[Description]` attributes for schema discovery by MCP clients.
- `Program.cs` updated:
  - New `--mcp` mode; `RunMcpAsync()` uses `WebApplication.CreateBuilder()`.
  - `WithHttpTransport()` + `app.MapMcp()` + `app.RunAsync(url)`.
  - URL configured via `appsettings.json` key `Mcp:Url` (default `http://localhost:3001`).
- `appsettings.json`: added `"Mcp": { "Url": "http://localhost:3001" }`.
- `cline_mcp_settings.json`: added `storedproc-graphrag` with `"url": "http://localhost:3001/mcp"` + all four tools auto-approved.
- Version bumped to 1.7.0.

### Cline Integration
The `cline_mcp_settings.json` has been updated automatically. To use the tools:
1. Start the server: `dotnet run --project "c:\Users\roman\source\repos\RoZwet.Tools.StoreProc\RoZwet.Tools.StoreProc.csproj" -- --mcp`
2. Cline will connect automatically via `http://localhost:3001/mcp`.

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

### v1.5.0 — Tier-1 Preprocessor + Background Retry Commit
- `LegacySqlPreprocessor`: added `RAISERROR` all-variant pattern + Sybase multi-assign `SET→SELECT`
- `PipelineOrchestrator`: retry pass now commits per-recovery to Neo4j (was missing)
- `PipelineOrchestrator`: `IConfiguration` injected for `Ingestion:MaxConcurrency`

### v1.4.0 — Streaming Reasoning Output
- `ChatService.AskStreamingAsync` added; tool-call rounds use `GetResponseAsync`, final answer streams via `GetStreamingResponseAsync`
- `TextReasoningContent` → `[Thinking]` (DarkGray), `TextContent` → `Assistant:` (default color)
- `Microsoft.Extensions.AI` upgraded 9.5.0 → 10.3.0 for `TextReasoningContent`
- `Program.cs` `RunChatAsync` uses streaming callback with per-chunk flush

## Active Configuration (appsettings.json)
```json
"Ingestion": {
  "SqlSourceDirectory": "C:\\SP\\sp",
  "CheckpointFile": "./checkpoint.json",
  "BatchSize": 50,
  "MaxConcurrency": 4
}
```

## Known Failures (FailedFiles in checkpoint)
Background repair tasks are fire-and-forget — files failing AI repair remain in `FailedFiles` and will be retried on next run.

## Next Potential Actions
- Hook `storedproc-graphrag` into Cline MCP settings and start building new services from the graph.
- Monitor `[BG-REPAIR]` log entries during ingestion for repair success rate.
- Tune `BatchSize` for throughput vs. commit granularity.
- Consider bounded concurrency for background repair tasks if API rate limits are hit.
