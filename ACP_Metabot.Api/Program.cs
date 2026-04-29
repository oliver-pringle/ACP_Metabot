using System.Threading.RateLimiting;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using ACP_Metabot.Api.Services.MarketplaceSource;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<OfferingRepository>();
builder.Services.AddSingleton<WatchRepository>();
builder.Services.AddSingleton<AgentReputationCacheRepository>();
builder.Services.AddSingleton<LifetimeSnapshotRepository>();
builder.Services.AddSingleton<RequestMetricsRepository>();
builder.Services.AddSingleton<MetricsChannel>();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<AcpOffChainClient>();
builder.Services.AddSingleton<ChainEventScanner>();
builder.Services.AddSingleton<ScoreCalculator>();
builder.Services.AddSingleton<VoyageEmbeddingProvider>();
builder.Services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<VoyageEmbeddingProvider>());
builder.Services.AddSingleton<VoyageRerankProvider>();
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

builder.Services.AddSingleton<ReputationService>();
builder.Services.AddSingleton<CategoryService>();
builder.Services.AddSingleton<DigestService>();
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<StackComposerService>();
builder.Services.AddSingleton<WebhookDeliveryService>();
builder.Services.AddSingleton<WatchService>();

builder.Services.AddSingleton<MarketplaceIndexerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketplaceIndexerService>());
builder.Services.AddHostedService<WatchPollerBackgroundService>();
builder.Services.AddSingleton<LifetimeSnapshotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LifetimeSnapshotService>());
builder.Services.AddSingleton<ReputationWarmerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReputationWarmerService>());
builder.Services.AddSingleton<MetricsWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsWriterService>());

builder.Services.AddOpenApi();

// Trust X-Forwarded-* from the local Caddy reverse proxy so per-IP rate
// limiting partitions on the real client IP, not the proxy container's IP.
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownIPNetworks.Clear();
    opts.KnownProxies.Clear();
});

// Per-IP rate limiting for public /v1/* endpoints. Two policies because the
// search call is cheap (one Voyage embedding) and the compose call is
// expensive (Claude Sonnet on top-K candidates).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("public-search", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    options.AddPolicy("public-compose", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    // Reputation lookup is cheap (no embeddings, single DB query), so a
    // higher per-IP cap is fine.
    options.AddPolicy("public-reputation", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    // Digest is two indexed reads — same cost class as reputation.
    options.AddPolicy("public-digest", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    // Agent browse — single ListByAgent query + reputation map. Cheap.
    options.AddPolicy("public-browse-agent", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

// Bootstrap SQLite schema
var db = app.Services.GetRequiredService<Db>();
await db.InitializeSchemaAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders();
app.UseRateLimiter();

// Operator metrics: record every request (including 429s and 401s) into
// the request_log table. Sits AFTER UseRateLimiter so 429 responses are
// captured, BEFORE the X-API-Key middleware so unauthorized internal-path
// attempts (401) are also captured — auth failures matter operationally.
app.UseMiddleware<RequestMetricsMiddleware>();

// X-API-Key middleware: enforce on every endpoint except /health and /v1/*.
// /v1/* are the public, rate-limited gateway endpoints used by the acp-find
// Claude Code plugin. Fail-closed: if the key isn't configured, refuse all
// auth'd requests. Set INTERNAL_API_KEY in the environment for both this
// container and the sidecar.
var apiKey = builder.Configuration["INTERNAL_API_KEY"];
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
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

// Shared handlers — mounted twice: once on the internal X-API-Key path used
// by the sidecar, and once on the public /v1/* path used by the acp-find
// plugin (rate-limited per IP).
async Task<IResult> HandleSearch(SearchRequest req, SearchService svc, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });
    var limit = req.Limit is null ? 10 : Math.Clamp(req.Limit.Value, 1, 50);
    var minScore = req.MinScore ?? 0.0;
    var priceMax = req.PriceMaxUsdc ?? double.PositiveInfinity;
    var staleAfterDays = req.StaleAfterDays;
    // Default rerank ON — pure cosine bumps relevance ~5-15% for ambiguous
    // queries and the cost is negligible. Callers can disable explicitly.
    var rerank = req.Rerank ?? true;
    var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim();
    var results = await svc.SearchAsync(req.Query, limit, minScore, priceMax, staleAfterDays, rerank, category, ct);

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
}

async Task<IResult> HandleCompose(ComposeRequest req, StackComposerService svc, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.UseCase))
        return Results.BadRequest(new { error = "useCase is required" });
    var max = req.MaxOfferings is null ? 5 : Math.Clamp(req.MaxOfferings.Value, 1, 10);
    var stack = await svc.ComposeAsync(req.UseCase, req.BudgetUsdc, max, ct);
    return Results.Ok(stack);
}

async Task<IResult> HandleReputation(AgentReputationRequest req,
    ReputationService reputation, OfferingRepository repo,
    ILogger<Program> log, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.AgentAddress))
        return Results.BadRequest(new { error = "invalid_address", message = "agentAddress is required" });
    var addr = req.AgentAddress.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });

    // Verify the agent is indexed before kicking off any compute.
    var offerings = await repo.ListByAgentAsync(addr);
    if (offerings.Count == 0)
        return Results.NotFound(new { error = "agent_not_indexed", message = "agent has no offerings on the marketplace" });

    try
    {
        var result = await reputation.GetOrComputeAsync(addr, ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "[reputation] compute failed for {addr}", addr);
        return Results.Json(
            new { error = "compute_failed", message = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

async Task<IResult> HandleDigest(int? days, DigestService svc)
{
    var window = days is null ? 1 : Math.Clamp(days.Value, 1, 30);
    var result = await svc.BuildAsync(window);
    return Results.Ok(result);
}

async Task<IResult> HandleBrowseAgent(string address,
    OfferingRepository repo, ReputationService reputation)
{
    if (string.IsNullOrWhiteSpace(address))
        return Results.BadRequest(new { error = "agentAddress is required" });

    if (!reputation.IsReady)
        return Results.Json(
            new { error = "reputation unavailable, indexer warming up" },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var addr = address.Trim().ToLowerInvariant();
    var offerings = await repo.ListByAgentAsync(addr);
    if (offerings.Count == 0)
        return Results.NotFound(new { error = "agent not found" });

    var rep = reputation.Build(offerings, offeringName: null);

    var browseOfferings = offerings
        .OrderByDescending(o => o.UsageCount)
        .Select(o =>
        {
            System.Text.Json.JsonElement? schema = null;
            if (!string.IsNullOrEmpty(o.RequirementSchemaJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(o.RequirementSchemaJson);
                    schema = doc.RootElement.Clone();
                }
                catch
                {
                    // Upstream schema may be malformed — surface it as null
                    // rather than failing the whole browse call.
                }
            }
            return new AgentBrowseOffering(
                OfferingId: o.Id,
                OfferingName: o.OfferingName,
                Description: o.Description,
                PriceUsdc: o.PriceUsdc,
                PriceType: o.PriceType,
                Chain: o.Chain,
                IsPrivate: o.IsPrivate,
                RequirementSchema: schema,
                FirstSeenAt: o.FirstSeenAt.ToString("O"),
                LastSeenAt: o.LastSeenAt.ToString("O"),
                Reputation: reputation.BuildSearchSummary(o));
        })
        .ToArray();

    var result = new AgentBrowseResult(
        AgentAddress: addr,
        AgentName: offerings[0].AgentName,
        Reputation: rep,
        Offerings: browseOfferings);

    return Results.Ok(result);
}

app.MapPost("/search", HandleSearch);
app.MapPost("/composeStack", HandleCompose);
app.MapPost("/agentReputation", HandleReputation);
app.MapGet("/digest", (int? days, DigestService svc) => HandleDigest(days, svc));
app.MapGet("/agent/{address}", (string address,
    OfferingRepository repo, ReputationService reputation)
    => HandleBrowseAgent(address, repo, reputation));
app.MapGet("/categories", (CategoryService svc) => Results.Ok(new { categories = svc.Categories }));

// Public gateway — same logic, no X-API-Key, IP rate-limited.
app.MapPost("/v1/search", HandleSearch).RequireRateLimiting("public-search");
app.MapPost("/v1/composeStack", HandleCompose).RequireRateLimiting("public-compose");
app.MapGet("/v1/agentReputation", async ([FromQuery] string agent,
    AgentReputationCacheRepository cacheRepo) =>
{
    if (string.IsNullOrWhiteSpace(agent))
        return Results.BadRequest(new { error = "invalid_address", message = "agent query param is required" });
    var addr = agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });

    var row = await cacheRepo.GetAsync(addr, DateTime.UtcNow);
    if (row is null)
        return Results.NotFound(new
        {
            error = "not_cached",
            hint = "hire the agentReputation offering for live computation"
        });

    var subScores = System.Text.Json.JsonSerializer.Deserialize<SubScoreSet>(row.SubScoresJson);
    var rawCounts = System.Text.Json.JsonSerializer.Deserialize<RawCounts>(row.RawCountsJson);
    var flags     = System.Text.Json.JsonSerializer.Deserialize<ReputationFlags>(row.FlagsJson);
    var result = new AgentReputationResultV2(
        AgentAddress: row.AgentAddress,
        AgentName:    row.AgentName,
        AgentScore:   row.AgentScore,
        ComputedAt:   row.ComputedAt.ToString("O"),
        WindowDays:   90,
        SubScores:    subScores!,
        RawCounts:    rawCounts!,
        Flags:        flags!);

    var hash = System.Security.Cryptography.SHA1.HashData(
        System.Text.Encoding.UTF8.GetBytes(row.ComputedAt.ToString("O")));
    var sb = new System.Text.StringBuilder(40);
    foreach (var b in hash) sb.Append(b.ToString("x2"));
    var etag = $"\"{sb.ToString()}\"";

    return new HeaderedJsonResult(result, new[] {
        ("Cache-Control", "public, max-age=3600"),
        ("ETag", etag),
    });
}).RequireRateLimiting("public-reputation");
app.MapGet("/v1/digest", (int? days, DigestService svc) => HandleDigest(days, svc)).RequireRateLimiting("public-digest");
app.MapGet("/v1/agent/{address}", (string address,
    OfferingRepository repo, ReputationService reputation)
    => HandleBrowseAgent(address, repo, reputation)).RequireRateLimiting("public-browse-agent");
// Static list — no per-IP limit; CDN-cacheable in front of Caddy if abuse appears.
app.MapGet("/v1/categories", (CategoryService svc) => Results.Ok(new { categories = svc.Categories }));

// Public diagnostic — used by the acp-find plugin's acp_health tool.
// Cheap (in-memory reads only); no rate-limit policy needed.
app.MapGet("/v1/health", (SearchService search, MarketplaceIndexerService idx, CategoryService cats) =>
    Results.Ok(new
    {
        status = "ok",
        time = DateTime.UtcNow.ToString("O"),
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
        corpus = new
        {
            count = search.CorpusCount,
            refreshedAt = search.CorpusRefreshedAtUtc == default
                ? null
                : search.CorpusRefreshedAtUtc.ToString("O"),
        },
        indexer = new
        {
            lastFetchAt = idx.LastFetchAt?.ToString("O"),
            lastFetchCount = idx.LastFetchCount,
        },
        categories = new
        {
            count = cats.Categories.Count,
            ready = cats.IsReady,
        },
    }));

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

// Operator-only telemetry. All five sit outside /v1/* so the X-API-Key
// middleware gates them automatically. Backed by the request_log table
// + hourly/daily rollups; see docs/runbook-scaling.md for the metric ->
// scaling-lever mapping.
app.MapGet("/metrics/summary",
    async (int? days, RequestMetricsRepository repo, MetricsChannel ch) =>
    {
        var window = days ?? 7;
        var summary = await repo.SummaryAsync(window);
        return Results.Ok(new { window, summary, metricsDropped = ch.DroppedCount });
    });

app.MapGet("/metrics/timeseries",
    async (int? days, string? granularity, RequestMetricsRepository repo) =>
    {
        try { return Results.Ok(await repo.TimeseriesAsync(days ?? 7, granularity ?? "hour")); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    });

app.MapGet("/metrics/endpoints",
    async (int? days, RequestMetricsRepository repo) =>
        Results.Ok(await repo.EndpointsAsync(days ?? 7)));

app.MapGet("/metrics/top",
    async (string dim, int? days, int? limit, RequestMetricsRepository repo) =>
    {
        if (dim != "query" && dim != "agent")
            return Results.BadRequest(new { error = "dim must be 'query' or 'agent'" });
        return Results.Ok(await repo.TopAsync(dim, days ?? 7, limit ?? 20));
    });

app.MapGet("/metrics/errors",
    async (int? days, int? limit, RequestMetricsRepository repo) =>
        Results.Ok(await repo.RecentErrorsAsync(days ?? 1, limit ?? 100)));

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

public record SearchRequest(string Query, int? Limit, double? MinScore, double? PriceMaxUsdc, int? StaleAfterDays, bool? Rerank, string? Category);
public record ComposeRequest(string UseCase, double? BudgetUsdc, int? MaxOfferings);
public record AgentReputationRequest(string AgentAddress);

class HeaderedJsonResult : IResult
{
    private readonly object _body;
    private readonly (string, string)[] _headers;
    public HeaderedJsonResult(object body, (string, string)[] headers)
    {
        _body = body;
        _headers = headers;
    }
    public async Task ExecuteAsync(HttpContext ctx)
    {
        foreach (var (k, v) in _headers) ctx.Response.Headers[k] = v;
        await Results.Ok(_body).ExecuteAsync(ctx);
    }
}
