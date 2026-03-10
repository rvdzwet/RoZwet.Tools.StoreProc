using Microsoft.Extensions.Configuration;
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

    private readonly IDriver _driver;
    private readonly ILogger<Neo4jIndexInitializer> _logger;
    private readonly int _embeddingDimensions;

    public Neo4jIndexInitializer(
        IDriver driver,
        IConfiguration config,
        ILogger<Neo4jIndexInitializer> logger)
    {
        _driver = driver;
        _logger = logger;
        _embeddingDimensions = int.TryParse(config["Ai:Embedding:Dimensions"], out var d) && d > 0
            ? d
            : 1024;
    }

    /// <summary>
    /// Applies all schema definitions to Neo4j.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing Neo4j schema (embedding dims={Dims})...", _embeddingDimensions);

        await using var session = _driver.AsyncSession();

        await ApplyConstraintAsync(session,
            "CREATE CONSTRAINT procedure_name_unique IF NOT EXISTS FOR (p:Procedure) REQUIRE p.name IS UNIQUE",
            "procedure_name_unique",
            cancellationToken);

        await ApplyConstraintAsync(session,
            "CREATE CONSTRAINT table_name_unique IF NOT EXISTS FOR (t:Table) REQUIRE t.name IS UNIQUE",
            "table_name_unique",
            cancellationToken);

        await ApplyConstraintAsync(session,
            "CREATE CONSTRAINT parameter_identity_unique IF NOT EXISTS FOR (param:Parameter) REQUIRE (param.procedureName, param.name) IS NODE KEY",
            "parameter_identity_unique",
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
        // $$"""...""" raw string: single { } are literal Cypher braces; {{expr}} is the C# interpolation.
        // _embeddingDimensions is a validated positive int — not user-controlled input.
        var cypher = $$"""
            CREATE VECTOR INDEX procedure_embeddings IF NOT EXISTS
            FOR (p:Procedure) ON (p.embedding)
            OPTIONS {
              indexConfig: {
                `vector.dimensions`: {{_embeddingDimensions}},
                `vector.similarity_function`: 'cosine'
              }
            }
            """;

        try
        {
            await session.RunAsync(cypher);
            _logger.LogInformation(
                "Vector index '{IndexName}' created (dims={Dims}, similarity=cosine).",
                VectorIndexName, _embeddingDimensions);
        }
        catch (ClientException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Vector index '{IndexName}' already exists — skipping.", VectorIndexName);
        }
    }
}
