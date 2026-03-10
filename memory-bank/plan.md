# ARCHITECTURAL PLAN: RoZwet.Tools.StoreProc — GraphRAG Stored Procedure Intelligence System

## Version: 1.9.0 | Status: ACTIVE

---

## PHASE 6 — AG-UI Web Chat Interface (v1.9.0)

### Overview
Convert the console `--chat` mode to a browser-based AG-UI chat endpoint with real-time SSE streaming, a password-gated password-gated UI, and the Microsoft Agent Framework.

### Project Changes

| File | Change |
|---|---|
| `RoZwet.Tools.StoreProc.csproj` | SDK → `Microsoft.NET.Sdk.Web`; added `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore 1.0.0-preview.260304.1`; ME.AI upgraded to `10.3.0` |
| `Program.cs` | `--chat`/`RunChatAsync` removed; `--web`/no-args → `RunWebAsync`; `AddAGUI()` + `MapAGUI("/chat", agent)`; `/api/auth` with SHA-256 cookie token; auth middleware protects `/chat` |
| `src/Application/Services/ChatService.cs` | `SystemPrompt` promoted to `internal const` |
| `appsettings.json` | `Web:Url` + `Web:AccessPassword` added |
| `wwwroot/index.html` | Self-contained AG-UI SSE client; clean palette; Dutch UI; password overlay |
| `Properties/launchSettings.json` | Three profiles: `Web (AG-UI)`, `MCP`, `Ingest` |

### AG-UI Endpoint Design
```
POST /chat  (requires __rzw_session cookie)
  ← RunAgentInput JSON: { threadId, runId, messages[], tools:[], context:[], state:null }
  → text/event-stream SSE:
      data: {"type":"RUN_STARTED",...}
      data: {"type":"TEXT_MESSAGE_START",...}
      data: {"type":"TEXT_MESSAGE_CONTENT","delta":"Hello"}
      ...
      data: {"type":"RUN_FINISHED",...}
```

### Auth Flow
```
POST /api/auth  { "password": "secret" }
  → if SHA256(submitted) == SHA256(configured):
      Set-Cookie: __rzw_session=<token>; HttpOnly; SameSite=Strict
      200 OK
  → else: 401 Unauthorized
```

### Mode Dispatch Table (v1.9.0)
| CLI arg | Behaviour |
|---|---|
| *(none)* | Starts AG-UI web server on `Web:Url` |
| `--web` | Starts AG-UI web server on `Web:Url` |
| `--ingest` | Runs ingestion pipeline |
| `--mcp` | Starts MCP HTTP server on `Mcp:Url` |


---

## 1. MISSION STATEMENT

Process 5,500 Sybase stored procedures into a Neo4j GraphRAG system, enabling semantic hybrid search and real-time agentic chat-driven intelligence over the procedural dependency graph.

---

## 2. SYSTEM ARCHITECTURE

```
┌─────────────────────────────────────────────────────────────────┐
│                    RoZwet.Tools.StoreProc                        │
│                    (.NET 10 Console App)                         │
├─────────────────────────────────────────────────────────────────┤
│  ENTRY POINT: Program.cs (DI Bootstrap + Mode Selector)          │
│    ├── Mode: --ingest  → PipelineOrchestrator                    │
│    └── Mode: --chat    → Agentic ChatLoop                        │
├─────────────────────────────────────────────────────────────────┤
│  DOMAIN LAYER (src/Domain/)                                      │
│    ├── StoredProcedure.cs       — Aggregate root                 │
│    ├── ProcedureCall.cs         — Value object                   │
│    └── TableDependency.cs       — Value object                   │
├─────────────────────────────────────────────────────────────────┤
│  APPLICATION LAYER (src/Application/)                            │
│    ├── Agents/                                                   │
│    │   ├── SqlAnalysisAgent.cs  — MAF Agent: parse + embed       │
│    │   └── GraphQueryTools.cs   — 4 AIFunction tool definitions  │
│    ├── Contracts/                                                │
│    │   └── INeo4jRepository.cs  — Port: 7 methods                │
│    ├── Pipeline/                                                 │
│    │   ├── PipelineOrchestrator.cs — Durable run coordinator     │
│    │   └── IngestionCheckpoint.cs  — Resume-state tracker        │
│    └── Services/                                                 │
│        ├── HybridSearchService.cs — Vector + Graph traversal     │
│        └── ChatService.cs         — Agentic tool-calling loop    │
├─────────────────────────────────────────────────────────────────┤
│  INFRASTRUCTURE LAYER (src/Infrastructure/)                      │
│    ├── Parsing/                                                  │
│    │   ├── TsqlFragmentVisitor.cs — ScriptDom AST walker         │
│    │   ├── LegacySqlPreprocessor.cs — Sybase regex normaliser    │
│    │   └── AiSqlRepairAgent.cs — AI fallback for parse failures  │
│    ├── Neo4j/                                                    │
│    │   ├── Neo4jRepository.cs   — Implements 7 port methods      │
│    │   └── Neo4jIndexInitializer.cs — Config-driven vector index │
│    └── Ai/                                                       │
│        ├── EmbeddingProvider.cs  — IEmbeddingGenerator wrapper   │
│        └── ChatProvider.cs       — IChatClient wrapper           │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. AI PROVIDER CONFIGURATION (v1.1.0)

| Role | Provider | Model | Endpoint |
|---|---|---|---|
| **Chat / Agent** | Google Gemini | `gemini-3-flash-preview` | `https://generativelanguage.googleapis.com/v1beta/openai/` |
| **Embeddings** | Voyage AI | `voyage-4-large` | `https://api.voyageai.com/v1` |

Both providers are accessed via the OpenAI-compatible REST protocol.
No additional NuGet packages are required — `Microsoft.Extensions.AI.OpenAI` handles both.

---

## 4. PHASE BREAKDOWN

### Phase 1 — Infrastructure Setup
- Target framework: net10.0
- DI container: Microsoft.Extensions.Hosting
- Neo4j vector index: Cosine similarity, `Ai:Embedding:Dimensions` (default 1024), on Procedure nodes
- IEmbeddingGenerator: Voyage AI voyage-4-large via OpenAI-compatible adapter
- IChatClient: Gemini 3 Flash via Google's OpenAI-compatible endpoint

### Phase 2 — SQL Analysis Agent (v1.2.0 — Two-Tier Parse Resilience)
- **Tier 1 — `LegacySqlPreprocessor`** (deterministic, zero API cost):
  - RAISERROR legacy format → `RAISERROR(msg, 16, 1)`
  - `SET ARITHABORT NUMERIC_TRUNCATION` → stripped
  - Double-quoted strings → single-quoted
  - `*=` / `=*` (Sybase outer joins) → `=`
  - `FOR READ ONLY` / `FOR BROWSE` cursor options → stripped
  - `SET SHOWPLAN ON|OFF` / `SET PROCID ON|OFF` → stripped
- **Tier 2 — `AiSqlRepairAgent`** (AI fallback, only fires on Tier-1 failure):
  - Sends preprocessed SQL + TSql160Parser errors to Gemini
  - Retries parsing on AI-repaired SQL
  - Original SQL always preserved verbatim for graph storage
  - Warning log only emitted when both tiers fail
- TSqlFragmentVisitor walks AST for EXEC calls and FROM/JOIN table refs
- Produces: List<ProcedureCall>, List<TableDependency>
- Generates: float[] embedding via IEmbeddingGenerator
- Output: enriched StoredProcedure aggregate

### Phase 3 — Graph Store
- MERGE on procedure name (idempotent upsert)
- [:CALLS] relationships between procedures
- [:READS_FROM] / [:WRITES_TO] to table nodes
- Batches of 50 per Bolt transaction

### Phase 4 — Durable Pipeline
- checkpoint.json: tracks last committed batch index
- PipelineOrchestrator: discover → load checkpoint → process → upsert → save checkpoint
- Safe to CTRL+C and restart — zero re-processing of committed batches

### Phase 5 — Agentic GraphRAG Chat (v1.1.0)
- `GraphQueryTools` exposes 4 `AIFunction` definitions:
  - `search_procedures(query)` → hybrid vector+graph search
  - `get_procedure_sql(name)` → full SQL body lookup
  - `expand_call_chain(name, depth)` → transitive CALLS traversal (depth 1–5)
  - `get_table_usage(tableName)` → all procedures touching a table
- `ChatService` runs an agentic tool-calling loop:
  - System prompt instructs strategy: search → inspect → expand → answer
  - Loop: `GetResponseAsync` → check tool calls → `AIFunctionArguments(dict)` → `InvokeAsync` → append results
  - Capped at `Ai:Agent:MaxToolRounds` (default 5)
  - After cap exhaustion: final pass with empty `ChatOptions` to force text answer

---

## 5. KEY DESIGN DECISIONS

| Decision | Rationale |
|---|---|
| Checkpoint as atomic JSON file | Zero external dependency; temp-file swap guarantees no corrupt state |
| Batch size = 50 | Balances Bolt transaction overhead vs memory for 5,500 procedures |
| Vector index on Procedure nodes only | Tables are structural; only procedure semantics need embedding |
| Agentic loop with MaxToolRounds cap | Prevents runaway multi-hop reasoning; 5 rounds sufficient for complex dependency chains |
| AIFunction lambdas vs method groups | Explicit lambda captures prevent delegate type ambiguity |
| `$$"""..."""` raw strings for Cypher with dynamic values | Single `{`/`}` are literal Cypher braces; `{{expr}}` safely interpolates validated ints |
| Voyage AI over Codestral-Embed | voyage-4-large: superior code+schema semantic understanding |
| Gemini 3 Flash over Codestral | State-of-the-art reasoning; tool-calling native support; OpenAI-compatible |
| Query-translation before embedding (v1.8.0) | Neo4j embeddings are Dutch; translating any query to Dutch via LLM before `EmbeddingProvider.GenerateAsync` yields correct cosine similarity without re-indexing. Fallback to original query on translation failure. LLM replies in user's original language via system-prompt rule. |

---

## 6. NEO4J GRAPH SCHEMA

### Nodes
- `(:Procedure {name, schema, sql, embedding: float[N]})`  — N from `Ai:Embedding:Dimensions`
- `(:Table {name, schema})`

### Relationships
- `(:Procedure)-[:CALLS]->(:Procedure)`
- `(:Procedure)-[:READS_FROM]->(:Table)`
- `(:Procedure)-[:WRITES_TO]->(:Table)`

### Vector Index
```cypher
CREATE VECTOR INDEX procedure_embeddings IF NOT EXISTS
FOR (p:Procedure) ON (p.embedding)
OPTIONS { indexConfig: { `vector.dimensions`: 1024, `vector.similarity_function`: 'cosine' } }
```

---

## 7. CONFIGURATION SCHEMA (appsettings.json — v1.1.0)

```json
{
  "Neo4j": { "Uri": "bolt://localhost:7687", "Username": "neo4j", "Password": "<secret>" },
  "Ai": {
    "Chat": {
      "Endpoint": "https://generativelanguage.googleapis.com/v1beta/openai/",
      "ApiKey": "<gemini-api-key>",
      "Model": "gemini-3-flash-preview"
    },
    "Embedding": {
      "Endpoint": "https://api.voyageai.com/v1",
      "ApiKey": "<voyage-api-key>",
      "Model": "voyage-4-large",
      "Dimensions": 1024
    },
    "Agent": { "MaxToolRounds": 5 }
  },
  "Ingestion": { "SqlSourceDirectory": "./sql", "CheckpointFile": "./checkpoint.json", "BatchSize": 50 }
}
```

---

## 8. MCP SERVER (v1.7.0)

### Transport
HTTP (JSON-RPC 2.0 over HTTP/SSE via `ModelContextProtocol.AspNetCore`) — network-accessible, compatible with Cline and any MCP HTTP client.

### Activation
```
RoZwet.Tools.StoreProc --mcp
```
Listens on `Mcp:Url` from `appsettings.json` (default `http://localhost:3001`).
MCP endpoint: `http://localhost:3001/mcp`

### Registered Tools (`src/Application/McpServer/StoreProcTools.cs`)

| MCP Tool Name | Input Parameters | Delegates To |
|---|---|---|
| `SearchProcedures` | `query: string` | `HybridSearchService.SearchAsync` |
| `GetProcedureSql` | `name: string` | `INeo4jRepository.GetProcedureSqlAsync` |
| `ExpandCallChain` | `name: string`, `depth: int (1–5)` | `INeo4jRepository.ExpandCallChainAsync` |
| `GetTableUsage` | `tableName: string` | `INeo4jRepository.GetTableUsageAsync` |

### Cline Integration (`cline_mcp_settings.json` — already configured)
```json
{
  "mcpServers": {
    "storedproc-graphrag": {
      "url": "http://localhost:3001/mcp",
      "disabled": false,
      "autoApprove": ["SearchProcedures", "GetProcedureSql", "ExpandCallChain", "GetTableUsage"]
    }
  }
}
```

### Design Notes
- Package: `ModelContextProtocol.AspNetCore` 1.1.0; `FrameworkReference Microsoft.AspNetCore.App` replaces the three explicit hosting packages.
- `[McpServerToolType]` / `[McpServerTool]` attributes drive discovery via `WithToolsFromAssembly`.
- `StoreProcTools` is an `internal sealed` DI-injected class — constructor receives `HybridSearchService` + `INeo4jRepository`.
- `RunMcpAsync()` uses `WebApplication.CreateBuilder()` → `WithHttpTransport()` → `app.MapMcp()` → `app.RunAsync(url)`.
- `IChatClient` and `ChatService` are registered but never resolved in MCP mode — factories are lazy, no Gemini calls occur.

---

## 9. HYBRID SEARCH CYPHER


```cypher
-- Step A: Vector similarity search
CALL db.index.vector.queryNodes('procedure_embeddings', 3, $embedding)
YIELD node AS p, score
RETURN p.name, p.sql, score

-- Step B: 1-hop graph expansion
MATCH (p:Procedure)-[:CALLS|READS_FROM]->(related)
WHERE p.name IN $topNames
RETURN DISTINCT related.name AS relatedName

-- Step C (agentic): N-hop call chain
MATCH (root:Procedure {name: $name})-[:CALLS*1..3]->(callee:Procedure)
RETURN DISTINCT callee.name AS calleeName ORDER BY calleeName

-- Step D (agentic): Table usage
MATCH (p:Procedure)-[:READS_FROM|WRITES_TO]->(t:Table {name: $tableName})
RETURN DISTINCT p.name AS procName ORDER BY procName
```
