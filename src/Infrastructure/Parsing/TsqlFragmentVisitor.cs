using Microsoft.SqlServer.TransactSql.ScriptDom;
using RoZwet.Tools.StoreProc.Domain;

namespace RoZwet.Tools.StoreProc.Infrastructure.Parsing;

/// <summary>
/// AST visitor that walks a T-SQL fragment to extract procedure call relationships
/// and table read/write dependencies from a stored procedure body.
/// </summary>
internal sealed class StoredProcedureVisitor : TSqlFragmentVisitor
{
    private static readonly IReadOnlySet<string> WriteStatementKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "BULK INSERT"
    };

    private readonly List<ProcedureCall> _calls = [];
    private readonly List<TableDependency> _tableDependencies = [];
    private bool _inWriteContext;

    /// <summary>Procedure calls discovered during the visit.</summary>
    public IReadOnlyList<ProcedureCall> Calls => _calls.AsReadOnly();

    /// <summary>Table dependencies discovered during the visit.</summary>
    public IReadOnlyList<TableDependency> TableDependencies => _tableDependencies.AsReadOnly();

    /// <inheritdoc />
    public override void Visit(ExecuteStatement node)
    {
        if (node.ExecuteSpecification?.ExecutableEntity is ExecutableProcedureReference procRef)
        {
            var schemaObject = procRef.ProcedureReference?.ProcedureReference?.Name;
            if (schemaObject is not null)
            {
                var name = schemaObject.BaseIdentifier?.Value ?? string.Empty;
                var schema = schemaObject.SchemaIdentifier?.Value ?? "dbo";

                if (!string.IsNullOrWhiteSpace(name))
                    _calls.Add(new ProcedureCall(name, schema));
            }
        }

        base.Visit(node);
    }

    /// <inheritdoc />
    public override void Visit(InsertStatement node)
    {
        _inWriteContext = true;
        base.Visit(node);
        _inWriteContext = false;
    }

    /// <inheritdoc />
    public override void Visit(UpdateStatement node)
    {
        _inWriteContext = true;
        base.Visit(node);
        _inWriteContext = false;
    }

    /// <inheritdoc />
    public override void Visit(DeleteStatement node)
    {
        _inWriteContext = true;
        base.Visit(node);
        _inWriteContext = false;
    }

    /// <inheritdoc />
    public override void Visit(MergeStatement node)
    {
        _inWriteContext = true;
        base.Visit(node);
        _inWriteContext = false;
    }

    /// <inheritdoc />
    public override void Visit(NamedTableReference node)
    {
        var name = node.SchemaObject?.BaseIdentifier?.Value;
        var schema = node.SchemaObject?.SchemaIdentifier?.Value ?? "dbo";

        if (string.IsNullOrWhiteSpace(name))
            return;

        var accessType = _inWriteContext ? TableAccessType.Write : TableAccessType.Read;
        var dependency = new TableDependency(name, schema, accessType);

        if (!_tableDependencies.Contains(dependency))
            _tableDependencies.Add(dependency);
    }
}
