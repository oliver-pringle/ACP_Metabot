using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using ACP_Metabot.Api.Services.MarketplaceSource;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<OfferingRepository>();
builder.Services.AddSingleton<WatchRepository>();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<VoyageEmbeddingProvider>();
builder.Services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<VoyageEmbeddingProvider>());
builder.Services.AddSingleton<IClaudeClient, ClaudeApiClient>();
// Marketplace source is pluggable via Indexer:Source ("acp-api" or "json-file").
// Default = acp-api (live upstream); set to json-file in dev/offline mode.
var indexerSource = builder.Configuration["Indexer:Source"]?.ToLowerInvariant() ?? "acp-api";
switch (indexerSource)
{
    case "acp-api":
        builder.Services.AddSingleton<IMarketplaceSource, AcpApiMarketplaceSource>();
        break;
    case "json-file":
        builder.Services.AddSingleton<IMarketplaceSource, JsonFileMarketplaceSource>();
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown Indexer:Source value '{indexerSource}'. Expected 'acp-api' or 'json-file'.");
}

builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<StackComposerService>();
builder.Services.AddSingleton<WebhookDeliveryService>();
builder.Services.AddSingleton<WatchService>();

builder.Services.AddSingleton<MarketplaceIndexerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketplaceIndexerService>());
builder.Services.AddHostedService<WatchPollerBackgroundService>();

builder.Services.AddOpenApi();

var app = builder.Build();

// Bootstrap SQLite schema
var db = app.Services.GetRequiredService<Db>();
await db.InitializeSchemaAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// X-API-Key middleware: enforce on every endpoint except /health.
// Fail-closed: if the key isn't configured, refuse all auth'd requests.
// Set INTERNAL_API_KEY in the environment for both this container and the sidecar.
var apiKey = builder.Configuration["INTERNAL_API_KEY"];
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    if (string.IsNullOrEmpty(apiKey))
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsync("INTERNAL_API_KEY is not configured");
        return;
    }
    if (!ctx.Request.Headers.TryGetValue("X-API-Key", out var provided)
        || !string.Equals(provided.ToString(), apiKey, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("Unauthorized");
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTime.UtcNow.ToString("O")
}));

app.MapPost("/search", async (SearchRequest req, SearchService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });
    var limit = req.Limit is null ? 10 : Math.Clamp(req.Limit.Value, 1, 50);
    var minScore = req.MinScore ?? 0.0;
    var priceMax = req.PriceMaxUsdc ?? double.PositiveInfinity;
    var results = await svc.SearchAsync(req.Query, limit, minScore, priceMax, ct);

    object? bestMatch = null;
    if (results.Count > 0 && results[0].Score >= 0.7)
    {
        var top = results[0];
        bestMatch = new
        {
            agentAddress = top.AgentAddress,
            offeringName = top.OfferingName,
            score = top.Score
        };
    }

    return Results.Ok(new { query = req.Query, count = results.Count, results, bestMatch });
});

app.MapPost("/composeStack", async (ComposeRequest req, StackComposerService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.UseCase))
        return Results.BadRequest(new { error = "useCase is required" });
    var max = req.MaxOfferings is null ? 5 : Math.Clamp(req.MaxOfferings.Value, 1, 10);
    var stack = await svc.ComposeAsync(req.UseCase, req.BudgetUsdc, max, ct);
    return Results.Ok(stack);
});

app.MapGet("/index/stats", async (OfferingRepository repo, MarketplaceIndexerService idx,
    VoyageEmbeddingProvider emb) =>
{
    var total = await repo.CountAsync();
    var embedded = await repo.CountEmbeddedAsync(emb.ModelId);
    return Results.Ok(new
    {
        offeringsTotal = total,
        offeringsEmbedded = embedded,
        embeddingModel = emb.ModelId,
        embeddingDimension = emb.Dimension,
        lastFetchAt = idx.LastFetchAt?.ToString("O"),
        lastFetchCount = idx.LastFetchCount
    });
});

// Operator-only: trigger an indexer fetch immediately (useful in dev).
app.MapPost("/index/refresh", async (MarketplaceIndexerService idx, CancellationToken ct) =>
{
    await idx.RunOnceAsync(ct);
    return Results.Ok(new { ok = true });
});

app.MapPost("/watches", async (RegisterWatchRequest req, WatchService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });
    if (req.JobId <= 0)
        return Results.BadRequest(new { error = "jobId is required" });
    if (string.IsNullOrWhiteSpace(req.BuyerAddress))
        return Results.BadRequest(new { error = "buyerAddress is required" });

    var urlCheck = await WebhookUrlValidator.ValidateAsync(req.WebhookUrl, ct);
    if (!urlCheck.Ok)
        return Results.BadRequest(new { error = urlCheck.Reason });

    var result = await svc.RegisterWatchAsync(req, ct);
    return Results.Ok(new
    {
        watchId = result.WatchId,
        expiresAt = result.ExpiresAt.ToString("O"),
        intervalHours = result.IntervalHours,
        maxAlerts = result.MaxAlerts,
        initialMatches = result.InitialMatches
    });
});

// Operator-only: read a watch's state for debugging.
app.MapGet("/watches/{id}", async (string id, WatchRepository repo) =>
{
    var w = await repo.GetByIdAsync(id);
    return w is null ? Results.NotFound() : Results.Ok(w);
});

// Operator-only: clear watch_seen and force an immediate poll. Useful for
// verifying webhook delivery without waiting for a genuinely new offering.
app.MapPost("/watches/{id}/test-fire", async (string id, WatchService svc, CancellationToken ct) =>
{
    var fired = await svc.TestFireAsync(id, ct);
    if (fired is null) return Results.NotFound();
    return Results.Ok(new { watchId = id, fired = fired.Value });
});

app.Run();

public record SearchRequest(string Query, int? Limit, double? MinScore, double? PriceMaxUsdc);
public record ComposeRequest(string UseCase, double? BudgetUsdc, int? MaxOfferings);
