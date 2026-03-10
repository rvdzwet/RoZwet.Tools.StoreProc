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
                await UpsertParameterRelationshipsAsync(tx, proc);
            }

            foreach (var proc in procedures)
            {
                await UpsertSharedTableRelationshipsAsync(tx, proc);
            }
        });

        _logger.LogDebug("Upserted batch of {Count} procedures.", procedures.Count);
    }

    /// <inheritdoc />
    public async Task<string?> GetContentHashAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        await using var session = _driver.AsyncSession();

        const string cypher = """
            MATCH (p:Procedure {name: $name})
            RETURN p.contentHash AS contentHash
            LIMIT 1
            """;

        var cursor = await session.RunAsync(cypher, new { name });

        string? hash = null;
        await cursor.ForEachAsync(record =>
        {
            hash = record["contentHash"].As<string?>();
        });

        return hash;
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

    /// <inheritdoc />
    public async Task<string?> GetProcedureSqlAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        await using var session = _driver.AsyncSession();

        const string cypher = """
            MATCH (p:Procedure {name: $name})
            RETURN p.sql AS sql
            LIMIT 1
            """;

        var cursor = await session.RunAsync(cypher, new { name });

        string? sql = null;
        await cursor.ForEachAsync(record =>
        {
            sql = record["sql"].As<string?>();
        });

        return sql;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExpandCallChainAsync(
        string name,
        int depth,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || depth <= 0)
            return [];

        await using var session = _driver.AsyncSession();

        // depth is a validated positive int (not user string input) — safe to interpolate.
        var cypher = $$"""
            MATCH (root:Procedure {name: $name})-[:CALLS*1..{{depth}}]->(callee:Procedure)
            RETURN DISTINCT callee.name AS calleeName
            ORDER BY calleeName
            """;

        var cursor = await session.RunAsync(cypher, new { name });

        var chain = new List<string>();
        await cursor.ForEachAsync(record =>
        {
            var callee = record["calleeName"].As<string?>();
            if (!string.IsNullOrWhiteSpace(callee))
                chain.Add(callee);
        });

        return chain.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetCallerChainAsync(
        string name,
        int depth,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || depth <= 0)
            return [];

        await using var session = _driver.AsyncSession();

        // depth is a validated positive int — safe to interpolate.
        var cypher = $$"""
            MATCH (caller:Procedure)-[:CALLS*1..{{depth}}]->(target:Procedure {name: $name})
            RETURN DISTINCT caller.name AS callerName
            ORDER BY callerName
            """;

        var cursor = await session.RunAsync(cypher, new { name });

        var chain = new List<string>();
        await cursor.ForEachAsync(record =>
        {
            var caller = record["callerName"].As<string?>();
            if (!string.IsNullOrWhiteSpace(caller))
                chain.Add(caller);
        });

        return chain.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetTableUsageAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return [];

        await using var session = _driver.AsyncSession();

        const string cypher = """
            MATCH (p:Procedure)-[:READS_FROM|WRITES_TO]->(t:Table {name: $tableName})
            RETURN DISTINCT p.name AS procName
            ORDER BY procName
            """;

        var cursor = await session.RunAsync(cypher, new { tableName });

        var procedures = new List<string>();
        await cursor.ForEachAsync(record =>
        {
            var proc = record["procName"].As<string?>();
            if (!string.IsNullOrWhiteSpace(proc))
                procedures.Add(proc);
        });

        return procedures.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SharedTableProcedure>> GetSharedTableProceduresAsync(
        string procedureName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(procedureName))
            return [];

        await using var session = _driver.AsyncSession();

        const string cypher = """
            MATCH (p:Procedure {name: $name})-[r:SHARES_TABLE_WITH]->(other:Procedure)
            RETURN other.name AS procName, r.table AS tableName
            ORDER BY tableName, procName
            """;

        var cursor = await session.RunAsync(cypher, new { name = procedureName });

        var results = new List<SharedTableProcedure>();
        await cursor.ForEachAsync(record =>
        {
            var proc  = record["procName"].As<string?>();
            var table = record["tableName"].As<string?>();
            if (!string.IsNullOrWhiteSpace(proc) && !string.IsNullOrWhiteSpace(table))
                results.Add(new SharedTableProcedure(proc, table));
        });

        return results.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParameterMatch>> FindProceduresByParameterTypeAsync(
        string dataType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataType))
            return [];

        await using var session = _driver.AsyncSession();

        const string cypher = """
            MATCH (p:Procedure)-[:USES_PARAMETER]->(param:Parameter)
            WHERE toUpper(param.dataType) STARTS WITH toUpper($dataType)
            RETURN p.name AS procName, param.name AS paramName, param.dataType AS dataType
            ORDER BY procName, paramName
            """;

        var cursor = await session.RunAsync(cypher, new { dataType });

        var results = new List<ParameterMatch>();
        await cursor.ForEachAsync(record =>
        {
            var proc  = record["procName"].As<string?>();
            var param = record["paramName"].As<string?>();
            var type  = record["dataType"].As<string?>();
            if (!string.IsNullOrWhiteSpace(proc) && !string.IsNullOrWhiteSpace(param) && !string.IsNullOrWhiteSpace(type))
                results.Add(new ParameterMatch(proc, param, type));
        });

        return results.AsReadOnly();
    }

    private static async Task UpsertProcedureNodeAsync(IAsyncQueryRunner tx, StoredProcedure proc)
    {
        const string cypher = """
            MERGE (p:Procedure {name: $name})
            SET p.schema      = $schema,
                p.sql         = $sql,
                p.contentHash = $contentHash,
                p.embedding   = $embedding
            """;

        await tx.RunAsync(cypher, new
        {
            name        = proc.Name,
            schema      = proc.Schema,
            sql         = proc.Sql,
            contentHash = proc.ContentHash ?? string.Empty,
            embedding   = proc.Embedding ?? Array.Empty<float>()
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
                callerName   = proc.Name,
                calleeName   = call.CalleeName,
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
                procName    = proc.Name,
                tableName   = dep.TableName,
                tableSchema = dep.TableSchema
            });
        }
    }

    private static async Task UpsertParameterRelationshipsAsync(IAsyncQueryRunner tx, StoredProcedure proc)
    {
        if (proc.Parameters.Count == 0)
            return;

        const string cypher = """
            MATCH (p:Procedure {name: $procName})
            MERGE (param:Parameter {procedureName: $procName, name: $paramName})
            SET param.dataType   = $dataType,
                param.isOutput   = $isOutput,
                param.hasDefault = $hasDefault
            MERGE (p)-[:USES_PARAMETER]->(param)
            """;

        foreach (var parameter in proc.Parameters)
        {
            await tx.RunAsync(cypher, new
            {
                procName   = proc.Name,
                paramName  = parameter.Name,
                dataType   = parameter.DataType,
                isOutput   = parameter.IsOutput,
                hasDefault = parameter.HasDefault
            });
        }
    }

    private static async Task UpsertSharedTableRelationshipsAsync(IAsyncQueryRunner tx, StoredProcedure proc)
    {
        // For each table this procedure writes to, create SHARES_TABLE_WITH edges
        // pointing at all other procedures that read from the same table.
        // Also covers the reverse: tables this procedure reads from that others write to.
        const string cypher = """
            MATCH (a:Procedure {name: $procName})-[:WRITES_TO]->(t:Table)<-[:READS_FROM]-(b:Procedure)
            WHERE a.name <> b.name
            MERGE (a)-[r:SHARES_TABLE_WITH {table: t.name}]->(b)
            WITH a, t, b
            MERGE (b)-[r2:SHARES_TABLE_WITH {table: t.name}]->(a)
            """;

        await tx.RunAsync(cypher, new { procName = proc.Name });
    }
}
