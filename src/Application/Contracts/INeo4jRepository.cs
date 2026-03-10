using RoZwet.Tools.StoreProc.Domain;

namespace RoZwet.Tools.StoreProc.Application.Contracts;

/// <summary>
/// Port defining all Neo4j persistence operations.
/// Implementations live in Infrastructure; all callers depend only on this interface.
/// </summary>
public interface INeo4jRepository
{
    /// <summary>
    /// Ensures the vector index and constraints exist in Neo4j.
    /// Safe to call on every startup — all operations are idempotent.
    /// </summary>
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a batch of stored procedures as nodes with their relationships.
    /// All upserts within a batch execute inside a single Bolt transaction.
    /// </summary>
    Task UpsertBatchAsync(
        IReadOnlyList<StoredProcedure> procedures,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored SHA-256 content hash for a procedure by its exact name.
    /// Returns <see langword="null"/> when the procedure does not exist in the graph.
    /// Used by the ingestion pipeline for incremental change detection.
    /// </summary>
    Task<string?> GetContentHashAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a vector similarity search over the procedure_embeddings index.
    /// Returns the top <paramref name="topK"/> nearest procedures by cosine similarity.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> VectorSearchAsync(
        float[] embedding,
        int topK,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Expands the search context by fetching 1-hop neighbors (CALLS / READS_FROM)
    /// for a set of procedure names.
    /// </summary>
    Task<IReadOnlyList<string>> ExpandNeighborsAsync(
        IReadOnlyList<string> procedureNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the full SQL body of a stored procedure identified by its exact name.
    /// Returns <see langword="null"/> when the procedure is not found in the graph.
    /// </summary>
    Task<string?> GetProcedureSqlAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Traverses CALLS relationships outward from <paramref name="name"/> up to
    /// <paramref name="depth"/> hops and returns the distinct names of all
    /// procedures reachable from that root (callees).
    /// </summary>
    Task<IReadOnlyList<string>> ExpandCallChainAsync(
        string name,
        int depth,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Traverses CALLS relationships inward toward <paramref name="name"/> up to
    /// <paramref name="depth"/> hops and returns the distinct names of all
    /// procedures that eventually call the given procedure (callers).
    /// </summary>
    Task<IReadOnlyList<string>> GetCallerChainAsync(
        string name,
        int depth,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the names of all procedures that reference the given table
    /// via READS_FROM or WRITES_TO relationships.
    /// </summary>
    Task<IReadOnlyList<string>> GetTableUsageAsync(
        string tableName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the names of all procedures coupled to <paramref name="procedureName"/>
    /// via SHARES_TABLE_WITH relationships — meaning they read from a table that the
    /// given procedure writes to, or vice versa.
    /// </summary>
    Task<IReadOnlyList<SharedTableProcedure>> GetSharedTableProceduresAsync(
        string procedureName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all procedures that declare at least one parameter whose SQL data type
    /// matches <paramref name="dataType"/> (case-insensitive prefix match).
    /// </summary>
    Task<IReadOnlyList<ParameterMatch>> FindProceduresByParameterTypeAsync(
        string dataType,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result record from a vector similarity search query.
/// </summary>
public sealed record SearchResult(string Name, string Schema, string Sql, double Score);

/// <summary>
/// Result record from a shared-table coupling query.
/// </summary>
public sealed record SharedTableProcedure(string ProcedureName, string SharedTableName);

/// <summary>
/// Result record from a parameter-type search query.
/// </summary>
public sealed record ParameterMatch(string ProcedureName, string ParameterName, string DataType);
