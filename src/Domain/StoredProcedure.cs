namespace RoZwet.Tools.StoreProc.Domain;

/// <summary>
/// Aggregate root representing a parsed stored procedure with its semantic embedding and dependency graph.
/// </summary>
public sealed class StoredProcedure
{
    private readonly List<ProcedureCall> _calls = [];
    private readonly List<TableDependency> _tableDependencies = [];

    /// <summary>
    /// Initializes a stored procedure with its identity and raw SQL.
    /// </summary>
    public StoredProcedure(string name, string schema, string sql)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Procedure name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL body cannot be empty.", nameof(sql));

        Name = name;
        Schema = string.IsNullOrWhiteSpace(schema) ? "dbo" : schema;
        Sql = sql;
    }

    /// <summary>Procedure name without schema qualifier.</summary>
    public string Name { get; }

    /// <summary>Schema owning this procedure.</summary>
    public string Schema { get; }

    /// <summary>Full raw SQL body.</summary>
    public string Sql { get; }

    /// <summary>Semantic vector embedding (1024 dimensions).</summary>
    public float[]? Embedding { get; private set; }

    /// <summary>Read-only view of procedures this procedure calls.</summary>
    public IReadOnlyList<ProcedureCall> Calls => _calls.AsReadOnly();

    /// <summary>Read-only view of tables this procedure reads from or writes to.</summary>
    public IReadOnlyList<TableDependency> TableDependencies => _tableDependencies.AsReadOnly();

    /// <summary>
    /// Applies the semantic embedding produced by the analysis agent.
    /// </summary>
    public void ApplyEmbedding(float[] embedding)
    {
        if (embedding.Length == 0)
            throw new ArgumentException("Embedding vector cannot be empty.", nameof(embedding));

        Embedding = embedding;
    }

    /// <summary>
    /// Registers a procedure call dependency discovered during SQL parsing.
    /// </summary>
    public void AddCall(ProcedureCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_calls.Contains(call))
            _calls.Add(call);
    }

    /// <summary>
    /// Registers a table dependency discovered during SQL parsing.
    /// </summary>
    public void AddTableDependency(TableDependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (!_tableDependencies.Contains(dependency))
            _tableDependencies.Add(dependency);
    }

    /// <summary>
    /// Returns the fully-qualified name as [Schema].[Name].
    /// </summary>
    public string FullyQualifiedName => $"[{Schema}].[{Name}]";
}
