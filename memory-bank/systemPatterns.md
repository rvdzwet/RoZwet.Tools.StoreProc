# SYSTEM PATTERNS: RoZwet.Tools.StoreProc

## Namespace Conventions (Ubiquitous Language)

| Namespace | Bounded Context | Responsibility |
|---|---|---|
| `RoZwet.Tools.StoreProc.Domain` | Procedural Model | Aggregates, Value Objects, Domain Events |
| `RoZwet.Tools.StoreProc.Application` | Orchestration | Agents, Services, Pipeline coordination |
| `RoZwet.Tools.StoreProc.Infrastructure` | External Concerns | Neo4j, AI providers, SQL parsing |

---

## DDD Bounded Contexts

### 1. Procedural Dependency Context
- **Aggregate Root**: `StoredProcedure` — owns its `ProcedureCall` and `TableDependency` collections
- **Value Objects**: `ProcedureCall`, `TableDependency` — immutable, identity by value
- **Invariant**: A `StoredProcedure` with an empty name is invalid

### 2. Graph Persistence Context
- **Repository Interface**: `INeo4jRepository` — defined in Application, implemented in Infrastructure
- **Anti-Corruption Layer**: Neo4j node/relationship DTOs never leak into Domain

### 3. Intelligence Context
- **Port**: `IEmbeddingGenerator<string, Embedding<float>>` (Microsoft.Extensions.AI)
- **Port**: `IChatClient` (Microsoft.Extensions.AI)
- **Adapters**: `EmbeddingProvider`, `ChatProvider` in Infrastructure.Ai

---

## Clean Architecture Dependency Rule

```
Domain ← Application ← Infrastructure
Domain ← Application ← Program.cs (Composition Root)
```

- Domain has ZERO external dependencies
- Application depends only on Domain + Microsoft.Extensions.AI interfaces
- Infrastructure depends on Application interfaces + third-party libraries
- Program.cs is the composition root — the only place DI is wired

---

## Agent Pattern (Microsoft Agent Framework)

`SqlAnalysisAgent` follows the single-responsibility agent pattern:
1. Accepts a well-defined input (`string sqlFilePath`)
2. Produces a well-defined output (`StoredProcedure`)
3. Is stateless — all state lives in the domain object and checkpoint

---

## Durable Pipeline Pattern

```
PipelineOrchestrator
  │
  ├── Load IngestionCheckpoint (or create empty)
  ├── Discover all .sql files
  ├── Skip files with index < checkpoint.LastCommittedIndex
  ├── For each batch of 50:
  │     ├── SqlAnalysisAgent.AnalyzeAsync(file)  [for each in batch]
  │     ├── Neo4jRepository.UpsertBatchAsync(batch)
  │     └── IngestionCheckpoint.CommitAsync(batchEndIndex)
  └── Mark complete
```

Checkpoint uses atomic write: write to `checkpoint.tmp` → rename to `checkpoint.json`

---

## Repository Pattern

`INeo4jRepository` interface in Application layer:
```csharp
Task EnsureIndexAsync();
Task UpsertBatchAsync(IReadOnlyList<StoredProcedure> procedures);
Task<IReadOnlyList<SearchResult>> VectorSearchAsync(float[] embedding, int topK);
Task<IReadOnlyList<string>> ExpandNeighborsAsync(IReadOnlyList<string> procedureNames);
```

---

## Search Pattern (Hybrid RAG)

```
UserQuery
  │
  ├── IEmbeddingGenerator.GenerateEmbeddingAsync(query)
  ├── INeo4jRepository.VectorSearchAsync(embedding, topK=3)
  │     └── Returns: List<SearchResult> { Name, Sql, Score }
  ├── INeo4jRepository.ExpandNeighborsAsync(topNames)
  │     └── Returns: List<string> neighbor procedure names
  └── Build context string → IChatClient.CompleteAsync
```
