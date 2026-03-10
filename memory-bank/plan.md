# ARCHITECTURAL PLAN: RoZwet.Tools.StoreProc — GraphRAG Stored Procedure Intelligence System

## Version: 1.0.0 | Status: ACTIVE

---

## 1. MISSION STATEMENT

Process 5,500 Sybase stored procedures into a Neo4j GraphRAG system, enabling semantic hybrid search and real-time chat-driven intelligence over the procedural dependency graph.

---

## 2. SYSTEM ARCHITECTURE

```
┌─────────────────────────────────────────────────────────────────┐
│                    RoZwet.Tools.StoreProc                        │
│                    (.NET 10 Console App)                         │
├─────────────────────────────────────────────────────────────────┤
│  ENTRY POINT: Program.cs (DI Bootstrap + Mode Selector)          │
│    ├── Mode: --ingest  → PipelineOrchestrator                    │
│    └── Mode: --chat    → ChatLoop                                │
├─────────────────────────────────────────────────────────────────┤
│  DOMAIN LAYER (src/Domain/)                                      │
│    ├── StoredProcedure.cs       — Aggregate root                 │
│    ├── ProcedureCall.cs         — Value object                   │
│    └── TableDependency.cs       — Value object                   │
├─────────────────────────────────────────────────────────────────┤
│  APPLICATION LAYER (src/Application/)                            │
│    ├── Agents/                                                   │
│    │   └── SqlAnalysisAgent.cs  — MAF Agent: parse + embed       │
│    ├── Pipeline/                                                 │
│    │   ├── PipelineOrchestrator.cs — Durable run coordinator     │
│    │   └── IngestionCheckpoint.cs  — Resume-state tracker        │
│    └── Services/                                                 │
│        ├── HybridSearchService.cs — Vector + Graph traversal     │
│        └── ChatService.cs         — RAG chat orchestration       │
├─────────────────────────────────────────────────────────────────┤
│  INFRASTRUCTURE LAYER (src/Infrastructure/)                      │
│    ├── Parsing/                                                  │
│    │   └── TsqlFragmentVisitor.cs — ScriptDom AST walker         │
│    ├── Neo4j/                                                    │
│    │   ├── Neo4jRepository.cs   — Upsert nodes/edges, batch=50   │
│    │   └── Neo4jIndexInitializer.cs — Vector index bootstrap     │
│    └── Ai/                                                       │
│        ├── EmbeddingProvider.cs  — IEmbeddingGenerator wrapper   │
│        └── ChatProvider.cs       — IChatClient wrapper           │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. PHASE BREAKDOWN

### Phase 1 — Infrastructure Setup
- Target framework: net10.0
- DI container: Microsoft.Extensions.Hosting
- Neo4j vector index: Cosine similarity, 1024 dimensions, on Procedure nodes
- IEmbeddingGenerator: Codestral-Embed via MAIA OpenAI-compatible endpoint
- IChatClient: Codestral-Instruct via MAIA OpenAI-compatible endpoint

### Phase 2 — SQL Analysis Agent
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

### Phase 5 — Hybrid Search + Chat
- Vector search: top 3 nearest procedure embeddings
- Graph expansion: 1-hop CALLS/READS_FROM neighbors
- Chat: inject structured context into system prompt → IChatClient

---

## 4. KEY DESIGN DECISIONS

| Decision | Rationale |
|---|---|
| Checkpoint as atomic JSON file | Zero external dependency; temp-file swap guarantees no corrupt state |
| Batch size = 50 | Balances Bolt transaction overhead vs memory for 5,500 procedures |
| Vector index on Procedure nodes only | Tables are structural; only procedure semantics need embedding |
| 1-hop graph expansion | Sufficient context without exponential traversal cost |
| TSqlFragmentVisitor for Sybase SQL | ScriptDom T-SQL dialect covers EXEC/FROM patterns common to Sybase ASE |

---

## 5. NEO4J GRAPH SCHEMA

### Nodes
- `(:Procedure {name, schema, sql, embedding: float[1024]})`
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

## 6. CONFIGURATION SCHEMA (appsettings.json)

```json
{
  "Neo4j": {
    "Uri": "bolt://localhost:7687",
    "Username": "neo4j",
    "Password": "<secret>"
  },
  "Ai": {
    "Endpoint": "https://codestral.mistral.ai/v1",
    "ApiKey": "<secret>",
    "EmbeddingModel": "codestral-embed",
    "ChatModel": "codestral-latest"
  },
  "Ingestion": {
    "SqlSourceDirectory": "./sql",
    "CheckpointFile": "./checkpoint.json",
    "BatchSize": 50
  }
}
```

---

## 7. HYBRID SEARCH CYPHER

```cypher
// Step A: Vector similarity search
CALL db.index.vector.queryNodes('procedure_embeddings', 3, $embedding)
YIELD node AS p, score
RETURN p.name, p.sql, score

// Step B: 1-hop graph expansion
MATCH (p:Procedure)-[:CALLS|READS_FROM]->(related)
WHERE p.name IN $topNames
RETURN p.name, collect(DISTINCT related.name) AS related
```
