using System.ComponentModel;
using ModelContextProtocol.Server;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Application.Services;

namespace RoZwet.Tools.StoreProc.Application.McpServer;

/// <summary>
/// MCP tool definitions exposing the stored-procedure knowledge graph to any
/// MCP-compatible client (e.g. Cline, Claude Desktop).
///
/// Tool methods are discovered automatically by <c>WithToolsFromAssembly()</c>
/// and instantiated per-session via the host DI container.
/// </summary>
[McpServerToolType]
internal sealed class StoreProcTools
{
    private readonly HybridSearchService _searchService;
    private readonly INeo4jRepository _repository;

    public StoreProcTools(HybridSearchService searchService, INeo4jRepository repository)
    {
        _searchService = searchService;
        _repository   = repository;
    }

    [McpServerTool]
    [Description(
        "Performs a hybrid vector + graph search to find stored procedures semantically related " +
        "to the given natural-language query. Returns procedure names, schemas, full SQL bodies, " +
        "similarity scores, and 1-hop graph neighbors (procedures called by or calling the top results).")]
    public async Task<string> SearchProcedures(
        [Description("Natural-language query describing the business logic or behaviour you are looking for.")]
        string query,
        CancellationToken cancellationToken = default)
    {
        var ctx = await _searchService.SearchAsync(query, cancellationToken);
        return ctx.ToContextString();
    }

    [McpServerTool]
    [Description(
        "Returns the complete T-SQL body of a stored procedure identified by its exact name " +
        "(case-sensitive, without schema prefix). Use search_procedures first to discover names.")]
    public async Task<string> GetProcedureSql(
        [Description("Exact stored-procedure name without schema prefix, e.g. 'usp_GetOrder'.")]
        string name,
        CancellationToken cancellationToken = default)
    {
        var sql = await _repository.GetProcedureSqlAsync(name, cancellationToken);
        return sql ?? $"Procedure '{name}' was not found in the knowledge base.";
    }

    [McpServerTool]
    [Description(
        "Traverses CALLS relationships outward from the given procedure up to the specified depth " +
        "and returns the distinct names of all reachable callee procedures in the dependency chain.")]
    public async Task<string> ExpandCallChain(
        [Description("Root procedure name to start outbound traversal from.")]
        string name,
        [Description("Maximum traversal depth (1 = direct callees only, up to 5).")]
        int depth,
        CancellationToken cancellationToken = default)
    {
        var safeDepth = Math.Clamp(depth, 1, 5);
        var chain = await _repository.ExpandCallChainAsync(name, safeDepth, cancellationToken);
        return chain.Count == 0
            ? $"No outbound call chain found for procedure '{name}' within {safeDepth} hop(s)."
            : $"Procedures called by '{name}' (up to {safeDepth} hop(s)):\n{string.Join("\n", chain)}";
    }

    [McpServerTool]
    [Description(
        "Traverses CALLS relationships inward toward the given procedure up to the specified depth " +
        "and returns all upstream callers. Use this for impact analysis before modifying a procedure.")]
    public async Task<string> GetCallerChain(
        [Description("Target procedure name to find callers for.")]
        string name,
        [Description("Maximum traversal depth (1 = direct callers only, up to 5).")]
        int depth,
        CancellationToken cancellationToken = default)
    {
        var safeDepth = Math.Clamp(depth, 1, 5);
        var chain = await _repository.GetCallerChainAsync(name, safeDepth, cancellationToken);
        return chain.Count == 0
            ? $"No callers found for procedure '{name}' within {safeDepth} hop(s)."
            : $"Procedures that call '{name}' (up to {safeDepth} hop(s)):\n{string.Join("\n", chain)}";
    }

    [McpServerTool]
    [Description(
        "Returns all stored procedures that read from or write to the given table " +
        "via READS_FROM or WRITES_TO graph relationships.")]
    public async Task<string> GetTableUsage(
        [Description("Table name to search for references, e.g. 'Orders' or 'dbo.Orders'.")]
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var procs = await _repository.GetTableUsageAsync(tableName, cancellationToken);
        return procs.Count == 0
            ? $"No stored procedures found that reference table '{tableName}'."
            : $"Procedures referencing table '{tableName}':\n{string.Join("\n", procs)}";
    }

    [McpServerTool]
    [Description(
        "Returns all procedures coupled to the given procedure via SHARES_TABLE_WITH relationships — " +
        "procedures that read from a table the given procedure writes to, or vice versa. " +
        "Use this to discover data dependencies and potential side-effects of a change.")]
    public async Task<string> GetSharedTableProcedures(
        [Description("Procedure name to find data-coupled peers for.")]
        string procedureName,
        CancellationToken cancellationToken = default)
    {
        var coupled = await _repository.GetSharedTableProceduresAsync(procedureName, cancellationToken);
        if (coupled.Count == 0)
            return $"No data-coupled procedures found for '{procedureName}'.";

        var lines = coupled.Select(c => $"- {c.ProcedureName} (via table: {c.SharedTableName})");
        return $"Procedures data-coupled to '{procedureName}' via shared table writes:\n{string.Join("\n", lines)}";
    }

    [McpServerTool]
    [Description(
        "Finds all stored procedures that declare at least one parameter whose SQL data type " +
        "matches the given type (case-insensitive prefix match). " +
        "Returns procedure name, parameter name, and full data type for each match.")]
    public async Task<string> FindProceduresByParameterType(
        [Description("SQL data type to search for, e.g. 'INT', 'VARCHAR', 'UNIQUEIDENTIFIER'. Prefix match is used.")]
        string dataType,
        CancellationToken cancellationToken = default)
    {
        var matches = await _repository.FindProceduresByParameterTypeAsync(dataType, cancellationToken);
        if (matches.Count == 0)
            return $"No procedures found with a parameter of type '{dataType}'.";

        var lines = matches.Select(m => $"- {m.ProcedureName}: {m.ParameterName} {m.DataType}");
        return $"Procedures with a parameter of type '{dataType}':\n{string.Join("\n", lines)}";
    }
}
