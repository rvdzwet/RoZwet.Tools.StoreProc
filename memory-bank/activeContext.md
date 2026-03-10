# ACTIVE CONTEXT: RoZwet.Tools.StoreProc

## Current Operation
**v1.2.0 — Two-Tier SQL Parse Resilience (AI-Assisted Repair)**

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

## AI Provider Configuration (v1.1.0)
| Role | Provider | Model | Endpoint |
|---|---|---|---|
| Chat / Agent | Google Gemini | `gemini-3-flash-preview` | `https://generativelanguage.googleapis.com/v1beta/openai/` |
| Embeddings | Voyage AI | `voyage-4-large` | `https://api.voyageai.com/v1` |

## Microsoft.Extensions.AI v9.5.0 — Verified API Surface
| Element | Confirmed Signature |
|---|---|
| `ChatResponse.Messages` | `IList<ChatMessage>` (plural) |
| `FunctionResultContent` ctor | `(string callId, object result)` — 2 args |
| `FunctionCallContent.Arguments` | `IDictionary<string, object>` |
| `AIFunction.Name` | Direct property — no `.Metadata` wrapper |
| `AIFunction.InvokeAsync` | `(AIFunctionArguments args, CancellationToken ct)` |
| `AIFunctionArguments` ctor | `(IDictionary<string, object>)` |

## Agentic Tool Definitions (GraphQueryTools)
| Tool Name | Description |
|---|---|
| `search_procedures` | Hybrid vector+graph search for semantically related procedures |
| `get_procedure_sql` | Returns full SQL body of a named procedure |
| `expand_call_chain` | Traverses CALLS edges up to depth N (clamped 1–5) |
| `get_table_usage` | Returns all procedures referencing a named table |

## Package Versions (Resolved)
| Package | Version |
|---|---|
| Microsoft.Extensions.Hosting | 9.0.2 |
| Microsoft.Extensions.AI | 9.5.0 |
| Microsoft.Extensions.AI.OpenAI | 10.3.0 |
| Microsoft.SqlServer.TransactSql.ScriptDom | 170.3.0 |
| Neo4j.Driver | 5.28.0 |
| OpenAI | 2.8.0 |

## Next Steps (Operator Actions Required)
1. Set real API keys in `appsettings.json`:
   - `Ai:Chat:ApiKey` → Gemini API key from Google AI Studio
   - `Ai:Embedding:ApiKey` → Voyage AI API key from voyageai.com
   - `Neo4j:Password` → your Neo4j instance password
2. Place 5,500 `.sql` files in `Ingestion:SqlSourceDirectory` (default: `./sql`)
3. Run ingestion: `dotnet run -- --ingest`
4. Start agentic chat: `dotnet run -- --chat`

## Risk Register
| Risk | Status |
|---|---|
| voyage-4-large dimension mismatch | MITIGATED — dims read from config, propagated to index + EmbeddingGenerator |
| Sybase-specific SQL syntax failures | MITIGATED — Tier-1 regex preprocessor + Tier-2 AI repair; warning only on full failure |
| Neo4j vector index not available | MITIGATED — idempotent init with clear error logging |
| Interruption during 5,500-file run | MITIGATED — checkpoint.json ensures resume from last committed batch |
| Agentic loop runaway | MITIGATED — MaxToolRounds=5 cap, tool exceptions caught and returned as error content |
