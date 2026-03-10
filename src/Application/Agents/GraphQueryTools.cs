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
                        ? $"No outbound call chain found for procedure '{name}' within {safeDepth} hop(s)."
                        : $"Procedures called by '{name}' (up to {safeDepth} hop(s)):\n{string.Join("\n", chain)}";
                },
                "expand_call_chain",
                "Traverses CALLS relationships outward from the given procedure name up to the specified depth (1–5) and returns all reachable callee procedure names."),

            AIFunctionFactory.Create(
                async (string name, int depth, CancellationToken ct) =>
                {
                    var safeDepth = Math.Clamp(depth, 1, 5);
                    var chain = await repository.GetCallerChainAsync(name, safeDepth, ct);
                    return chain.Count == 0
                        ? $"No callers found for procedure '{name}' within {safeDepth} hop(s)."
                        : $"Procedures that call '{name}' (up to {safeDepth} hop(s)):\n{string.Join("\n", chain)}";
                },
                "get_caller_chain",
                "Traverses CALLS relationships inward toward the given procedure name up to the specified depth (1–5) and returns all upstream callers. Use this for impact analysis before modifying a procedure."),

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

            AIFunctionFactory.Create(
                async (string procedureName, CancellationToken ct) =>
                {
                    var coupled = await repository.GetSharedTableProceduresAsync(procedureName, ct);
                    if (coupled.Count == 0)
                        return $"No data-coupled procedures found for '{procedureName}'.";

                    var lines = coupled.Select(c => $"- {c.ProcedureName} (via table: {c.SharedTableName})");
                    return $"Procedures data-coupled to '{procedureName}' via shared table writes:\n{string.Join("\n", lines)}";
                },
                "get_shared_table_procedures",
                "Returns all procedures coupled to the given procedure via SHARES_TABLE_WITH relationships — procedures that read from a table the given procedure writes to, or vice versa. Use this to find data dependencies and potential side-effects."),

            AIFunctionFactory.Create(
                async (string dataType, CancellationToken ct) =>
                {
                    var matches = await repository.FindProceduresByParameterTypeAsync(dataType, ct);
                    if (matches.Count == 0)
                        return $"No procedures found with a parameter of type '{dataType}'.";

                    var lines = matches.Select(m => $"- {m.ProcedureName}: {m.ParameterName} {m.DataType}");
                    return $"Procedures with a parameter of type '{dataType}':\n{string.Join("\n", lines)}";
                },
                "find_procedures_by_parameter_type",
                "Finds all stored procedures that declare at least one parameter whose SQL data type matches the given type (case-insensitive prefix match, e.g. 'INT', 'VARCHAR', 'UNIQUEIDENTIFIER'). Returns procedure name, parameter name, and full data type."),
        ];
    }

    /// <summary>
    /// All registered tool definitions available to the language model.
    /// </summary>
    public IReadOnlyList<AIFunction> All { get; }
}
