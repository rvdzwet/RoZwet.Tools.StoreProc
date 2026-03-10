using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RoZwet.Tools.StoreProc.Infrastructure.Ai;

/// <summary>
/// Thin facade over <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> that exposes
/// a strongly-typed method returning a <see cref="float"/> array for Neo4j storage.
/// </summary>
internal sealed class EmbeddingProvider
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger<EmbeddingProvider> _logger;

    public EmbeddingProvider(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        ILogger<EmbeddingProvider> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    /// <summary>
    /// Generates a 1024-dimensional embedding vector for the given text.
    /// </summary>
    public async Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot embed an empty string.", nameof(text));

        _logger.LogDebug("Generating embedding for text of length {Length}.", text.Length);

        var results = await _generator.GenerateAsync([text], cancellationToken: cancellationToken);

        var vector = results[0].Vector.ToArray();

        _logger.LogDebug("Embedding generated: {Dims} dimensions.", vector.Length);

        return vector;
    }
}
