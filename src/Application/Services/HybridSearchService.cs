using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;

namespace RoZwet.Tools.StoreProc.Application.Services;

/// <summary>
/// Encapsulates the two-step hybrid GraphRAG search:
/// Step A — vector similarity search for top-K nearest procedures.
/// Step B — 1-hop graph expansion for contextual neighbors.
/// </summary>
/// <remarks>
/// The knowledge base embeddings are generated from Dutch text.
/// Before embedding, any non-Dutch query is translated to Dutch via the LLM so that
/// the query vector aligns with the stored Dutch embeddings. The LLM caller is
/// responsible for presenting the final answer in the user's original language.
/// </remarks>
internal sealed class HybridSearchService
{
    private const int DefaultTopK = 3;

    private const string TranslationSystemPrompt =
        "You are a translation engine. " +
        "Translate the following text to Dutch. " +
        "Return ONLY the translated text — no explanation, no quotes, no prefix.";

    private readonly INeo4jRepository _repository;
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly IChatClient _chatClient;
    private readonly ILogger<HybridSearchService> _logger;

    public HybridSearchService(
        INeo4jRepository repository,
        EmbeddingProvider embeddingProvider,
        IChatClient chatClient,
        ILogger<HybridSearchService> logger)
    {
        _repository = repository;
        _embeddingProvider = embeddingProvider;
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Executes the hybrid vector + graph search for a natural-language query.
    /// Translates the query to Dutch before embedding so it aligns with the Dutch knowledge base.
    /// Returns a structured <see cref="GraphSearchContext"/> ready for RAG prompt injection.
    /// </summary>
    public async Task<GraphSearchContext> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query cannot be empty.", nameof(query));

        _logger.LogDebug("Hybrid search initiated for query of length {Len}.", query.Length);

        var dutchQuery = await TranslateToDataLanguageAsync(query, cancellationToken);

        var embedding = await _embeddingProvider.GenerateAsync(dutchQuery, cancellationToken);

        var topResults = await _repository.VectorSearchAsync(embedding, DefaultTopK, cancellationToken);

        _logger.LogDebug("Vector search returned {Count} results.", topResults.Count);

        var topNames = topResults.Select(r => r.Name).ToList();

        var neighbors = await _repository.ExpandNeighborsAsync(topNames, cancellationToken);

        _logger.LogDebug("Graph expansion returned {Count} neighbor names.", neighbors.Count);

        return new GraphSearchContext(topResults, neighbors);
    }

    /// <summary>
    /// Translates <paramref name="query"/> to Dutch using the LLM so that the resulting
    /// embedding aligns with the Dutch vectors stored in Neo4j.
    /// If translation fails the original query is used as a safe fallback.
    /// </summary>
    private async Task<string> TranslateToDataLanguageAsync(
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, TranslationSystemPrompt),
                new(ChatRole.User, query)
            };

            var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var translated = response.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(translated))
            {
                _logger.LogWarning("Translation returned empty result; falling back to original query.");
                return query;
            }

            _logger.LogDebug(
                "Query translated to Dutch. Original length: {OrigLen}, translated length: {TrLen}.",
                query.Length, translated.Length);

            return translated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query translation failed; falling back to original query.");
            return query;
        }
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
