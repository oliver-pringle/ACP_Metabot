using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using ACP_Metabot.Api.Services.MarketplaceSource;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<OfferingRepository>();

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

builder.Services.AddSingleton<MarketplaceIndexerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketplaceIndexerService>());

builder.Services.AddOpenApi();

var app = builder.Build();

// Bootstrap SQLite schema
var db = app.Services.GetRequiredService<Db>();
await db.InitializeSchemaAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// TODO: add X-API-Key middleware here if you expose this API publicly.
// Boilerplate ships with no auth — both containers share a private docker network
// and the API does not publish any ports.

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
    var results = await svc.SearchAsync(req.Query, limit, minScore, ct);
    return Results.Ok(new { query = req.Query, count = results.Count, results });
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

app.Run();

public record SearchRequest(string Query, int? Limit, double? MinScore);
public record ComposeRequest(string UseCase, double? BudgetUsdc, int? MaxOfferings);
