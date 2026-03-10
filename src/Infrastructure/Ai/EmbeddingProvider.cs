using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;

namespace RoZwet.Tools.StoreProc.Infrastructure.Ai;

/// <summary>
/// Thin facade over <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> that exposes
/// a strongly-typed method returning a <see cref="float"/> array for Neo4j storage.
/// Wraps every call in a Polly resilience pipeline that retries on HTTP 429
/// (rate-limit) and transient network failures with exponential back-off + jitter.
/// </summary>
internal sealed class EmbeddingProvider
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger<EmbeddingProvider> _logger;
    private readonly ResiliencePipeline<float[]> _pipeline;

    public EmbeddingProvider(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        AiResilienceOptions resilienceOptions,
        ILogger<EmbeddingProvider> logger)
    {
        _generator = generator;
        _logger = logger;
        _pipeline = AiResiliencePipelineFactory.Create<float[]>(resilienceOptions, logger, "Embedding");
    }

    /// <summary>
    /// Generates a high-dimensional embedding vector for the given text.
    /// Retries automatically on transient failures as configured by <see cref="AiResilienceOptions"/>.
    /// </summary>
    public async Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot embed an empty string.", nameof(text));

        _logger.LogDebug("Generating embedding for text of length {Length}.", text.Length);

        var vector = await _pipeline.ExecuteAsync(
            async ct =>
            {
                var results = await _generator.GenerateAsync([text], cancellationToken: ct);
                return results[0].Vector.ToArray();
            },
            cancellationToken);

        _logger.LogDebug("Embedding generated: {Dims} dimensions.", vector.Length);
        return vector;
    }
}
