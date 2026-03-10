# RoZwet.Tools.StoreProc

> A .NET 10 GraphRAG tool that ingests Sybase stored procedures into Neo4j and provides an agentic chat interface powered by **Gemini 3 Flash** and **Voyage AI** embeddings.

---

## Overview

`RoZwet.Tools.StoreProc` processes `.sp` files containing Sybase stored procedure SQL into a Neo4j graph database enriched with 1024-dimensional semantic vector embeddings. Once ingested, you can query the knowledge base through an interactive chat session: the **Gemini 3 Flash** language model reasons over the graph using four built-in tools to retrieve, expand, and inspect procedures before answering.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  ENTRY POINT: Program.cs                                         │
│    ├── --ingest  → PipelineOrchestrator (durable, resumable)     │
│    └── --chat    → Agentic Chat Loop (Gemini 3 Flash + tools)    │
├──────────────────┬──────────────────────────────────────────────┤
│  APPLICATION     │  Domain layer — StoredProcedure aggregate     │
│  • SqlAnalysisAgent    (parse + embed each .sp file)             │
│  • GraphQueryTools     (4 AIFunction tool definitions)           │
│  • PipelineOrchestrator + IngestionCheckpoint (durable state)    │
│  • HybridSearchService (vector top-3 + 1-hop graph expansion)    │
│  • ChatService         (agentic MaxToolRounds loop)              │
├──────────────────┴──────────────────────────────────────────────┤
│  INFRASTRUCTURE                                                  │
│  • TsqlFragmentVisitor  — ScriptDom AST → calls + table deps     │
│  • Neo4jRepository      — Bolt driver, batched upserts           │
│  • EmbeddingProvider    — Voyage AI voyage-4-large (1024-dim)    │
│  • ChatProvider         — Gemini 3 Flash (OpenAI-compatible)     │
└─────────────────────────────────────────────────────────────────┘
```

---

## Prerequisites

| Requirement | Version / Notes |
|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0 or later |
| [Neo4j](https://neo4j.com/download/) | 5.x with **vector search** plugin enabled |
| [Gemini API key](https://aistudio.google.com/app/apikey) | Free tier available in Google AI Studio |
| [Voyage AI API key](https://dash.voyageai.com/) | Required for `voyage-4-large` embeddings |

### Neo4j Setup

1. Install Neo4j 5.x (Desktop, Docker, or Aura cloud).
2. Ensure the **GenAI / Vector** plugin is enabled (required for `db.index.vector.queryNodes`).
3. Note your Bolt URI (default: `bolt://localhost:7687`), username, and password.

---

## Configuration

`appsettings.json` is excluded from version control (it contains secrets). Copy `appsettings.template.json` from the project root into a new `appsettings.json` and fill in your values:

```json
{
  "Neo4j": {
    "Uri": "bolt://localhost:7687",
    "Username": "neo4j",
    "Password": "<your-neo4j-password>"
  },
  "Ai": {
    "Chat": {
      "Endpoint": "https://generativelanguage.googleapis.com/v1beta/openai/",
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
      "MaxToolRounds": 5
    }
  },
  "Ingestion": {
    "SqlSourceDirectory": "./sp",
    "CheckpointFile": "./checkpoint.json",
    "BatchSize": 50
  },
  "Web": {
    "Url": "http://localhost:5000",
    "AccessPassword": "<your-access-password>"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Neo4j": "Warning"
    }
  }
}
```

### Key Reference

| Key | Description |
|---|---|
| `Neo4j:Uri` | Bolt connection string for your Neo4j instance |
| `Neo4j:Username` | Neo4j username (default: `neo4j`) |
| `Neo4j:Password` | Neo4j password |
| `Ai:Chat:ApiKey` | Gemini API key from [Google AI Studio](https://aistudio.google.com/app/apikey) |
| `Ai:Chat:Model` | Gemini model name (default: `gemini-3-flash-preview`) |
| `Ai:Embedding:ApiKey` | Voyage AI API key from [dash.voyageai.com](https://dash.voyageai.com/) |
| `Ai:Embedding:Model` | Embedding model (default: `voyage-4-large`) |
| `Ai:Embedding:Dimensions` | Vector dimensions — must match the Neo4j index (default: `1024`) |
| `Ai:Agent:MaxToolRounds` | Max tool-calling rounds per chat turn before forcing a final answer |
| `Ingestion:SqlSourceDirectory` | Directory containing your `.sp` files |
| `Ingestion:CheckpointFile` | Path to the durable checkpoint file |
| `Ingestion:BatchSize` | Procedures per Neo4j transaction batch (default: `50`) |
| `Web:Url` | URL for the web server (default: `http://localhost:5000`) |
| `Web:AccessPassword` | Password for the web chat UI |

---

## Getting Started

### 1. Clone and Restore

```bash
git clone <repo-url>
cd RoZwet.Tools.StoreProc
dotnet restore
```

### 2. Configure

```bash
cp appsettings.template.json appsettings.json
# Edit appsettings.json and fill in your API keys and Neo4j credentials
```

### 3. Place Your Stored Procedures

Put all your Sybase `.sp` files into the directory specified by `Ingestion:SqlSourceDirectory` (default: `./sp`). Subdirectories are scanned recursively.

```
./sp/
  dbo.usp_GetOrders.sp
  dbo.usp_InsertOrder.sp
  reporting.usp_SalesSummary.sp
  ...
```

### 4. Run Ingestion

```bash
dotnet run -- --ingest
```

The pipeline will:
1. Discover all `.sp` files recursively
2. Parse each file with ScriptDom to extract procedure calls and table dependencies
3. Generate a 1024-dim semantic embedding via Voyage AI
4. Upsert procedure nodes, call edges, and table edges into Neo4j in batches of 50
5. Save a durable checkpoint after every committed batch

Progress is logged to the console. Press **Ctrl+C** to safely interrupt — the next `--ingest` run will resume automatically from the last committed batch.

### 5. Start Web Chat

```bash
dotnet run -- --web
```

Open `http://localhost:5000` in your browser. Enter the `Web:AccessPassword` to access the chat interface.

### 6. Start MCP Server (optional)

```bash
dotnet run -- --mcp
```

The MCP server listens on `Mcp:Url` (default: `http://localhost:3001`). Connect from any MCP-compatible client such as [Cline](https://github.com/cline/cline).

---

## Agentic Tools

The chat agent has four built-in tools it can call autonomously before composing its answer:

| Tool | Signature | Description |
|---|---|---|
| `search_procedures` | `(string query)` | Hybrid search — top-3 vector-similar procedures expanded by 1-hop graph neighbours |
| `get_procedure_sql` | `(string name)` | Returns the full SQL body stored in Neo4j for the named procedure |
| `expand_call_chain` | `(string name, int depth)` | Traverses `CALLS` edges up to `depth` hops (clamped 1–5) and returns the reachable call graph |
| `get_table_usage` | `(string tableName)` | Returns every procedure that reads from or writes to the specified table |

The model may invoke any combination of tools, in any order, up to `Ai:Agent:MaxToolRounds` rounds per user turn.

---

## Neo4j Graph Schema

After ingestion, the graph contains the following node and relationship types:

```
(:Procedure {name, schema, body, embedding})
    -[:CALLS]->(:Procedure)
    -[:READS_FROM]->(:Table {name})
    -[:WRITES_TO]->(:Table {name})
```

### Vector Index

A 1024-dimensional cosine-similarity vector index named `procedure-embeddings` is created automatically on first run over the `Procedure.embedding` property.

### Useful Cypher Queries

```cypher
// All procedures that call a specific procedure
MATCH (caller:Procedure)-[:CALLS]->(callee:Procedure {name: 'dbo.usp_InsertOrder'})
RETURN caller.name;

// Full call chain up to 3 hops
MATCH path = (p:Procedure {name: 'dbo.usp_ProcessOrders'})-[:CALLS*1..3]->(dep:Procedure)
RETURN path;

// All tables written to by a procedure
MATCH (p:Procedure {name: 'dbo.usp_InsertOrder'})-[:WRITES_TO]->(t:Table)
RETURN t.name;
```

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| `No .sp files found in './sp'` | Wrong directory or files have a different extension | Set `Ingestion:SqlSourceDirectory` to the correct path; ensure files end in `.sp` |
| `Neo4j connection refused` | Neo4j not running or wrong URI | Start Neo4j and verify `Neo4j:Uri` in `appsettings.json` |
| `Vector index not found` | GenAI plugin not enabled | Enable the **GenAI / Vector** plugin in Neo4j and restart the database |
| `401 Unauthorized` on embeddings | Wrong Voyage AI key | Replace `Ai:Embedding:ApiKey` with a valid key from [dash.voyageai.com](https://dash.voyageai.com/) |
| `401 Unauthorized` on chat | Wrong Gemini API key | Replace `Ai:Chat:ApiKey` with a valid key from [Google AI Studio](https://aistudio.google.com/app/apikey) |
| Pipeline restarts from scratch | Checkpoint file deleted or path changed | Ensure `Ingestion:CheckpointFile` points to the same path across runs |
| Build error: duplicate `AssemblyInfo` | `probe/` project inside solution tree | The `probe/` directory is excluded in `.csproj` and `.gitignore` — do not remove those exclusions |

---

## License

[MIT](LICENSE) — © 2026 RoZwet

---

## Version

**v1.1.0** — Gemini 3 Flash + Voyage AI embeddings + agentic tool-calling loop + `.sp` file support.

Versioning follows [Semantic Versioning 2.0.0](https://semver.org/).
