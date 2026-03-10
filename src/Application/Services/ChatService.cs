using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RoZwet.Tools.StoreProc.Application.Agents;

namespace RoZwet.Tools.StoreProc.Application.Services;

/// <summary>
/// Agentic chat service: runs a tool-calling loop that lets the language model
/// query the Neo4j graph knowledge base before composing its final answer.
/// The loop is capped at <c>Ai:Agent:MaxToolRounds</c> to prevent runaway execution.
/// </summary>
internal sealed class ChatService
{
    internal const string SystemPrompt = """
        You are an expert database engineer specializing in Sybase stored procedure analysis.
        You have access to tools that query a Neo4j graph database containing thousands of
        stored procedures with semantic embeddings and dependency relationships.
        The knowledge base data is stored in Dutch; the tools handle translation automatically.

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
        - Always respond in the same language the user used to ask their question.
          Translate technical output (descriptions, explanations) as needed, but keep
          exact identifiers such as procedure names and table names unchanged.
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
            ChatResponse response;
            try
            {
                response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
            }
            catch (ClientResultException ex)
            {
                var rawBody = ex.GetRawResponse()?.Content?.ToString() ?? "(no body)";
                _logger.LogError(
                    "Chat API returned HTTP {Status} on round {Round}. Body: {Body}",
                    ex.Status, round, rawBody);
                throw;
            }

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

            var toolResultContents = new List<AIContent>(toolCalls.Count);
            foreach (var toolCall in toolCalls)
            {
                _logger.LogInformation(
                    "Tool call → '{ToolName}' | args: {Args}",
                    toolCall.Name,
                    JsonSerializer.Serialize(toolCall.Arguments));

                var result = await InvokeToolSafeAsync(toolCall, cancellationToken);

                // Gemini's OpenAI-compatible endpoint requires tool-result content to be a
                // plain string.  Serialising a non-string object as the content body causes
                // HTTP 400 on the tool-result round.
                var resultString = result switch
                {
                    null        => string.Empty,
                    string s    => s,
                    _           => JsonSerializer.Serialize(result)
                };

                _logger.LogDebug(
                    "Tool result for '{ToolName}' (callId={CallId}): {Result}",
                    toolCall.Name,
                    toolCall.CallId,
                    resultString);

                toolResultContents.Add(new FunctionResultContent(toolCall.CallId, resultString));
            }

            _logger.LogDebug(
                "Sending {Count} tool result(s) back to model on round {Round}.",
                toolResultContents.Count, round + 1);

            messages.Add(new ChatMessage(ChatRole.Tool, toolResultContents));
        }

        _logger.LogWarning("MaxToolRounds ({Max}) exhausted. Requesting final answer without tools.", _maxToolRounds);

        var finalResponse = await _chatClient.GetResponseAsync(messages, new ChatOptions(), cancellationToken);
        return finalResponse.Text ?? string.Empty;
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
