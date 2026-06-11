using Docker.DotNet;
using Microsoft.Extensions.Logging;
using QavrenSwarm.Services;

// One process hosts BOTH the stdio MCP server (talking to the IDE harness) and a Kestrel
// endpoint (the host-side Claude Code broker). WebApplicationBuilder is an
// IHostApplicationBuilder, so the MCP server registers exactly as it would on a plain Host.
var builder = WebApplication.CreateBuilder(args);

var config = new SwarmConfig();

// --- Logging: stdout is reserved for JSON-RPC; everything else goes to stderr + a file. ---
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // ALL console logs → stderr
var logPath = Environment.GetEnvironmentVariable("QAVREN_LOG_FILE")
    ?? Path.Combine(AppContext.BaseDirectory, "logs", "qavren.log");
builder.Logging.AddProvider(new FileLoggerProvider(logPath));
builder.Logging.SetMinimumLevel(LogLevel.Information);

// --- Kestrel bound to 0.0.0.0 so containers reach the broker via host.docker.internal. ---
builder.WebHost.UseUrls($"http://0.0.0.0:{config.BrokerPort}");

// --- Services ---
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<IDockerClient>(_ =>
    new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient());
builder.Services.AddSingleton<JobStateStore>();
builder.Services.AddSingleton<DockerLifecycleManager>();
builder.Services.AddSingleton<ClaudeCodeBroker>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("QavrenSwarm");
startupLog.LogInformation(
    "Qavren Swarm starting. defaultProvider={Provider} brokerPort={Port} brokerEnabled={Enabled} log={Log}",
    config.DefaultProvider, config.BrokerPort, config.BrokerEnabled, logPath);

// --- Claude Code broker endpoint (OpenAI-compatible). ---
if (config.BrokerEnabled)
{
    app.MapPost("/v1/chat/completions", async (HttpContext ctx, ClaudeCodeBroker broker, CancellationToken ct) =>
    {
        if (!broker.IsAuthorized(ctx.Request.Headers["Authorization"].ToString()))
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

        ChatCompletionRequest? req;
        try
        {
            req = await ctx.Request.ReadFromJsonAsync<ChatCompletionRequest>(ct);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = $"bad request: {ex.Message}" }, statusCode: StatusCodes.Status400BadRequest);
        }
        if (req is null || req.Messages.Count == 0)
            return Results.Json(new { error = "missing messages" }, statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var resp = await broker.CompleteAsync(req, ct);
            return Results.Json(resp);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    });

    app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
}

await app.RunAsync();
