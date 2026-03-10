using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.ClientModel;

namespace RoZwet.Tools.StoreProc.Infrastructure.Ai;

/// <summary>
/// Builds Polly <see cref="ResiliencePipeline{T}"/> instances for AI provider calls.
///
/// Strategy: exponential back-off with full jitter, retrying only on transient
/// HTTP 429 (rate-limit) and network-level failures.  All other exceptions
/// (400 Bad Request, 401 Unauthorized, etc.) surface immediately — no retry.
/// </summary>
internal static class AiResiliencePipelineFactory
{
    /// <summary>
    /// Creates a typed resilience pipeline for calls returning <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Return type of the protected operation.</typeparam>
    /// <param name="options">Retry tuning parameters.</param>
    /// <param name="logger">Logger used for structured retry diagnostics.</param>
    /// <param name="providerName">Human-readable label emitted in log messages (e.g. "Chat", "Embedding").</param>
    internal static ResiliencePipeline<T> Create<T>(
        AiResilienceOptions options,
        ILogger logger,
        string providerName)
    {
        return new ResiliencePipelineBuilder<T>()
            .AddRetry(new RetryStrategyOptions<T>
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(options.BaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.MaxDelaySeconds),

                ShouldHandle = new PredicateBuilder<T>()
                    .Handle<ClientResultException>(ex => ex.Status == 429)
                    .Handle<HttpRequestException>(),

                OnRetry = args =>
                {
                    logger.LogWarning(
                        "[{Provider}] Transient AI failure (attempt {Attempt}/{Max}). " +
                        "Retrying in {Delay:N1}s. Reason: {Reason}",
                        providerName,
                        args.AttemptNumber + 1,
                        options.MaxRetries,
                        args.RetryDelay.TotalSeconds,
                        args.Outcome.Exception?.Message ?? "unknown");

                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }
}
