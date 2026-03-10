using Microsoft.Extensions.Logging;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;

namespace RoZwet.Tools.StoreProc.Application.Services;

/// <summary>
/// Grounds a user question in the retrieved graph context and generates a response
/// via the configured <see cref="ChatProvider"/>.
/// </summary>
internal sealed class ChatService
{
    private const string SystemPromptTemplate = """
        You are an expert database engineer specializing in Sybase stored procedure analysis.
        Answer the user's question using ONLY the stored procedure code and relationships provided below.
        If the answer cannot be determined from the provided context, state that clearly.
        Do not invent procedure names, table names, or logic that is not present in the context.

        {CONTEXT}
        """;

    private readonly HybridSearchService _searchService;
    private readonly ChatProvider _chatProvider;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        HybridSearchService searchService,
        ChatProvider chatProvider,
        ILogger<ChatService> logger)
    {
        _searchService = searchService;
        _chatProvider = chatProvider;
        _logger = logger;
    }

    /// <summary>
    /// Answers a user question using the GraphRAG pipeline:
    /// search → context injection → chat completion.
    /// </summary>
    public async Task<string> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.", nameof(question));

        _logger.LogInformation("Processing question: {Question}", question);

        var context = await _searchService.SearchAsync(question, cancellationToken);

        var systemPrompt = SystemPromptTemplate.Replace("{CONTEXT}", context.ToContextString());

        var answer = await _chatProvider.CompleteAsync(systemPrompt, question, cancellationToken);

        _logger.LogInformation("Answer generated ({Len} chars).", answer.Length);

        return answer;
    }
}
