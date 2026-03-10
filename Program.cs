using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using OpenAI;
using System.ClientModel;
using RoZwet.Tools.StoreProc.Application.Agents;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Application.Pipeline;
using RoZwet.Tools.StoreProc.Application.Services;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;
using RoZwet.Tools.StoreProc.Infrastructure.Neo4j;

namespace RoZwet.Tools.StoreProc;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var mode = args[0].ToLowerInvariant();
        if (mode is not ("--ingest" or "--chat"))
        {
            Console.Error.WriteLine($"Unknown mode: '{args[0]}'");
            PrintUsage();
            return 1;
        }

        var host = BuildHost();
        await host.StartAsync();

        try
        {
            return mode switch
            {
                "--ingest" => await RunIngestAsync(host.Services),
                "--chat"   => await RunChatAsync(host.Services),
                _          => 1
            };
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                config.AddEnvironmentVariables(prefix: "ROZWET_");
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureServices((ctx, services) =>
            {
                var config = ctx.Configuration;

                RegisterNeo4j(services, config);
                RegisterAiProviders(services, config);
                RegisterApplicationServices(services, config);
            })
            .Build();

    private static void RegisterNeo4j(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IDriver>(_ =>
        {
            var uri      = RequireConfig(config, "Neo4j:Uri");
            var username = RequireConfig(config, "Neo4j:Username");
            var password = RequireConfig(config, "Neo4j:Password");
            return GraphDatabase.Driver(uri, AuthTokens.Basic(username, password));
        });

        services.AddSingleton<Neo4jIndexInitializer>();
        services.AddSingleton<INeo4jRepository, Neo4jRepository>();
    }

    private static void RegisterAiProviders(IServiceCollection services, IConfiguration config)
    {
        // Embedding provider: Voyage AI (voyage-4-large)
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
        {
            var endpoint       = RequireConfig(config, "Ai:Embedding:Endpoint");
            var apiKey         = RequireConfig(config, "Ai:Embedding:ApiKey");
            var embeddingModel = RequireConfig(config, "Ai:Embedding:Model");
            var dimensions     = int.TryParse(config["Ai:Embedding:Dimensions"], out var d) && d > 0 ? d : 1024;

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            return openAiClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator(defaultModelDimensions: dimensions);
        });

        // Chat provider: Gemini 3 Flash (OpenAI-compatible endpoint)
        services.AddSingleton<IChatClient>(_ =>
        {
            var endpoint  = RequireConfig(config, "Ai:Chat:Endpoint");
            var apiKey    = RequireConfig(config, "Ai:Chat:ApiKey");
            var chatModel = RequireConfig(config, "Ai:Chat:Model");

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            return openAiClient.GetChatClient(chatModel).AsIChatClient();
        });

        services.AddSingleton<EmbeddingProvider>();
        services.AddSingleton<ChatProvider>();
    }

    private static void RegisterApplicationServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<SqlAnalysisAgent>();
        services.AddSingleton<HybridSearchService>();
        services.AddSingleton<GraphQueryTools>();
        services.AddSingleton<ChatService>();

        services.AddSingleton(sp =>
        {
            var checkpointFile = config["Ingestion:CheckpointFile"] ?? "./checkpoint.json";
            var logger = sp.GetRequiredService<ILogger<IngestionCheckpoint>>();
            return new IngestionCheckpoint(checkpointFile, logger);
        });

        services.AddSingleton<PipelineOrchestrator>();
    }

    private static async Task<int> RunIngestAsync(IServiceProvider services)
    {
        var logger       = services.GetRequiredService<ILogger<PipelineOrchestrator>>();
        var orchestrator = services.GetRequiredService<PipelineOrchestrator>();
        var config       = services.GetRequiredService<IConfiguration>();

        var sqlDir    = config["Ingestion:SqlSourceDirectory"] ?? "./sql";
        var batchSize = int.TryParse(config["Ingestion:BatchSize"], out var b) ? b : 50;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogWarning("Cancellation requested. Finishing current batch before exiting...");
            cts.Cancel();
        };

        try
        {
            await orchestrator.RunAsync(sqlDir, batchSize, cts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Ingestion cancelled. Resume by re-running with --ingest.");
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Ingestion failed with an unhandled exception.");
            return 2;
        }
    }

    private static async Task<int> RunChatAsync(IServiceProvider services)
    {
        var chatService = services.GetRequiredService<ChatService>();
        var logger      = services.GetRequiredService<ILogger<ChatService>>();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== RoZwet GraphRAG Chat ===");
        Console.WriteLine("Type your question and press Enter. Type 'exit' to quit.");
        Console.ResetColor();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("\nYou: ");
            Console.ResetColor();

            var input = Console.ReadLine();

            if (input is null || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            try
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nAssistant:");
                Console.ResetColor();

                var answer = await chatService.AskAsync(input, cts.Token);
                Console.WriteLine(answer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing question.");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }

        Console.WriteLine("\nSession ended.");
        return 0;
    }

    private static string RequireConfig(IConfiguration config, string key)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required configuration key '{key}' is missing or empty.");
        return value;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  RoZwet.Tools.StoreProc --ingest   Run the ingestion pipeline");
        Console.WriteLine("  RoZwet.Tools.StoreProc --chat     Start the interactive chat session");
    }
}
