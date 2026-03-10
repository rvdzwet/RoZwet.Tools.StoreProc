using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;

namespace RoZwet.Tools.StoreProc.Infrastructure.Parsing;

/// <summary>
/// AI-powered fallback that attempts to repair SQL which failed deterministic
/// preprocessing and T-SQL 160 parsing.
///
/// Sends the raw SQL plus the parser's error messages to the configured chat
/// model and asks it to return a T-SQL 160-compatible rewrite of the stored
/// procedure.  The repaired SQL is used only for AST extraction; the original
/// SQL is always preserved verbatim for graph storage.
///
/// This agent is intentionally narrow in scope: it only addresses syntax
/// incompatibilities so that <c>TSql160Parser</c> can produce a valid AST.
/// It does not alter business logic or naming conventions.
/// </summary>
internal sealed class AiSqlRepairAgent
{
    private static readonly string RepairSystemPrompt =
        """
        You are a SQL migration expert specialising in Sybase-to-SQL-Server conversions.
        Your sole task is to fix syntax errors so that the SQL can be parsed by SQL Server 2019 (TSql160Parser).

        STRICT RULES:
        - Return ONLY the corrected SQL. No explanations. No markdown. No code fences.
        - Preserve all object names, table names, column names, and procedure names exactly as-is.
        - Fix ONLY syntax that prevents parsing. Do not restructure logic.
        - Common Sybase patterns to fix:
            * Old-style parameter lists without @ prefix: add @ to each parameter name.
            * DECLARE CURSOR with unsupported options (e.g. FOR READ ONLY, HOLDLOCK): remove the option.
            * Any remaining *=, =* outer-join operators: convert to ANSI JOIN syntax.
            * RAISERROR <number> <msg>: convert to RAISERROR(<msg>, 16, 1).
        """;

    private readonly ChatProvider _chatProvider;
    private readonly ILogger<AiSqlRepairAgent> _logger;

    public AiSqlRepairAgent(ChatProvider chatProvider, ILogger<AiSqlRepairAgent> logger)
    {
        _chatProvider = chatProvider;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to produce a parse-clean version of <paramref name="sql"/> by
    /// asking the AI to fix the reported <paramref name="parseErrors"/>.
    /// </summary>
    /// <param name="sql">
    ///   The SQL that failed parsing (already preprocessed by
    ///   <see cref="LegacySqlPreprocessor"/>).
    /// </param>
    /// <param name="parseErrors">Errors from the first parse attempt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///   AI-repaired SQL string, or <see langword="null"/> if the repair call
    ///   failed or returned an empty response.
    /// </returns>
    public async Task<string?> RepairAsync(
        string sql,
        IList<ParseError> parseErrors,
        CancellationToken cancellationToken = default)
    {
        var errorSummary = string.Join("; ", parseErrors
            .Take(5)
            .Select(e => $"Line {e.Line}, Column {e.Column}: {e.Message}"));

        var userPrompt =
            $"""
            Fix the following T-SQL parse errors and return only the corrected SQL.

            PARSE ERRORS:
            {errorSummary}

            SQL TO FIX:
            {sql}
            """;

        try
        {
            _logger.LogDebug(
                "Invoking AI SQL repair. Error count: {Count}. First: {First}",
                parseErrors.Count, parseErrors[0].Message);

            var repaired = await _chatProvider.CompleteAsync(
                RepairSystemPrompt, userPrompt, cancellationToken);

            repaired = StripMarkdownFences(repaired);

            if (string.IsNullOrWhiteSpace(repaired))
            {
                _logger.LogDebug("AI repair returned an empty response.");
                return null;
            }

            _logger.LogDebug("AI repair succeeded. Repaired SQL length: {Len}.", repaired.Length);
            return repaired;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI SQL repair call failed.");
            return null;
        }
    }

    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.AsSpan().Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return text.Trim();

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return text.Trim();

        trimmed = trimmed[(firstNewline + 1)..].TrimStart();

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
            trimmed = trimmed[..^3].TrimEnd();

        return trimmed.ToString();
    }
}
