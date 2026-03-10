namespace RoZwet.Tools.StoreProc.Domain;

/// <summary>
/// Describes the nature of a stored procedure's relationship to a database table.
/// </summary>
public enum TableAccessType
{
    /// <summary>Procedure reads data from the table (SELECT / FROM / JOIN).</summary>
    Read,

    /// <summary>Procedure writes data to the table (INSERT / UPDATE / DELETE / MERGE).</summary>
    Write
}

/// <summary>
/// Immutable value object representing a table dependency for a stored procedure.
/// Equality is determined by table name, schema, and access type.
/// </summary>
public sealed class TableDependency : IEquatable<TableDependency>
{
    /// <summary>
    /// Initializes a table dependency value object.
    /// </summary>
    public TableDependency(string tableName, string tableSchema, TableAccessType accessType)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be empty.", nameof(tableName));

        TableName = tableName;
        TableSchema = string.IsNullOrWhiteSpace(tableSchema) ? "dbo" : tableSchema;
        AccessType = accessType;
    }

    /// <summary>Name of the table.</summary>
    public string TableName { get; }

    /// <summary>Schema owning the table.</summary>
    public string TableSchema { get; }

    /// <summary>Whether the procedure reads from or writes to this table.</summary>
    public TableAccessType AccessType { get; }

    /// <inheritdoc />
    public bool Equals(TableDependency? other) =>
        other is not null &&
        string.Equals(TableName, other.TableName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(TableSchema, other.TableSchema, StringComparison.OrdinalIgnoreCase) &&
        AccessType == other.AccessType;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as TableDependency);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            TableName.ToUpperInvariant(),
            TableSchema.ToUpperInvariant(),
            AccessType);

    /// <inheritdoc />
    public override string ToString() => $"[{TableSchema}].[{TableName}] ({AccessType})";
}
