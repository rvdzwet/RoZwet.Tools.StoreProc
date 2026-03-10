using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using RoZwet.Tools.StoreProc.Domain;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;
using RoZwet.Tools.StoreProc.Infrastructure.Parsing;

namespace RoZwet.Tools.StoreProc.Application.Agents;

/// <summary>
/// Agent responsible for the full analysis pipeline of a single SQL file:
/// parse the AST, extract dependencies, and generate a semantic embedding.
///
/// Parse resilience follows a two-tier strategy:
/// <list type="number">
///   <item>
///     Tier 1 — <see cref="LegacySqlPreprocessor"/> applies deterministic regex
///     transforms for known Sybase patterns before handing SQL to
///     <c>TSql160Parser</c>.
///   </item>
///   <item>
///     Tier 2 — If parsing still fails, <see cref="AiSqlRepairAgent"/> sends the
///     SQL and error messages to the configured LLM and retries parsing on the
///     AI-repaired version.  The original SQL is always preserved for graph storage.
///   </item>
/// </list>
///
/// Stateless — all produced state is returned as a <see cref="StoredProcedure"/> aggregate.
/// </summary>
internal sealed class SqlAnalysisAgent
{
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly AiSqlRepairAgent _repairAgent;
    private readonly ILogger<SqlAnalysisAgent> _logger;

    public SqlAnalysisAgent(
        EmbeddingProvider embeddingProvider,
        AiSqlRepairAgent repairAgent,
        ILogger<SqlAnalysisAgent> logger)
    {
        _embeddingProvider = embeddingProvider;
        _repairAgent = repairAgent;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes a stored procedure SQL file and returns a fully enriched domain aggregate.
    /// Returns <see langword="null"/> if the file cannot be parsed as a valid stored procedure
    /// even after AI-assisted repair.
    /// </summary>
    public async Task<StoredProcedure?> AnalyzeAsync(
        string sqlFilePath,
        CancellationToken cancellationToken = default)
    {
        var sql = await File.ReadAllTextAsync(sqlFilePath, cancellationToken);

        if (string.IsNullOrWhiteSpace(sql))
        {
            _logger.LogWarning("Skipping empty file: {FilePath}", sqlFilePath);
            return null;
        }

        var procedure = await ParseProcedureAsync(sql, sqlFilePath, cancellationToken);
        if (procedure is null)
            return null;

        var embedding = await _embeddingProvider.GenerateAsync(sql, cancellationToken);
        procedure.ApplyEmbedding(embedding);

        _logger.LogDebug(
            "Analyzed '{Name}': {Calls} calls, {Tables} table deps, embedding applied.",
            procedure.Name, procedure.Calls.Count, procedure.TableDependencies.Count);

        return procedure;
    }

    private async Task<StoredProcedure?> ParseProcedureAsync(
        string sql,
        string filePath,
        CancellationToken cancellationToken)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var normalizedSql = LegacySqlPreprocessor.Normalize(sql);

        using var reader = new StringReader(normalizedSql);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            _logger.LogInformation(
                "Tier-1 preprocessor left {Count} parse error(s) in '{FilePath}'. First: {First}. Escalating to AI repair.",
                errors.Count, filePath, errors[0].Message);

            var repairedSql = await _repairAgent.RepairAsync(normalizedSql, errors, cancellationToken);

            if (repairedSql is not null)
            {
                using var repairedReader = new StringReader(repairedSql);
                var repairedFragment = parser.Parse(repairedReader, out var repairedErrors);

                if (repairedErrors.Count == 0)
                {
                    _logger.LogInformation(
                        "AI repair succeeded for '{FilePath}': all parse errors resolved. Proceeding with repaired AST.",
                        filePath);
                    fragment = repairedFragment;
                    errors = repairedErrors;
                }
                else
                {
                    _logger.LogWarning(
                        "AI repair for '{FilePath}' reduced errors from {Before} to {After}. Remaining: {First}. Proceeding with best-effort AST.",
                        filePath, errors.Count, repairedErrors.Count, repairedErrors[0].Message);
                    fragment = repairedFragment;
                }
            }
            else
            {
                _logger.LogWarning(
                    "AI repair unavailable for '{FilePath}'. Original {Count} parse error(s) remain. First: {First}. Proceeding with partial AST.",
                    filePath, errors.Count, errors[0].Message);
            }
        }

        var createProc = FindCreateProcedureStatement(fragment);
        if (createProc is null)
        {
            _logger.LogWarning("No CREATE PROCEDURE statement found in '{FilePath}'. Skipping.", filePath);
            return null;
        }

        var name = createProc.ProcedureReference?.Name?.BaseIdentifier?.Value
                   ?? Path.GetFileNameWithoutExtension(filePath);
        var schema = createProc.ProcedureReference?.Name?.SchemaIdentifier?.Value ?? "dbo";

        var procedure = new StoredProcedure(name, schema, sql);

        var visitor = new StoredProcedureVisitor();
        fragment.Accept(visitor);

        foreach (var call in visitor.Calls)
            procedure.AddCall(call);

        foreach (var dep in visitor.TableDependencies)
            procedure.AddTableDependency(dep);

        return procedure;
    }

    private static CreateProcedureStatement? FindCreateProcedureStatement(TSqlFragment fragment)
    {
        if (fragment is not TSqlScript script)
            return null;

        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                if (statement is CreateProcedureStatement createProc)
                    return createProc;
            }
        }

        return null;
    }
}
