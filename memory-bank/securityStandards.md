# SECURITY STANDARDS: RoZwet.Tools.StoreProc

## Zero-Trust Protocol

### Input Validation
- All SQL file paths are canonicalized and validated to remain within the configured `SqlSourceDirectory`
- No user-supplied strings are interpolated into Cypher queries — parameterized queries only
- All configuration values are validated at startup; missing required keys cause immediate exit

### Secret Management
- Neo4j credentials and AI API keys are sourced from `appsettings.json` (dev) or environment variables (prod)
- Secrets are NEVER logged, printed, or included in exception messages
- `appsettings.json` is in `.gitignore`

### Neo4j Security
- All Cypher queries use `$param` syntax — no string concatenation in queries
- Driver connection uses `GraphDatabase.Driver(uri, AuthTokens.Basic(...))`
- Sessions are disposed immediately after use via `await using`

---

## SOLID / DRY / KISS Compliance

### SOLID
- **S**: Each class has one reason to change (agent only parses/embeds, repository only persists)
- **O**: New relationship types can be added without modifying existing upsert logic
- **L**: `INeo4jRepository` implementations are interchangeable
- **I**: `IHybridSearchService` and `IChatService` are segregated interfaces
- **D**: Application layer depends on interfaces, not Neo4j or AI SDK concretions

### DRY
- Cypher query strings are defined as private constants, never duplicated
- Batch processing logic exists in one place: `PipelineOrchestrator`

### KISS
- Checkpoint is a plain JSON file — no database, no distributed lock, no complexity
- Search is two sequential Cypher queries — no complex graph algorithms needed

---

## Performance Benchmarks

| Operation | Target | Method |
|---|---|---|
| SQL parsing (per file) | < 50ms | ScriptDom direct AST walk |
| Embedding generation | < 200ms | Single API call per procedure |
| Neo4j batch upsert (50 procs) | < 500ms | Single Bolt transaction |
| Vector search (top-3) | < 100ms | Native Neo4j vector index |
| End-to-end chat response | < 3s | Parallel embed + search |

---

## Audit-Ready Code Standards
- No `TODO`, `HACK`, `FIXME` comments in committed code
- No hardcoded secrets
- No `catch (Exception e) {}` swallowing
- All public API methods have XML summary documentation
- `CancellationToken` propagated throughout all async paths
- `ILogger<T>` used for structured logging — no `Console.WriteLine` in production paths (except Chat UI)

---

## Dependency Constraints
- All NuGet packages pinned to specific versions in `.csproj`
- No pre-release packages in production paths
- Transitive dependency audit run before each release
