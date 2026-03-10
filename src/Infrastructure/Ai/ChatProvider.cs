using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;

namespace RoZwet.Tools.StoreProc.Infrastructure.Ai;

/// <summary>
/// Thin facade over <see cref="IChatClient"/> for structured RAG completions.
/// Builds the full chat message list from a system prompt and user question.
/// Wraps every call in a Polly resilience pipeline that retries on HTTP 429
/// (rate-limit) and transient network failures with exponential back-off + jitter.
/// </summary>
internal sealed class ChatProvider
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<ChatProvider> _logger;
    private readonly ResiliencePipeline<string> _pipeline;

    public ChatProvider(
        IChatClient chatClient,
        AiResilienceOptions resilienceOptions,
        ILogger<ChatProvider> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
        _pipeline = AiResiliencePipelineFactory.Create<string>(resilienceOptions, logger, "Chat");
    }

    /// <summary>
    /// Sends a grounded prompt to the chat model and returns the response text.
    /// Retries automatically on transient failures as configured by <see cref="AiResilienceOptions"/>.
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

        var content = await _pipeline.ExecuteAsync(
            async ct =>
            {
                var response = await _chatClient.GetResponseAsync(messages, cancellationToken: ct);
                return response.Text ?? string.Empty;
            },
            cancellationToken);

        _logger.LogDebug("Chat completion received. Response length: {Len}.", content.Length);

        return content;
    }
}
