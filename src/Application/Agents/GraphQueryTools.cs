using Microsoft.Extensions.AI;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Application.Services;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;

namespace RoZwet.Tools.StoreProc.Application.Agents;

/// <summary>
/// Exposes the Neo4j graph knowledge base as <see cref="AIFunction"/> tools
/// that a language model can invoke during an agentic reasoning loop.
/// </summary>
internal sealed class GraphQueryTools
{
    public GraphQueryTools(
        HybridSearchService searchService,
        INeo4jRepository repository)
    {
        All =
        [
            AIFunctionFactory.Create(
                async (string query, CancellationToken ct) =>
                {
                    var ctx = await searchService.SearchAsync(query, ct);
                    return ctx.ToContextString();
                },
                "search_procedures",
                "Performs a hybrid vector+graph search to find stored procedures semantically related to the given natural-language query. Returns procedure names, schemas, SQL bodies, similarity scores, and 1-hop graph neighbors."),

            AIFunctionFactory.Create(
                async (string name, CancellationToken ct) =>
                {
                    var sql = await repository.GetProcedureSqlAsync(name, ct);
                    return sql is null
                        ? $"Procedure '{name}' was not found in the knowledge base."
                        : sql;
                },
                "get_procedure_sql",
                "Returns the complete SQL body of a stored procedure identified by its exact name (case-sensitive, without schema prefix)."),

            AIFunctionFactory.Create(
                async (string name, int depth, CancellationToken ct) =>
                {
                    var safeDepth = Math.Clamp(depth, 1, 5);
                    var chain = await repository.ExpandCallChainAsync(name, safeDepth, ct);
                    return chain.Count == 0
                        ? $"No call chain found for procedure '{name}' within {safeDepth} hop(s)."
                        : $"Procedures called by '{name}' (up to {safeDepth} hop(s)):\n{string.Join("\n", chain)}";
                },
                "expand_call_chain",
                "Traverses CALLS relationships from the given procedure name up to the specified depth (1–5) and returns all reachable procedure names in the dependency chain."),

            AIFunctionFactory.Create(
                async (string tableName, CancellationToken ct) =>
                {
                    var procs = await repository.GetTableUsageAsync(tableName, ct);
                    return procs.Count == 0
                        ? $"No stored procedures found that reference table '{tableName}'."
                        : $"Procedures referencing table '{tableName}':\n{string.Join("\n", procs)}";
                },
                "get_table_usage",
                "Returns all stored procedures that read from or write to the given table name via READS_FROM or WRITES_TO relationships."),
        ];
    }

    /// <summary>
    /// All registered tool definitions available to the language model.
    /// </summary>
    public IReadOnlyList<AIFunction> All { get; }
}
