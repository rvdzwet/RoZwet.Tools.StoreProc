namespace RoZwet.Tools.StoreProc.Domain;

/// <summary>
/// Immutable value object representing a declared input or output parameter of a stored procedure.
/// Equality is determined by parameter name (case-insensitive).
/// </summary>
public sealed class ProcedureParameter : IEquatable<ProcedureParameter>
{
    /// <summary>
    /// Initializes a procedure parameter value object.
    /// </summary>
    public ProcedureParameter(string name, string dataType, bool isOutput, bool hasDefault)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Parameter name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(dataType))
            throw new ArgumentException("Parameter data type cannot be empty.", nameof(dataType));

        Name = name;
        DataType = dataType;
        IsOutput = isOutput;
        HasDefault = hasDefault;
    }

    /// <summary>Parameter name including the leading @ sigil (e.g. @CustomerId).</summary>
    public string Name { get; }

    /// <summary>SQL data type as a normalized string (e.g. INT, VARCHAR(50), UNIQUEIDENTIFIER).</summary>
    public string DataType { get; }

    /// <summary>True when the parameter carries the OUTPUT or READONLY modifier.</summary>
    public bool IsOutput { get; }

    /// <summary>True when the parameter declares a DEFAULT value expression.</summary>
    public bool HasDefault { get; }

    /// <inheritdoc />
    public bool Equals(ProcedureParameter? other) =>
        other is not null &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ProcedureParameter);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Name.ToUpperInvariant());

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name} {DataType}{(IsOutput ? " OUTPUT" : string.Empty)}{(HasDefault ? " = <default>" : string.Empty)}";
}
