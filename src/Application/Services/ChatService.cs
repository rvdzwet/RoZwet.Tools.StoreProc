using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RoZwet.Tools.StoreProc.Application.Agents;

namespace RoZwet.Tools.StoreProc.Application.Services;

/// <summary>
/// Agentic chat service: runs a tool-calling loop that lets the language model
/// query the Neo4j graph knowledge base before composing its final answer.
/// The loop is capped at <c>Ai:Agent:MaxToolRounds</c> to prevent runaway execution.
/// Supports both blocking and streaming completion for the final answer turn.
/// </summary>
internal sealed class ChatService
{
    private const string SystemPrompt = """
        You are an expert database engineer specializing in Sybase stored procedure analysis.
        You have access to tools that query a Neo4j graph database containing thousands of
        stored procedures with semantic embeddings and dependency relationships.

        Strategy:
        1. Use search_procedures to locate semantically relevant procedures.
        2. Use get_procedure_sql to inspect full SQL bodies when needed.
        3. Use expand_call_chain to understand transitive dependencies.
        4. Use get_table_usage to find all procedures touching a specific table.

        Rules:
        - Only cite procedures and tables that you retrieved through the tools.
        - Do not invent procedure names, table names, or SQL logic.
        - When context is insufficient, state that clearly rather than guessing.
        - Provide precise, technical answers suitable for a senior database engineer.
        """;

    private readonly IChatClient _chatClient;
    private readonly GraphQueryTools _tools;
    private readonly int _maxToolRounds;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IChatClient chatClient,
        GraphQueryTools tools,
        IConfiguration config,
        ILogger<ChatService> logger)
    {
        _chatClient = chatClient;
        _tools = tools;
        _maxToolRounds = int.TryParse(config["Ai:Agent:MaxToolRounds"], out var r) && r > 0 ? r : 5;
        _logger = logger;
    }

    /// <summary>
    /// Answers a user question using the agentic GraphRAG pipeline.
    /// The model may call tools multiple times before returning a final answer.
    /// </summary>
    public async Task<string> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.", nameof(question));

        _logger.LogInformation("Agentic chat started. MaxToolRounds={Max}.", _maxToolRounds);

        var chatOptions = new ChatOptions { Tools = [.. _tools.All] };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, question)
        };

        for (int round = 0; round < _maxToolRounds; round++)
        {
            var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            messages.AddRange(response.Messages);

            var toolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (toolCalls.Count == 0)
            {
                _logger.LogInformation("Agentic chat completed after {Rounds} tool round(s).", round);
                return response.Text ?? string.Empty;
            }

            _logger.LogDebug("Tool round {Round}/{Max}: executing {Count} call(s).",
                round + 1, _maxToolRounds, toolCalls.Count);

            await ExecuteToolCallsAsync(messages, toolCalls, cancellationToken);
        }

        _logger.LogWarning("MaxToolRounds ({Max}) exhausted. Requesting final answer without tools.", _maxToolRounds);

        var finalResponse = await _chatClient.GetResponseAsync(messages, new ChatOptions(), cancellationToken);
        return finalResponse.Text ?? string.Empty;
    }

    /// <summary>
    /// Answers a user question using the agentic GraphRAG pipeline, streaming
    /// both reasoning tokens and final answer tokens as they arrive.
    /// Tool-calling rounds are resolved in full before streaming the final answer.
    /// </summary>
    /// <param name="question">The raw user question.</param>
    /// <param name="onChunk">
    /// Callback invoked for each streamed token.
    /// <c>isReasoning=true</c> indicates a model thinking/reasoning token;
    /// <c>isReasoning=false</c> indicates a final answer token.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AskStreamingAsync(
        string question,
        Action<bool, string> onChunk,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question cannot be empty.", nameof(question));

        ArgumentNullException.ThrowIfNull(onChunk);

        _logger.LogInformation("Agentic streaming chat started. MaxToolRounds={Max}.", _maxToolRounds);

        var chatOptions = new ChatOptions { Tools = [.. _tools.All] };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, question)
        };

        for (int round = 0; round < _maxToolRounds; round++)
        {
            var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            messages.AddRange(response.Messages);

            var toolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (toolCalls.Count == 0)
            {
                _logger.LogInformation("Tool rounds complete after {Rounds} round(s). Streaming final answer.", round);
                await StreamFinalAnswerAsync(messages, onChunk, cancellationToken);
                return;
            }

            _logger.LogDebug("Tool round {Round}/{Max}: executing {Count} call(s).",
                round + 1, _maxToolRounds, toolCalls.Count);

            await ExecuteToolCallsAsync(messages, toolCalls, cancellationToken);
        }

        _logger.LogWarning("MaxToolRounds ({Max}) exhausted. Streaming final answer without tools.", _maxToolRounds);
        await StreamFinalAnswerAsync(messages, onChunk, cancellationToken);
    }

    private async Task StreamFinalAnswerAsync(
        List<ChatMessage> messages,
        Action<bool, string> onChunk,
        CancellationToken cancellationToken)
    {
        var streamingOptions = new ChatOptions();

        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, streamingOptions, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent rc when rc.Text is { Length: > 0 }:
                        onChunk(true, rc.Text);
                        break;

                    case TextContent tc when tc.Text is { Length: > 0 }:
                        onChunk(false, tc.Text);
                        break;
                }
            }
        }
    }

    private async Task ExecuteToolCallsAsync(
        List<ChatMessage> messages,
        List<FunctionCallContent> toolCalls,
        CancellationToken cancellationToken)
    {
        var toolResultContents = new List<AIContent>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            var result = await InvokeToolSafeAsync(toolCall, cancellationToken);
            toolResultContents.Add(new FunctionResultContent(toolCall.CallId, result));
        }

        messages.Add(new ChatMessage(ChatRole.Tool, toolResultContents));
    }

    private async Task<object?> InvokeToolSafeAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken)
    {
        var tool = _tools.All.FirstOrDefault(t => t.Name == toolCall.Name);

        if (tool is null)
        {
            _logger.LogWarning("Model requested unknown tool '{ToolName}'.", toolCall.Name);
            return $"Tool '{toolCall.Name}' is not registered.";
        }

        try
        {
            _logger.LogDebug("Invoking tool '{ToolName}'.", toolCall.Name);
            var args = toolCall.Arguments is not null
                ? new AIFunctionArguments(toolCall.Arguments)
                : new AIFunctionArguments();
            return await tool.InvokeAsync(args, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool '{ToolName}' threw an exception.", toolCall.Name);
            return $"Tool execution failed: {ex.Message}";
        }
    }
}
