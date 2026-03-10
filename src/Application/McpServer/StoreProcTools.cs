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
        "and returns the distinct names of all reachable procedures in the dependency chain.")]
    public async Task<string> ExpandCallChain(
        [Description("Root procedure name to start traversal from.")]
        string name,
        [Description("Maximum traversal depth (1 = direct callees only, up to 5).")]
        int depth,
        CancellationToken cancellationToken = default)
    {
        var safeDepth = Math.Clamp(depth, 1, 5);
        var chain = await _repository.ExpandCallChainAsync(name, safeDepth, cancellationToken);
        return chain.Count == 0
            ? $"No call chain found for procedure '{name}' within {safeDepth} hop(s)."
            : $"Procedures called by '{name}' (up to {safeDepth} hop(s)):\n{string.Join("\n", chain)}";
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
}
