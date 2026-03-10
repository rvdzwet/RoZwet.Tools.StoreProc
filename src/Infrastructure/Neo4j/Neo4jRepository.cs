using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Domain;

namespace RoZwet.Tools.StoreProc.Infrastructure.Neo4j;

/// <summary>
/// Neo4j implementation of <see cref="INeo4jRepository"/>.
/// All Cypher queries use parameterized syntax — no string interpolation.
/// </summary>
internal sealed class Neo4jRepository : INeo4jRepository
{
    private const string VectorIndexName = "procedure_embeddings";
    private const int DefaultTopK = 3;

    private readonly IDriver _driver;
    private readonly Neo4jIndexInitializer _indexInitializer;
    private readonly ILogger<Neo4jRepository> _logger;

    public Neo4jRepository(
        IDriver driver,
        Neo4jIndexInitializer indexInitializer,
        ILogger<Neo4jRepository> logger)
    {
        _driver = driver;
        _indexInitializer = indexInitializer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await _indexInitializer.InitializeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertBatchAsync(
        IReadOnlyList<StoredProcedure> procedures,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        await session.ExecuteWriteAsync(async tx =>
        {
            foreach (var proc in procedures)
            {
                await UpsertProcedureNodeAsync(tx, proc);
                await UpsertCallRelationshipsAsync(tx, proc);
                await UpsertTableRelationshipsAsync(tx, proc);
            }
        });

        _logger.LogDebug("Upserted batch of {Count} procedures.", procedures.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> VectorSearchAsync(
        float[] embedding,
        int topK,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        const string cypher = """
            CALL db.index.vector.queryNodes($indexName, $topK, $embedding)
            YIELD node AS p, score
            RETURN p.name AS name, p.schema AS schema, p.sql AS sql, score
            ORDER BY score DESC
            """;

        var cursor = await session.RunAsync(cypher, new
        {
            indexName = VectorIndexName,
            topK,
            embedding
        });

        var results = new List<SearchResult>();
        await cursor.ForEachAsync(record =>
        {
            results.Add(new SearchResult(
                record["name"].As<string>(),
                record["schema"].As<string>(),
                record["sql"].As<string>(),
                record["score"].As<double>()));
        });

        return results.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExpandNeighborsAsync(
        IReadOnlyList<string> procedureNames,
        CancellationToken cancellationToken = default)
    {
        if (procedureNames.Count == 0)
            return [];

        await using var session = _driver.AsyncSession();

        const string cypher = """
            MATCH (p:Procedure)-[:CALLS|READS_FROM]->(related)
            WHERE p.name IN $names
            RETURN DISTINCT related.name AS relatedName
            """;

        var cursor = await session.RunAsync(cypher, new { names = procedureNames });

        var neighbors = new List<string>();
        await cursor.ForEachAsync(record =>
        {
            var name = record["relatedName"].As<string?>();
            if (!string.IsNullOrWhiteSpace(name))
                neighbors.Add(name);
        });

        return neighbors.AsReadOnly();
    }

    private static async Task UpsertProcedureNodeAsync(IAsyncQueryRunner tx, StoredProcedure proc)
    {
        const string cypher = """
            MERGE (p:Procedure {name: $name})
            SET p.schema = $schema,
                p.sql    = $sql,
                p.embedding = $embedding
            """;

        await tx.RunAsync(cypher, new
        {
            name = proc.Name,
            schema = proc.Schema,
            sql = proc.Sql,
            embedding = proc.Embedding ?? Array.Empty<float>()
        });
    }

    private static async Task UpsertCallRelationshipsAsync(IAsyncQueryRunner tx, StoredProcedure proc)
    {
        if (proc.Calls.Count == 0)
            return;

        const string cypher = """
            MATCH (caller:Procedure {name: $callerName})
            MERGE (callee:Procedure {name: $calleeName})
            ON CREATE SET callee.schema = $calleeSchema
            MERGE (caller)-[:CALLS]->(callee)
            """;

        foreach (var call in proc.Calls)
        {
            await tx.RunAsync(cypher, new
            {
                callerName = proc.Name,
                calleeName = call.CalleeName,
                calleeSchema = call.CalleeSchema
            });
        }
    }

    private static async Task UpsertTableRelationshipsAsync(IAsyncQueryRunner tx, StoredProcedure proc)
    {
        if (proc.TableDependencies.Count == 0)
            return;

        const string readCypher = """
            MATCH (p:Procedure {name: $procName})
            MERGE (t:Table {name: $tableName})
            ON CREATE SET t.schema = $tableSchema
            MERGE (p)-[:READS_FROM]->(t)
            """;

        const string writeCypher = """
            MATCH (p:Procedure {name: $procName})
            MERGE (t:Table {name: $tableName})
            ON CREATE SET t.schema = $tableSchema
            MERGE (p)-[:WRITES_TO]->(t)
            """;

        foreach (var dep in proc.TableDependencies)
        {
            var cypher = dep.AccessType == TableAccessType.Read ? readCypher : writeCypher;
            await tx.RunAsync(cypher, new
            {
                procName = proc.Name,
                tableName = dep.TableName,
                tableSchema = dep.TableSchema
            });
        }
    }
}
