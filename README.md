# RoZwet.Tools.StoreProc

> A .NET 10 GraphRAG tool that ingests Sybase stored procedures into Neo4j and provides an AG-UI web chat interface and an MCP server — both powered by **Gemini Flash** and **Voyage AI** embeddings.

---

## Overview

`RoZwet.Tools.StoreProc` processes `.sp` files containing Sybase stored-procedure SQL through a two-tier resilient parse pipeline, enriches each procedure with a 1024-dimensional semantic vector embedding, and persists the result as a rich dependency graph in Neo4j.

The ingestion pipeline is **incremental by design** — each daily run computes a SHA-256 hash of every source file and skips any procedure whose content has not changed since the last run. Only new or modified procedures consume AI embedding credits.

Once ingested, the knowledge base is queryable through three surfaces:

| Surface | Command | Description |
|---|---|---|
| **AG-UI web chat** | `--web` (default) | Browser-based chat UI; protected by password-gated session cookie |
| **MCP server** | `--mcp` | HTTP Model Context Protocol server; any MCP client (Cline, Claude Desktop) can call the tools |
| **Ingestion pipeline** | `--ingest` | Incremental, durable ingestion with content-hash change detection |

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│  ENTRY POINT  Program.cs                                             │
│    ├── (default / --web)  →  AG-UI ASP.NET Core web server           │
│    ├── --ingest           →  PipelineOrchestrator (incremental)       │
│    └── --mcp              →  MCP HTTP server (ModelContextProtocol)   │
├─────────────────────────────┬────────────────────────────────────────┤
│  DOMAIN                     │  Clean-architecture aggregate root      │
│  • StoredProcedure          │  name, schema, sql, contentHash,        │
│                             │  embedding                              │
│  • ProcedureCall            │  value object — callee name + schema    │
│  • TableDependency          │  value object — table name + access type│
│  • ProcedureParameter       │  value object — name, dataType,         │
│                             │  isOutput, hasDefault                   │
├─────────────────────────────┴────────────────────────────────────────┤
│  APPLICATION                                                         │
│  • SqlAnalysisAgent       Two-tier parse + embedding agent;          │
│                           SHA-256 hash computation; parameter extract│
│  • PipelineOrchestrator   Incremental ingestion; hash-based skip     │
│  • IngestionCheckpoint    Hash-map checkpoint (path → SHA-256)       │
│  • HybridSearchService    Dutch-translation → vector → 1-hop expand  │
│  • ChatService            Agentic MaxToolRounds loop (non-web path)  │
│  • GraphQueryTools        7 AIFunction tools for the AG-UI agent     │
│  • StoreProcTools         7 McpServerTool definitions for MCP mode   │
├──────────────────────────────────────────────────────────────────────┤
│  INFRASTRUCTURE                                                      │
│  • LegacySqlPreprocessor  8-rule regex normaliser for Sybase syntax  │
│  • StoredProcedureVisitor ScriptDom AST → CALLS + table deps         │
│  • AiSqlRepairAgent       LLM-powered Tier-2 syntax repair (10 rnds) │
│  • EmbeddingProvider      Voyage AI via OpenAI-compat SDK + Polly    │
│  • ChatProvider           Gemini Flash via native SDK + Polly        │
│  • AiResiliencePipelineFactory  Exponential back-off + jitter (429) │
│  • Neo4jRepository        Bolt driver; parameterised Cypher; batched │
│  • Neo4jIndexInitializer  Idempotent schema + cosine vector index    │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Prerequisites

| Requirement | Version / Notes |
|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0 or later |
| [Neo4j](https://neo4j.com/download/) | 5.x — **vector search** plugin required |
| [Gemini API key](https://aistudio.google.com/app/apikey) | Free tier available via Google AI Studio |
| [Voyage AI API key](https://dash.voyageai.com/) | Required for `voyage-4-large` embeddings |

### Neo4j Setup

1. Install Neo4j 5.x (Desktop, Docker, or Aura).
2. Enable the **GenAI / Vector** plugin (required for `db.index.vector.queryNodes`).
3. Note your Bolt URI (default `bolt://localhost:7687`), username, and password.

The schema (unique constraints + cosine vector index) is bootstrapped automatically on first `--ingest` or `--web` startup — no manual Cypher required.

---

## Configuration

`appsettings.json` is excluded from version control. Copy `appsettings.template.json` and fill in your values:

```bash
cp appsettings.template.json appsettings.json
```

```json
{
  "Neo4j": {
    "Uri": "bolt://localhost:7687",
    "Username": "neo4j",
    "Password": "<your-neo4j-password>"
  },
  "Ai": {
    "Chat": {
      "ApiKey": "<your-gemini-api-key>",
      "Model": "gemini-3-flash-preview"
    },
    "Embedding": {
      "Endpoint": "https://api.voyageai.com/v1",
      "ApiKey": "<your-voyage-api-key>",
      "Model": "voyage-4-large",
      "Dimensions": 1024
    },
    "Agent": {
      "MaxToolRounds": 50
    },
    "Resilience": {
      "MaxRetries": 6,
      "BaseDelaySeconds": 2.0,
      "MaxDelaySeconds": 60.0
    }
  },
  "Ingestion": {
    "SqlSourceDirectory": "./sp",
    "CheckpointFile": "./checkpoint.json",
    "BatchSize": 50
  },
  "Mcp": {
    "Url": "http://localhost:3001"
  },
  "Web": {
    "Url": "http://localhost:5000",
    "AccessPassword": "<your-access-password>"
  }
}
```

Environment variables prefixed with `ROZWET_` override any key (e.g. `ROZWET_Neo4j__Password=secret`).

### Configuration Reference

| Key | Default | Description |
|---|---|---|
| `Neo4j:Uri` | `bolt://localhost:7687` | Bolt connection string |
| `Neo4j:Username` | `neo4j` | Neo4j username |
| `Neo4j:Password` | — | Neo4j password |
| `Ai:Chat:ApiKey` | — | Gemini API key |
| `Ai:Chat:Model` | `gemini-3-flash-preview` | Gemini model name |
| `Ai:Embedding:ApiKey` | — | Voyage AI API key |
| `Ai:Embedding:Model` | `voyage-4-large` | Embedding model |
| `Ai:Embedding:Dimensions` | `1024` | Vector dimensions — must match the Neo4j vector index |
| `Ai:Agent:MaxToolRounds` | `50` | Max tool-calling rounds per chat turn before forcing a final answer |
| `Ai:Resilience:MaxRetries` | `6` | Polly retry attempts on HTTP 429 / network failure |
| `Ai:Resilience:BaseDelaySeconds` | `2.0` | Exponential back-off base delay |
| `Ai:Resilience:MaxDelaySeconds` | `60.0` | Upper bound on retry delay |
| `Ingestion:SqlSourceDirectory` | `./sp` | Directory scanned recursively for `.sp` files |
| `Ingestion:CheckpointFile` | `./checkpoint.json` | Durable checkpoint path |
| `Ingestion:BatchSize` | `50` | Procedures per Neo4j write transaction |
| `Mcp:Url` | `http://localhost:3001` | Bind URL for the MCP HTTP server |
| `Web:Url` | `http://localhost:5000` | Bind URL for the AG-UI web server |
| `Web:AccessPassword` | — | Password required to access the web chat UI |

---

## Getting Started

### 1. Clone and restore

```bash
git clone https://github.com/rvdzwet/RoZwet.Tools.StoreProc.git
cd RoZwet.Tools.StoreProc
dotnet restore
```

### 2. Configure

```bash
cp appsettings.template.json appsettings.json
# Edit appsettings.json with your keys and credentials
```

### 3. Place your stored procedures

Drop all Sybase `.sp` files into the directory specified by `Ingestion:SqlSourceDirectory` (default `./sp`). Subdirectories are scanned recursively.

```
./sp/
  dbo.usp_GetOrders.sp
  dbo.usp_InsertOrder.sp
  reporting.usp_SalesSummary.sp
  ...
```

### 4. Run ingestion

```bash
dotnet run -- --ingest
```

The pipeline:

1. Discovers all `.sp` files recursively, sorted by path.
2. Loads the durable checkpoint — computes SHA-256 hashes and skips unchanged files.
3. Retries any previously-failed files (both Tier-1 and Tier-2 attempted on retry).
4. **Tier 1 (fast path):** `LegacySqlPreprocessor` normalises each file with 8 deterministic regex rules, then `TSql160Parser` parses the AST. Successful files are embedding-enriched and batched to Neo4j.
5. **Tier 2 (background repair):** Files that still have parse errors after Tier-1 are dispatched as fire-and-forget background tasks. `AiSqlRepairAgent` iteratively sends the SQL and parser errors to the Gemini model (up to 10 rounds) until the AST is clean, then upserts the result and clears the failure from the checkpoint.
6. Saves an atomic checkpoint (temp-file rename) after every committed batch.

Press **Ctrl+C** to interrupt safely — the next `--ingest` run resumes automatically, skipping anything unchanged.

> **Daily scheduling:** Because ingestion is content-hash based, it is safe to run on a daily schedule (Windows Task Scheduler, cron, CI pipeline). Only modified or new procedures are re-processed.

### 5. Start the web chat

```bash
dotnet run
# or explicitly:
dotnet run -- --web
```

Open `http://localhost:5000` in a browser. Enter the `Web:AccessPassword` when prompted. The AG-UI frontend connects to the `/chat` SSE endpoint. Authentication uses an `HttpOnly` session cookie set by `POST /api/auth`.

### 6. Start the MCP server (optional)

```bash
dotnet run -- --mcp
```

The MCP server listens on `Mcp:Url` (default `http://localhost:3001`) using the HTTP transport. Connect any MCP-compatible client such as [Cline](https://github.com/cline/cline) or Claude Desktop and point it at that URL.

---

## Ingestion Pipeline Detail

### Incremental change detection

Every source file is hashed with SHA-256 before parsing. The checkpoint stores a `processedFiles` map of `{ filePath → SHA-256 hex }`. On each run:

- **Hash matches stored value** → file skipped entirely (no parse, no embed, no Neo4j write).
- **Hash differs or file is new** → full parse + embed + upsert cycle runs.
- **Previously failed files** → always retried regardless of hash.

This makes daily runs cheap — typically only a small fraction of the corpus changes between runs.

> **Migration note:** Checkpoints from v1.x used a sequential `lastCommittedIndex` format. The v2 pipeline auto-detects the old format and resets to a full re-ingest on the first run, then switches to hash-map tracking thereafter.

### Two-tier parse resilience

| Tier | Path | Mechanism | Speed |
|---|---|---|---|
| Tier 1 | Synchronous (main loop) | `LegacySqlPreprocessor` → `TSql160Parser` → `StoredProcedureVisitor` | Memory-speed |
| Tier 2 | Background fire-and-forget | `AiSqlRepairAgent` → up to 10 LLM repair rounds → re-parse | Network-bound |

Files that fail both tiers are persisted in `failedFiles` inside the checkpoint JSON and retried on the next `--ingest` run.

### LegacySqlPreprocessor transformations

Applies eight deterministic regex transforms for Sybase-to-T-SQL incompatibilities before handing SQL to `TSql160Parser`:

1. **Legacy RAISERROR** — `RAISERROR 20001 'msg'` → `RAISERROR('msg', 16, 1)`
2. **SET ARITHABORT NUMERIC_TRUNCATION** — Sybase-only qualifier stripped
3. **Double-quoted string literals** — `"value"` → `'value'`
4. **Sybase outer-join operators** — `*=` and `=*` neutralised to `=`
5. **CURSOR FOR READ ONLY / FOR BROWSE** — Sybase cursor options stripped
6. **SET SHOWPLAN ON|OFF** — Sybase-only statement stripped
7. **SET PROCID ON|OFF** — Sybase-only statement stripped
8. **Sybase multi-assignment SET** — `SET @a = e1, @b = e2` → `SELECT @a = e1, @b = e2`

### Durable checkpoint format (v2)

```json
{
  "lastRunUtc": "2026-03-10T22:00:00Z",
  "processedFiles": {
    "C:\\SP\\sp\\usp_GetOrder.sp": "a3f4b2c1...",
    "C:\\SP\\sp\\usp_CreateInvoice.sp": "88c1d0e5..."
  },
  "failedFiles": [
    "C:\\SP\\sp\\usp_Legacy.sp"
  ]
}
```

Writes are atomic: the JSON is serialised to a `.tmp` file then renamed over the target, preventing corrupt state on crash or Ctrl+C.

---

## Neo4j Graph Schema

```
(:Procedure {name, schema, sql, contentHash, embedding})
    -[:CALLS]->              (:Procedure)
    -[:READS_FROM]->         (:Table {name, schema})
    -[:WRITES_TO]->          (:Table {name, schema})
    -[:USES_PARAMETER]->     (:Parameter {procedureName, name, dataType, isOutput, hasDefault})
    -[:SHARES_TABLE_WITH {table}]-> (:Procedure)
```

### Nodes

| Label | Key Properties | Description |
|---|---|---|
| `:Procedure` | `name` (unique) | A stored procedure |
| `:Table` | `name` (unique) | A database table |
| `:Parameter` | `(procedureName, name)` (node key) | A declared procedure parameter |

### Relationships

| Type | Direction | Created during | Description |
|---|---|---|---|
| `:CALLS` | Procedure → Procedure | Parse (AST `EXEC` detection) | Static call dependency |
| `:READS_FROM` | Procedure → Table | Parse (AST `SELECT`/`FROM`/`JOIN`) | Table read access |
| `:WRITES_TO` | Procedure → Table | Parse (AST `INSERT`/`UPDATE`/`DELETE`/`MERGE`) | Table write access |
| `:USES_PARAMETER` | Procedure → Parameter | Parse (AST parameter list) | Declared input/output parameter |
| `:SHARES_TABLE_WITH` | Procedure → Procedure | Upsert (graph self-join) | Data coupling — one writes to a table the other reads |

### Schema constraints and indexes

- Unique constraint on `Procedure.name`
- Unique constraint on `Table.name`
- Node key constraint on `(Parameter.procedureName, Parameter.name)`
- 1024-dimensional cosine-similarity vector index `procedure_embeddings` on `Procedure.embedding`
- All constraints and the vector index are created idempotently on startup — no manual Cypher required.

### Useful Cypher queries

```cypher
-- All direct callers of a procedure
MATCH (caller:Procedure)-[:CALLS]->(callee:Procedure {name: 'usp_InsertOrder'})
RETURN caller.name;

-- Full call chain up to 3 hops (outward)
MATCH path = (p:Procedure {name: 'usp_ProcessOrders'})-[:CALLS*1..3]->(dep:Procedure)
RETURN path;

-- Full caller chain up to 3 hops (inward) — who calls this?
MATCH path = (caller:Procedure)-[:CALLS*1..3]->(p:Procedure {name: 'usp_InsertOrder'})
RETURN DISTINCT caller.name;

-- All tables written by a procedure
MATCH (p:Procedure {name: 'usp_InsertOrder'})-[:WRITES_TO]->(t:Table)
RETURN t.name;

-- All procedures touching a specific table
MATCH (p:Procedure)-[r:READS_FROM|WRITES_TO]->(t:Table {name: 'Orders'})
RETURN p.name, type(r) AS access;

-- Procedures data-coupled to a given procedure (shared table writes/reads)
MATCH (p:Procedure {name: 'usp_InsertOrder'})-[r:SHARES_TABLE_WITH]->(other:Procedure)
RETURN other.name, r.table AS sharedTable;

-- All procedures with an INT output parameter
MATCH (p:Procedure)-[:USES_PARAMETER]->(param:Parameter {isOutput: true})
WHERE toUpper(param.dataType) STARTS WITH 'INT'
RETURN p.name, param.name, param.dataType;
```

---

## Query Tools

Both the AG-UI agent and the MCP server expose seven tools backed by `HybridSearchService` and `INeo4jRepository`:

| Tool name | Parameters | Description |
|---|---|---|
| `search_procedures` | `query: string` | Hybrid search — translates query to Dutch, embeds, runs top-3 vector similarity, expands 1-hop graph neighbours |
| `get_procedure_sql` | `name: string` | Returns the full SQL body stored in Neo4j for the named procedure |
| `expand_call_chain` | `name: string, depth: int` | Traverses `:CALLS` outward up to `depth` hops (1–5) — returns callee names |
| `get_caller_chain` | `name: string, depth: int` | Traverses `:CALLS` inward up to `depth` hops (1–5) — returns all upstream callers; use for impact analysis |
| `get_table_usage` | `tableName: string` | Returns every procedure that reads from or writes to the specified table |
| `get_shared_table_procedures` | `procedureName: string` | Returns procedures data-coupled via `:SHARES_TABLE_WITH` — procedures that read a table this one writes, or vice versa |
| `find_procedures_by_parameter_type` | `dataType: string` | Returns all procedures that declare a parameter whose SQL data type starts with the given prefix (case-insensitive) |

### HybridSearchService — query translation

The knowledge base is stored in Dutch. Before embedding, any incoming query is translated to Dutch by the Gemini model so that the query vector aligns with the stored Dutch embeddings. If translation fails, the original query is used as a safe fallback. The final answer is always delivered in the user's original language.

### AG-UI web chat

`GraphQueryTools` wraps the seven tools as `AIFunction` definitions and binds them to a `ChatClientAgent` (from `Microsoft.Agents.AI`). The agent runs an automatic tool-calling loop up to `Ai:Agent:MaxToolRounds` rounds before composing its final answer.

### MCP server

`StoreProcTools` exposes the same seven tools as `[McpServerTool]` methods, discovered automatically via `WithToolsFromAssembly`. Connect any MCP-compatible client to `Mcp:Url`.

---

## AI Resilience

All AI provider calls (`ChatProvider`, `EmbeddingProvider`) are wrapped in Polly resilience pipelines built by `AiResiliencePipelineFactory`:

- **Retries on:** HTTP 429 (rate-limit) and `HttpRequestException` (transient network).
- **Surfaces immediately:** 400, 401, and all other non-transient errors.
- **Strategy:** exponential back-off with full jitter; configurable via `Ai:Resilience`.

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| `No .sp files found` | Wrong directory or wrong extension | Set `Ingestion:SqlSourceDirectory` to the correct path; ensure files end in `.sp` |
| `Neo4j connection refused` | Neo4j not running or wrong URI | Start Neo4j and verify `Neo4j:Uri` |
| `Vector index not found` | GenAI plugin not enabled | Enable the **GenAI / Vector** plugin in Neo4j and restart the database |
| `401 Unauthorized` on embeddings | Wrong or expired Voyage AI key | Replace `Ai:Embedding:ApiKey` |
| `401 Unauthorized` on chat | Wrong or expired Gemini key | Replace `Ai:Chat:ApiKey` |
| Pipeline re-ingests everything | Legacy v1 checkpoint detected | Expected on first v2 run — subsequent runs are incremental |
| Pipeline re-ingests everything | `CheckpointFile` path changed | Ensure `Ingestion:CheckpointFile` points to the same path across runs |
| Web chat returns 401 | Session cookie absent or expired | Re-authenticate via the login screen |
| MCP client cannot connect | Wrong URL or firewall | Verify `Mcp:Url` matches the client configuration |
| Files permanently stuck in `failedFiles` | Both Tier-1 and Tier-2 repair exhausted | Inspect the file manually; it may contain non-SQL content or be empty |
| `SHARES_TABLE_WITH` edges missing | Procedures ingested before the counterpart existed | Re-run `--ingest`; edges are recomputed on every upsert |

---

## Project Structure

```
RoZwet.Tools.StoreProc/
├── Program.cs                          Entry point; three modes
├── appsettings.template.json           Configuration template (commit this)
├── appsettings.json                    Secrets (gitignored)
├── wwwroot/
│   └── index.html                      AG-UI browser frontend
└── src/
    ├── Domain/
    │   ├── StoredProcedure.cs          Aggregate root (+ ContentHash, Parameters)
    │   ├── ProcedureCall.cs            Value object — callee name + schema
    │   ├── TableDependency.cs          Value object + TableAccessType enum
    │   └── ProcedureParameter.cs       Value object — name, dataType, isOutput, hasDefault
    ├── Application/
    │   ├── Contracts/
    │   │   └── INeo4jRepository.cs     Repository port (+ new query methods + result records)
    │   ├── Agents/
    │   │   ├── SqlAnalysisAgent.cs     Parse + embed + parameter extraction + SHA-256 hashing
    │   │   └── GraphQueryTools.cs      7 AIFunction tools for the AG-UI agent
    │   ├── McpServer/
    │   │   └── StoreProcTools.cs       7 McpServerTool definitions
    │   ├── Pipeline/
    │   │   ├── PipelineOrchestrator.cs Incremental ingestion loop (hash-based skip)
    │   │   └── IngestionCheckpoint.cs  Hash-map checkpoint (processedFiles + failedFiles)
    │   └── Services/
    │       ├── HybridSearchService.cs  Vector + graph hybrid search
    │       └── ChatService.cs          Agentic tool-calling loop
    └── Infrastructure/
        ├── Parsing/
        │   ├── LegacySqlPreprocessor.cs  8-rule Sybase regex normaliser
        │   ├── TsqlFragmentVisitor.cs    ScriptDom AST → CALLS + table dependencies
        │   └── AiSqlRepairAgent.cs       LLM-based syntax repair
        ├── Ai/
        │   ├── ChatProvider.cs           Gemini facade + Polly
        │   ├── EmbeddingProvider.cs      Voyage AI facade + Polly
        │   ├── AiResilienceOptions.cs    Retry config record
        │   └── AiResiliencePipelineFactory.cs  Polly pipeline builder
        └── Neo4j/
            ├── Neo4jRepository.cs        Full INeo4jRepository implementation
            └── Neo4jIndexInitializer.cs  Idempotent schema bootstrap
```

---

## License

[MIT](LICENSE) — © 2026 RoZwet

---

## Version

**v2.0.0** — Extended graph schema with `:USES_PARAMETER`, `:SHARES_TABLE_WITH`, and reverse CALLS traversal · 7 AI + MCP tools · Incremental daily ingestion via SHA-256 content-hash change detection · Hash-map checkpoint replacing sequential index format.

Versioning follows [Semantic Versioning 2.0.0](https://semver.org/).
