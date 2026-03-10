using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using RoZwet.Tools.StoreProc.Domain;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;
using RoZwet.Tools.StoreProc.Infrastructure.Parsing;

namespace RoZwet.Tools.StoreProc.Application.Agents;

/// <summary>
/// Agent responsible for the full analysis pipeline of a single SQL file:
/// parse the AST, extract dependencies, and generate a semantic embedding.
/// Stateless — all produced state is returned as a <see cref="StoredProcedure"/> aggregate.
/// </summary>
internal sealed class SqlAnalysisAgent
{
    private readonly EmbeddingProvider _embeddingProvider;
    private readonly ILogger<SqlAnalysisAgent> _logger;

    public SqlAnalysisAgent(
        EmbeddingProvider embeddingProvider,
        ILogger<SqlAnalysisAgent> logger)
    {
        _embeddingProvider = embeddingProvider;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes a stored procedure SQL file and returns a fully enriched domain aggregate.
    /// Returns <see langword="null"/> if the file cannot be parsed as a valid stored procedure.
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

        var procedure = ParseProcedure(sql, sqlFilePath);
        if (procedure is null)
            return null;

        var embedding = await _embeddingProvider.GenerateAsync(sql, cancellationToken);
        procedure.ApplyEmbedding(embedding);

        _logger.LogDebug(
            "Analyzed '{Name}': {Calls} calls, {Tables} table deps, embedding applied.",
            procedure.Name, procedure.Calls.Count, procedure.TableDependencies.Count);

        return procedure;
    }

    private StoredProcedure? ParseProcedure(string sql, string filePath)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);

        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "Parse errors in '{FilePath}': {ErrorCount} error(s). First: {FirstError}",
                filePath, errors.Count, errors[0].Message);
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
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                foreach (var statement in batch.Statements)
                {
                    if (statement is CreateProcedureStatement createProc)
                        return createProc;
                }
            }
        }

        return null;
    }
}
