using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using RoZwet.Tools.StoreProc.Domain;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;
using RoZwet.Tools.StoreProc.Infrastructure.Parsing;

namespace RoZwet.Tools.StoreProc.Application.Agents;

/// <summary>
/// The result of a Tier-1 (deterministic) parse attempt against a SQL file.
/// </summary>
/// <param name="Success">True when the AST was produced with zero parse errors.</param>
/// <param name="Procedure">Populated domain aggregate when <paramref name="Success"/> is true.</param>
/// <param name="OriginalSql">Raw SQL as read from disk.</param>
/// <param name="NormalizedSql">SQL after <see cref="LegacySqlPreprocessor"/> transforms.</param>
/// <param name="Errors">Parse errors remaining after Tier-1; non-empty when <paramref name="Success"/> is false.</param>
internal sealed record Tier1Result(
    bool Success,
    StoredProcedure? Procedure,
    string OriginalSql,
    string NormalizedSql,
    IList<ParseError> Errors);

/// <summary>
/// Agent responsible for the analysis pipeline of a single SQL file.
///
/// Parse resilience follows a two-tier strategy:
/// <list type="number">
///   <item>
///     Tier 1 — <see cref="LegacySqlPreprocessor"/> applies deterministic regex
///     transforms for known Sybase patterns before handing SQL to
///     <c>TSql160Parser</c>.  Result available via <see cref="TryTier1Async"/>.
///   </item>
///   <item>
///     Tier 2 — If Tier-1 still leaves parse errors, <see cref="AiSqlRepairAgent"/>
///     sends the SQL to the LLM and retries.  Invoked via
///     <see cref="RepairAndCompleteAsync"/> — intended to run as a background task.
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
        _repairAgent       = repairAgent;
        _logger            = logger;
    }

    /// <summary>
    /// Attempts Tier-1 parsing (deterministic only — no AI).
    /// Returns immediately: success path builds the procedure scaffold;
    /// failure path returns the normalized SQL and errors for the background repair agent.
    /// </summary>
    public async Task<Tier1Result> TryTier1Async(
        string sqlFilePath,
        CancellationToken cancellationToken = default)
    {
        var sql = await File.ReadAllTextAsync(sqlFilePath, cancellationToken);

        if (string.IsNullOrWhiteSpace(sql))
        {
            _logger.LogWarning("Skipping empty file: {FilePath}", sqlFilePath);
            return new Tier1Result(false, null, sql, sql, Array.Empty<ParseError>());
        }

        var normalizedSql = LegacySqlPreprocessor.Normalize(sql);
        var parser        = new TSql160Parser(initialQuotedIdentifiers: true);

        using var reader = new StringReader(normalizedSql);
        var fragment     = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            _logger.LogInformation(
                "Tier-1 left {Count} parse error(s) in '{File}'. First: {Msg}. Dispatching to background repair.",
                errors.Count, Path.GetFileName(sqlFilePath), errors[0].Message);

            return new Tier1Result(false, null, sql, normalizedSql, errors);
        }

        var procedure = BuildProcedure(fragment, sql, sqlFilePath);
        if (procedure is null)
        {
            _logger.LogWarning(
                "No CREATE PROCEDURE statement found in '{FilePath}'. Skipping.", sqlFilePath);
            return new Tier1Result(false, null, sql, normalizedSql, errors);
        }

        return new Tier1Result(true, procedure, sql, normalizedSql, errors);
    }

    /// <summary>
    /// Generates and applies a semantic embedding to an already-parsed procedure.
    /// Mutates <paramref name="procedure"/> in place and returns the same instance.
    /// </summary>
    public async Task<StoredProcedure> ApplyEmbeddingAsync(
        StoredProcedure procedure,
        string originalSql,
        CancellationToken cancellationToken = default)
    {
        var embedding = await _embeddingProvider.GenerateAsync(originalSql, cancellationToken);
        procedure.ApplyEmbedding(embedding);

        _logger.LogDebug(
            "Embedding applied to '{Name}': {Calls} calls, {Tables} table deps.",
            procedure.Name, procedure.Calls.Count, procedure.TableDependencies.Count);

        return procedure;
    }

    /// <summary>
    /// Runs the Tier-2 AI repair loop, then builds the procedure aggregate and applies embedding.
    /// Designed to be called as a fire-and-forget background task from
    /// <c>PipelineOrchestrator</c>.
    /// Returns <see langword="null"/> when AI repair is exhausted or produces an invalid AST.
    /// </summary>
    public async Task<StoredProcedure?> RepairAndCompleteAsync(
        string sqlFilePath,
        string normalizedSql,
        IList<ParseError> errors,
        string originalSql,
        CancellationToken cancellationToken = default)
    {
        var parser     = new TSql160Parser(initialQuotedIdentifiers: true);
        var currentSql = normalizedSql;
        var currentErrors = errors;
        TSqlFragment? fragment = null;

        const int MaxRepairRounds = 10;

        for (int round = 1; round <= MaxRepairRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var repairedSql = await _repairAgent.RepairAsync(
                currentSql, currentErrors, round, MaxRepairRounds, cancellationToken);

            if (repairedSql is null)
            {
                _logger.LogWarning(
                    "[BG-REPAIR] AI returned null at round {Round}/{Max} for '{File}'. {Count} error(s) remain.",
                    round, MaxRepairRounds, Path.GetFileName(sqlFilePath), currentErrors.Count);
                break;
            }

            using var repairedReader = new StringReader(repairedSql);
            var repairedFragment     = parser.Parse(repairedReader, out var repairedErrors);

            if (repairedErrors.Count == 0)
            {
                _logger.LogInformation(
                    "[BG-REPAIR] Succeeded for '{File}' after {Round} round(s).",
                    Path.GetFileName(sqlFilePath), round);
                fragment = repairedFragment;
                currentErrors = repairedErrors;
                break;
            }

            if (repairedErrors.Count >= currentErrors.Count)
            {
                _logger.LogWarning(
                    "[BG-REPAIR] Stalled at round {Round}/{Max} for '{File}': error count unchanged at {Count}.",
                    round, MaxRepairRounds, Path.GetFileName(sqlFilePath), repairedErrors.Count);
                fragment      = repairedFragment;
                currentErrors = repairedErrors;
                break;
            }

            _logger.LogInformation(
                "[BG-REPAIR] Round {Round}/{Max}: errors {Before} → {After} in '{File}'.",
                round, MaxRepairRounds, currentErrors.Count, repairedErrors.Count,
                Path.GetFileName(sqlFilePath));

            fragment      = repairedFragment;
            currentErrors = repairedErrors;
            currentSql    = repairedSql;

            if (round == MaxRepairRounds)
            {
                _logger.LogWarning(
                    "[BG-REPAIR] Max {Max} rounds reached for '{File}'. {Count} error(s) remain.",
                    MaxRepairRounds, Path.GetFileName(sqlFilePath), currentErrors.Count);
            }
        }

        if (fragment is null || currentErrors.Count > 0)
            return null;

        var procedure = BuildProcedure(fragment, originalSql, sqlFilePath);
        if (procedure is null)
        {
            _logger.LogWarning(
                "[BG-REPAIR] No CREATE PROCEDURE found in repaired AST for '{FilePath}'.", sqlFilePath);
            return null;
        }

        return await ApplyEmbeddingAsync(procedure, originalSql, cancellationToken);
    }

    private StoredProcedure? BuildProcedure(
        TSqlFragment fragment,
        string originalSql,
        string filePath)
    {
        var createProc = FindCreateProcedureStatement(fragment);
        if (createProc is null)
            return null;

        var name   = createProc.ProcedureReference?.Name?.BaseIdentifier?.Value
                     ?? Path.GetFileNameWithoutExtension(filePath);
        var schema = createProc.ProcedureReference?.Name?.SchemaIdentifier?.Value ?? "dbo";

        var procedure = new StoredProcedure(name, schema, originalSql);

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
