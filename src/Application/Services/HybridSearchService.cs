using Microsoft.Extensions.Logging;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;

namespace RoZwet.Tools.StoreProc.Application.Services;

/// <summary>
/// Encapsulates the two-step hybrid GraphRAG search:
/// Step A — vector similarity search for top-K nearest procedures.
/// Step B — 1-hop graph expansion for contextual neighbors.
/// </summary>
internal sealed class HybridSearchService
{
    private const int DefaultTopK = 3;

    private readonly INeo4jRepository _repository;
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly ILogger<HybridSearchService> _logger;

    public HybridSearchService(
        INeo4jRepository repository,
        EmbeddingProvider embeddingProvider,
        ILogger<HybridSearchService> logger)
    {
        _repository = repository;
        _embeddingProvider = embeddingProvider;
        _logger = logger;
    }

    /// <summary>
    /// Executes the hybrid vector + graph search for a natural-language query.
    /// Returns a structured <see cref="GraphSearchContext"/> ready for RAG prompt injection.
    /// </summary>
    public async Task<GraphSearchContext> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be empty.", nameof(query));

        _logger.LogDebug("Hybrid search initiated for query of length {Len}.", query.Length);

        var embedding = await _embeddingProvider.GenerateAsync(query, cancellationToken);

        var topResults = await _repository.VectorSearchAsync(embedding, DefaultTopK, cancellationToken);

        _logger.LogDebug("Vector search returned {Count} results.", topResults.Count);

        var topNames = topResults.Select(r => r.Name).ToList();

        var neighbors = await _repository.ExpandNeighborsAsync(topNames, cancellationToken);

        _logger.LogDebug("Graph expansion returned {Count} neighbor names.", neighbors.Count);

        return new GraphSearchContext(topResults, neighbors);
    }
}

/// <summary>
/// Structured result from a hybrid search combining vector similarity and graph expansion.
/// </summary>
public sealed record GraphSearchContext(
    IReadOnlyList<SearchResult> TopProcedures,
    IReadOnlyList<string> NeighborNames)
{
    /// <summary>
    /// Serializes the context into a structured string for injection into the RAG system prompt.
    /// </summary>
    public string ToContextString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Relevant Stored Procedures (by semantic similarity)");
        sb.AppendLine();

        foreach (var proc in TopProcedures)
        {
            sb.AppendLine($"### [{proc.Schema}].[{proc.Name}]  (score: {proc.Score:F4})");
            sb.AppendLine("```sql");
            sb.AppendLine(proc.Sql.Length > 2000 ? proc.Sql[..2000] + "\n-- [truncated]" : proc.Sql);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (NeighborNames.Count > 0)
        {
            sb.AppendLine("## Related Procedures (1-hop graph neighbors)");
            foreach (var name in NeighborNames)
                sb.AppendLine($"- {name}");
        }

        return sb.ToString();
    }
}
