namespace RoZwet.Tools.StoreProc.Infrastructure.Ai;

/// <summary>
/// Configuration for Polly resilience pipelines applied to AI provider calls.
/// Bound from the <c>Ai:Resilience</c> configuration section.
/// </summary>
internal sealed record AiResilienceOptions
{
    /// <summary>Maximum number of retry attempts on transient failures (e.g. HTTP 429).</summary>
    public int MaxRetries { get; init; } = 6;

    /// <summary>Base exponential backoff delay in seconds before the first retry.</summary>
    public double BaseDelaySeconds { get; init; } = 2.0;

    /// <summary>Upper bound on the computed delay, preventing unbounded waits.</summary>
    public double MaxDelaySeconds { get; init; } = 60.0;
}
