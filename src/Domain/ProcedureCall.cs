namespace RoZwet.Tools.StoreProc.Domain;

/// <summary>
/// Immutable value object representing a call from one stored procedure to another.
/// Equality is determined by the callee name and schema.
/// </summary>
public sealed class ProcedureCall : IEquatable<ProcedureCall>
{
    /// <summary>
    /// Initializes a procedure call value object.
    /// </summary>
    public ProcedureCall(string calleeName, string calleeSchema)
    {
        if (string.IsNullOrWhiteSpace(calleeName))
            throw new ArgumentException("Callee name cannot be empty.", nameof(calleeName));

        CalleeName = calleeName;
        CalleeSchema = string.IsNullOrWhiteSpace(calleeSchema) ? "dbo" : calleeSchema;
    }

    /// <summary>Name of the procedure being called.</summary>
    public string CalleeName { get; }

    /// <summary>Schema of the procedure being called.</summary>
    public string CalleeSchema { get; }

    /// <inheritdoc />
    public bool Equals(ProcedureCall? other) =>
        other is not null &&
        string.Equals(CalleeName, other.CalleeName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(CalleeSchema, other.CalleeSchema, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ProcedureCall);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            CalleeName.ToUpperInvariant(),
            CalleeSchema.ToUpperInvariant());

    /// <inheritdoc />
    public override string ToString() => $"[{CalleeSchema}].[{CalleeName}]";
}
