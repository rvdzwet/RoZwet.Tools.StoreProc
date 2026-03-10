using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace RoZwet.Tools.StoreProc.Infrastructure.Neo4j;

/// <summary>
/// Bootstraps the Neo4j schema: unique constraints and the vector index for semantic search.
/// All operations are idempotent — safe to call on every application startup.
/// </summary>
internal sealed class Neo4jIndexInitializer
{
    private const string VectorIndexName = "procedure_embeddings";
    private const int EmbeddingDimensions = 1024;

    private readonly IDriver _driver;
    private readonly ILogger<Neo4jIndexInitializer> _logger;

    public Neo4jIndexInitializer(IDriver driver, ILogger<Neo4jIndexInitializer> logger)
    {
        _driver = driver;
        _logger = logger;
    }

    /// <summary>
    /// Applies all schema definitions to Neo4j.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing Neo4j schema...");

        await using var session = _driver.AsyncSession();

        await ApplyConstraintAsync(session,
            "CREATE CONSTRAINT procedure_name_unique IF NOT EXISTS FOR (p:Procedure) REQUIRE p.name IS UNIQUE",
            "procedure_name_unique",
            cancellationToken);

        await ApplyConstraintAsync(session,
            "CREATE CONSTRAINT table_name_unique IF NOT EXISTS FOR (t:Table) REQUIRE t.name IS UNIQUE",
            "table_name_unique",
            cancellationToken);

        await ApplyVectorIndexAsync(session, cancellationToken);

        _logger.LogInformation("Neo4j schema initialization complete.");
    }

    private async Task ApplyConstraintAsync(
        IAsyncSession session,
        string cypher,
        string constraintName,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.RunAsync(cypher);
            _logger.LogDebug("Constraint {ConstraintName} applied.", constraintName);
        }
        catch (ClientException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Constraint {ConstraintName} already exists — skipping.", constraintName);
        }
    }

    private async Task ApplyVectorIndexAsync(IAsyncSession session, CancellationToken cancellationToken)
    {
        // Cypher uses curly-brace syntax for OPTIONS — plain raw string avoids interpolation conflicts.
        // Values are manifest constants: index name = "procedure_embeddings", dims = 1024.
        const string cypher = """
            CREATE VECTOR INDEX procedure_embeddings IF NOT EXISTS
            FOR (p:Procedure) ON (p.embedding)
            OPTIONS {
              indexConfig: {
                `vector.dimensions`: 1024,
                `vector.similarity_function`: 'cosine'
              }
            }
            """;

        try
        {
            await session.RunAsync(cypher);
            _logger.LogInformation(
                "Vector index '{IndexName}' created (dims={Dims}, similarity=cosine).",
                VectorIndexName, EmbeddingDimensions);
        }
        catch (ClientException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Vector index '{IndexName}' already exists — skipping.", VectorIndexName);
        }
    }
}
