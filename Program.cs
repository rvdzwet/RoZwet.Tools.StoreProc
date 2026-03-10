using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mscc.GenerativeAI.Microsoft;
using Neo4j.Driver;
using OpenAI;
using System.ClientModel;
using System.Security.Cryptography;
using System.Text;
using RoZwet.Tools.StoreProc.Application.Agents;
using RoZwet.Tools.StoreProc.Application.Contracts;
using RoZwet.Tools.StoreProc.Application.McpServer;
using RoZwet.Tools.StoreProc.Application.Pipeline;
using RoZwet.Tools.StoreProc.Application.Services;
using RoZwet.Tools.StoreProc.Infrastructure.Ai;
using RoZwet.Tools.StoreProc.Infrastructure.Neo4j;
using RoZwet.Tools.StoreProc.Infrastructure.Parsing;

namespace RoZwet.Tools.StoreProc;

internal sealed record AuthRequest(string? Password);

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--web";

        if (mode is not ("--web" or "--ingest" or "--mcp"))
        {
            Console.Error.WriteLine($"Unknown mode: '{args[0]}'");
            PrintUsage();
            return 1;
        }

        return mode switch
        {
            "--mcp"    => await RunMcpAsync(),
            "--ingest" => await RunIngestAsync(),
            _          => await RunWebAsync()
        };
    }

    // -------------------------------------------------------------------------
    // AG-UI Web API — default mode (no args or --web).
    // Serves wwwroot/index.html and exposes POST /chat as an AG-UI SSE endpoint.
    // Protected by a password gate: POST /api/auth sets an HttpOnly session cookie.
    // -------------------------------------------------------------------------
    private static async Task<int> RunWebAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "ROZWET_");

        var config = builder.Configuration;

        RegisterNeo4j(builder.Services, config);
        RegisterAiProviders(builder.Services, config);
        RegisterApplicationServices(builder.Services, config);

        builder.Services.AddAGUI();
        builder.Services.AddHttpClient();

        var app = builder.Build();

        app.UseDefaultFiles();   // maps GET / → /index.html
        app.UseStaticFiles();

        var expectedToken = ComputeSessionToken(RequireConfig(config, "Web:AccessPassword"));

        // --- Zero-trust auth gate: protect /chat ---
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/chat", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Request.Cookies.TryGetValue("__rzw_session", out var cookieVal) ||
                    !string.Equals(cookieVal, expectedToken, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            await next(context);
        });

        // --- Auth endpoint ---
        app.MapPost("/api/auth", (HttpContext ctx, AuthRequest req) =>
        {
            var submitted = ComputeSessionToken(req.Password ?? string.Empty);
            if (!string.Equals(submitted, expectedToken, StringComparison.Ordinal))
                return Results.Unauthorized();

            ctx.Response.Cookies.Append("__rzw_session", expectedToken, new CookieOptions
            {
                HttpOnly  = true,
                SameSite  = SameSiteMode.Strict,
                IsEssential = true,
                Secure    = false   // localhost; set to true behind HTTPS reverse-proxy
            });

            return Results.Ok();
        });

        // --- AG-UI GraphRAG agent endpoint ---
        var chatClient    = app.Services.GetRequiredService<IChatClient>();
        var graphTools    = app.Services.GetRequiredService<GraphQueryTools>();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        var agent = new ChatClientAgent(
            chatClient,
            "graphrag-assistant",
            "GraphRAGAssistant",
            ChatService.SystemPrompt,
            graphTools.All.Cast<AITool>().ToList(),
            loggerFactory,
            app.Services);

        app.MapAGUI("/chat", agent);

        var url = config["Web:Url"] ?? "http://localhost:5000";
        await app.RunAsync(url);
        return 0;
    }

    // -------------------------------------------------------------------------
    // MCP HTTP server — listens on Mcp:Url (default http://localhost:3001).
    // -------------------------------------------------------------------------
    private static async Task<int> RunMcpAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "ROZWET_");

        var config = builder.Configuration;

        RegisterNeo4j(builder.Services, config);
        RegisterAiProviders(builder.Services, config);
        RegisterApplicationServices(builder.Services, config);

        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(StoreProcTools).Assembly);

        var app = builder.Build();
        app.MapMcp();

        var url = config["Mcp:Url"] ?? "http://localhost:3001";
        await app.RunAsync(url);
        return 0;
    }

    // -------------------------------------------------------------------------
    // Ingestion pipeline — processes SQL source files into Neo4j.
    // -------------------------------------------------------------------------
    private static async Task<int> RunIngestAsync()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                cfg.AddEnvironmentVariables(prefix: "ROZWET_");
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

        await host.StartAsync();

        try
        {
            var logger       = host.Services.GetRequiredService<ILogger<PipelineOrchestrator>>();
            var orchestrator = host.Services.GetRequiredService<PipelineOrchestrator>();
            var config       = host.Services.GetRequiredService<IConfiguration>();

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
        finally
        {
            await host.StopAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Shared service registrations.
    // -------------------------------------------------------------------------
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
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
        {
            var endpoint       = RequireConfig(config, "Ai:Embedding:Endpoint");
            var apiKey         = RequireConfig(config, "Ai:Embedding:ApiKey");
            var embeddingModel = RequireConfig(config, "Ai:Embedding:Model");

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

            return openAiClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator();
        });

        // Chat provider: native Gemini SDK — preserves thought_signature for thinking models.
        services.AddSingleton<IChatClient>(_ =>
        {
            var apiKey    = RequireConfig(config, "Ai:Chat:ApiKey");
            var chatModel = RequireConfig(config, "Ai:Chat:Model");
            return new GeminiChatClient(apiKey: apiKey, model: chatModel);
        });

        services.AddSingleton(_ =>
        {
            var opts = new AiResilienceOptions();
            config.GetSection("Ai:Resilience").Bind(opts);
            return opts;
        });

        services.AddSingleton<EmbeddingProvider>();
        services.AddSingleton<ChatProvider>();
    }

    private static void RegisterApplicationServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<AiSqlRepairAgent>();
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

    // -------------------------------------------------------------------------
    // Utilities.
    // -------------------------------------------------------------------------
    private static string ComputeSessionToken(string password) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

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
        Console.WriteLine("  RoZwet.Tools.StoreProc             Start the AG-UI web chat server (default)");
        Console.WriteLine("  RoZwet.Tools.StoreProc --web       Start the AG-UI web chat server");
        Console.WriteLine("  RoZwet.Tools.StoreProc --ingest    Run the ingestion pipeline");
        Console.WriteLine("  RoZwet.Tools.StoreProc --mcp       Start the MCP HTTP server");
    }
}
