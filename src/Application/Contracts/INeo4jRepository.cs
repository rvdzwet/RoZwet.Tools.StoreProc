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
}

/// <summary>
/// Result record from a vector similarity search query.
/// </summary>
public sealed record SearchResult(string Name, string Schema, string Sql, double Score);
