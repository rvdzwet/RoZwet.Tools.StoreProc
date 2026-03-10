# ACTIVE CONTEXT: RoZwet.Tools.StoreProc

## Current Operation
**v1.4.0 — Streaming Reasoning Output + Retry Durability Fix**

## State
- Phase: Production Ready
- Last Updated: 2026-03-10
- Status: BUILD SUCCEEDED — Zero errors, zero warnings

## Completed Steps — v1.1.0 Migration
- [x] appsettings.json split into `Ai:Chat` (Gemini 3 Flash) + `Ai:Embedding` (voyage-4-large) + `Ai:Agent` sections
- [x] Program.cs — dual `OpenAIClient` registration (separate endpoints per provider)
- [x] Neo4jIndexInitializer — embedding dimensions config-driven via `Ai:Embedding:Dimensions`
- [x] StoredProcedure.ApplyEmbedding — removed hardcoded 1024 constraint; accepts any non-empty vector
- [x] INeo4jRepository — 3 new methods: GetProcedureSqlAsync, ExpandCallChainAsync, GetTableUsageAsync
- [x] Neo4jRepository — Cypher implementations for 3 new methods
- [x] GraphQueryTools.cs (NEW) — 4 AIFunction tool definitions wrapping Neo4j graph
- [x] ChatService — full agentic tool-calling loop (MaxToolRounds=5, AIFunctionArguments, correct API surface)
- [x] RoZwet.Tools.StoreProc.csproj — probe/ directory excluded from main compilation glob

## Completed Steps — v1.2.0 SQL Parse Resilience
- [x] `LegacySqlPreprocessor` — added 5 new Sybase patterns: `*=` (left outer join), `=*` (right outer join), `FOR READ ONLY` / `FOR BROWSE` cursor options, `SET SHOWPLAN ON|OFF`, `SET PROCID ON|OFF`
- [x] `AiSqlRepairAgent` (NEW) — AI-powered fallback: sends failed SQL + parse errors to Gemini, strips markdown fences, retries parsing on repaired output; original SQL always preserved for graph storage
- [x] `SqlAnalysisAgent` — injected `AiSqlRepairAgent`; `ParseProcedure` made async; two-tier logic: Tier-1 preprocessor → Tier-2 AI repair on parse failure; warnings only emitted when AI also fails
- [x] `Program.cs` — registered `AiSqlRepairAgent` as singleton; added `using RoZwet.Tools.StoreProc.Infrastructure.Parsing`

## Completed Steps — v1.3.0 Polly Resilience + Checkpoint Warning Tracking
- [x] `Polly` v8.5.2 — added to csproj
- [x] `AiResilienceOptions` (NEW) — config-bound record: `MaxRetries` (6), `BaseDelaySeconds` (2.0), `MaxDelaySeconds` (60.0)
- [x] `AiResiliencePipelineFactory` (NEW) — static factory; `ResiliencePipeline<T>` with exponential backoff + full jitter; retries on `ClientResultException` (429) and `HttpRequestException` only; logs structured warning on each retry attempt
- [x] `ChatProvider` — constructor accepts `AiResilienceOptions`; `_pipeline` (`ResiliencePipeline<string>`) wraps `CompleteAsync` body; manual error handling removed
- [x] `EmbeddingProvider` — constructor accepts `AiResilienceOptions`; `_pipeline` (`ResiliencePipeline<float[]>`) wraps `GenerateAsync` body; manual `ClientResultException` catch removed
- [x] `appsettings.json` — added `"Ai": { ..., "Resilience": { "MaxRetries": 6, "BaseDelaySeconds": 2.0, "MaxDelaySeconds": 60.0 } }`
- [x] `Program.cs` — `AiResilienceOptions` registered as singleton, bound from `Ai:Resilience`; injected into both provider constructors via DI
- [x] `IngestionCheckpoint` — `CheckpointState.FailedFiles` (`HashSet<string>`) persisted as sorted JSON array; `RecordFailure(filePath)` accumulates pending failures; `CommitAsync` merges + flushes pending failures atomically; `LoadAsync` restored (was commented out); `FailedFilePaths` read property exposed
- [x] `PipelineOrchestrator` — calls `_checkpoint.RecordFailure(filePath)` when `AnalyzeAsync` returns null; commit boundary now always calls `CommitAsync` (even when batchBuffer is empty) to ensure failures are durably flushed on every batch boundary

## AI Provider Configuration (v1.1.0)
| Role | Provider | Model | Endpoint |
|---|---|---|---|
| Chat / Agent | Google Gemini | `gemini-3-flash-preview` | `https://generativelanguage.googleapis.com/v1beta/openai/` |
| Embeddings | Voyage AI | `voyage-4-large` | `https://api.voyageai.com/v1` |

## Resilience Configuration (v1.3.0)
| Attempt | Approx. Wait |
|---|---|
| 1 | ~2s + jitter |
| 2 | ~4s + jitter |
| 3 | ~8s + jitter |
| 4 | ~16s + jitter |
| 5 | ~32s + jitter |
| 6 | ~60s (capped) |
| 7th failure | propagates as original exception |

Only retries on: `ClientResultException` with `Status == 429` and `HttpRequestException`.
All other exceptions (400, 401, 500) surface immediately — no retry.

## Checkpoint JSON Shape (v1.3.0)
```json
{
  "LastCommittedIndex": 1199,
  "TotalProcessed": 1190,
  "LastCommittedUtc": "2026-03-10T14:00:00Z",
  "FailedFiles": [
    "C:\\SP\\sp\\proc_bad_syntax.sp",
    "C:\\SP\\sp\\proc_legacy_cursor.sp"
  ]
}
```

## Package Versions (Resolved)
| Package | Version |
|---|---|
| Microsoft.Extensions.Hosting | 9.0.2 |
| Microsoft.Extensions.AI | 9.5.0 |
| Microsoft.Extensions.AI.OpenAI | 10.3.0 |
| Microsoft.SqlServer.TransactSql.ScriptDom | 170.3.0 |
| Neo4j.Driver | 5.28.0 |
| OpenAI | 2.8.0 |
| Polly | 8.5.2 |

## Next Steps (Operator Actions Required)
1. Set real API keys in `appsettings.json`:
   - `Ai:Chat:ApiKey` → Gemini API key from Google AI Studio
   - `Ai:Embedding:ApiKey` → Voyage AI API key from voyageai.com
   - `Neo4j:Password` → your Neo4j instance password
2. Place 5,500 `.sql` files in `Ingestion:SqlSourceDirectory` (default: `./sql`)
3. Run ingestion: `dotnet run -- --ingest`
4. Start agentic chat: `dotnet run -- --chat`
5. After ingestion, inspect checkpoint JSON for `FailedFiles` to identify procedures requiring manual review

## Risk Register
| Risk | Status |
|---|---|
| voyage-4-large dimension mismatch | MITIGATED — dims read from config, propagated to index + EmbeddingGenerator |
| Sybase-specific SQL syntax failures | MITIGATED — Tier-1 regex preprocessor + Tier-2 AI repair; warning only on full failure |
| Neo4j vector index not available | MITIGATED — idempotent init with clear error logging |
| Interruption during 5,500-file run | MITIGATED — checkpoint.json ensures resume from last committed batch |
| Agentic loop runaway | MITIGATED — MaxToolRounds=5 cap, tool exceptions caught and returned as error content |
| AI provider rate-limiting (429) | MITIGATED — Polly retry pipeline: 6 attempts, exponential backoff + jitter, 2s–60s delay window |
| Failed files silently lost from checkpoint | MITIGATED — FailedFiles array in checkpoint.json; committed on every batch boundary |
