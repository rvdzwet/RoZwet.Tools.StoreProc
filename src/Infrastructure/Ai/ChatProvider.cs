using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RoZwet.Tools.StoreProc.Infrastructure.Ai;

/// <summary>
/// Thin facade over <see cref="IChatClient"/> for structured RAG completions.
/// Builds the full chat message list from a system prompt and user question.
/// </summary>
internal sealed class ChatProvider
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<ChatProvider> _logger;

    public ChatProvider(IChatClient chatClient, ILogger<ChatProvider> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Sends a grounded prompt to the chat model and returns the response text.
    /// </summary>
    /// <param name="systemPrompt">Structured context injected as the system message.</param>
    /// <param name="userQuestion">The raw user question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userQuestion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userQuestion))
            throw new ArgumentException("User question cannot be empty.", nameof(userQuestion));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userQuestion)
        };

        _logger.LogDebug("Sending chat completion request. System prompt length: {Len}.", systemPrompt.Length);

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);

        var content = response.Text ?? string.Empty;

        _logger.LogDebug("Chat completion received. Response length: {Len}.", content.Length);

        return content;
    }
}
