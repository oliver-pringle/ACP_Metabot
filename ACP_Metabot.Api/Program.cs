using System.Threading.RateLimiting;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using ACP_Metabot.Api.Services.MarketplaceSource;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// Cap request body size at the server level. Per-route validators already
// reject overlong free-form inputs (MaxQueryLen=1000, MaxUseCaseLen=2000),
// but a global cap defends against payload-size DoS on any endpoint.
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 256L * 1024L;
});

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<AgentProfileRepository>();
builder.Services.AddSingleton<OfferingRepository>();
builder.Services.AddSingleton<WatchRepository>();
builder.Services.AddSingleton<AgentReputationCacheRepository>();
builder.Services.AddSingleton<AgentReputationHistoryRepository>();
builder.Services.AddSingleton<LifetimeSnapshotRepository>();
builder.Services.AddSingleton<RequestMetricsRepository>();
builder.Services.AddSingleton<V2KnownSellersRepository>();
builder.Services.AddSingleton<MetricsChannel>();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<AcpOffChainClient>();
builder.Services.AddSingleton<ChainEventScanner>();
builder.Services.AddSingleton<ScoreCalculator>();
builder.Services.AddSingleton<VoyageEmbeddingProvider>();
builder.Services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<VoyageEmbeddingProvider>());
builder.Services.AddSingleton<VoyageRerankProvider>();
builder.Services.AddSingleton<IRerankProvider, VoyageRerankAdapter>();
builder.Services.AddSingleton<IClaudeClient, ClaudeApiClient>();
// Marketplace sources are pluggable via Indexer:Source.
//   "acp-api"   — live upstream V1 + V2 (V2 toggleable via Indexer:V2:Enabled, default true)
//   "json-file" — single offline source from disk (dev / tests)
//
// v1.3 (2026-04-30) registered V2 alongside V1 — the C# indexer pulls
// IEnumerable<IMarketplaceSource> and unions their outputs, with each
// source tagging its rows via the MarketplaceVersion property.
var indexerSource = builder.Configuration["Indexer:Source"]?.ToLowerInvariant() ?? "acp-api";
switch (indexerSource)
{
    case "acp-api":
        builder.Services.AddSingleton<IMarketplaceSource, AcpApiMarketplaceSource>();
        var v2Enabled = builder.Configuration.GetValue<bool?>("Indexer:V2:Enabled") ?? true;
        if (v2Enabled)
        {
            builder.Services.AddSingleton<IMarketplaceSource, AcpV2MarketplaceSource>();
        }
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
builder.Services.AddSingleton<SaturationCalculator>(_ => new SaturationCalculator(
    threshold: builder.Configuration.GetValue<double?>("Saturation:Threshold") ?? 0.85));
builder.Services.AddSingleton<PricePercentileCalculator>(_ => new PricePercentileCalculator(
    lowNThreshold: builder.Configuration.GetValue<int?>("PricePercentile:LowNThreshold") ?? 5));
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<CrossPresenceBuilder>();
builder.Services.AddSingleton<AgentSearchService>();
builder.Services.AddSingleton<StackComposerService>();
builder.Services.AddSingleton<WebhookDeliveryService>();
builder.Services.AddSingleton<WatchService>();

builder.Services.AddSingleton<MarketplaceIndexerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketplaceIndexerService>());
builder.Services.AddHostedService<WatchPollerBackgroundService>();
// V2 seller chain-scan: enumerates every JobCreated provider on the V2 contract
// so AcpV2MarketplaceSource's Source A surfaces all V2 sellers, not just
// keyword-sweep matches + the hardcoded portfolio. Only registered when V2 is
// enabled — V1-only deployments don't need it.
{
    var v2Enabled = builder.Configuration.GetValue<bool?>("Indexer:V2:Enabled") ?? true;
    if (indexerSource == "acp-api" && v2Enabled)
    {
        builder.Services.AddSingleton<V2SellerScannerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<V2SellerScannerService>());
    }
}
builder.Services.AddSingleton<LifetimeSnapshotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LifetimeSnapshotService>());
builder.Services.AddSingleton<ReputationWarmerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReputationWarmerService>());
builder.Services.AddSingleton<MetricsWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsWriterService>());
builder.Services.AddHostedService<AgentProfileEmbedderService>();

builder.Services.AddOpenApi();

// Trust X-Forwarded-* ONLY from the configured reverse-proxy network. Without
// a restriction, any direct caller (sibling bots on acp-shared, anyone who
// reaches the docker bridge) could forge X-Forwarded-For and bypass the
// per-IP rate limits.
//
// Configure the trusted ingress range via the TRUSTED_PROXY_NETWORKS env var
// (comma-separated CIDRs). When unset, no X-Forwarded-* header is honoured —
// rate limits partition on the direct connection IP, which is correct for
// every deployment except the public Caddy gateway. The droplet sets
// TRUSTED_PROXY_NETWORKS to the acp-metabot bridge subnet (where Caddy lives).
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownIPNetworks.Clear();
    opts.KnownProxies.Clear();
    var trusted = builder.Configuration["TRUSTED_PROXY_NETWORKS"];
    if (!string.IsNullOrWhiteSpace(trusted))
    {
        foreach (var cidr in trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                opts.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
            catch
            {
                // Bad CIDR in env — skip silently rather than failing startup
                // for a misconfigured trust list.
            }
        }
    }
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

    // Agent-level search — BM25 + group-by, slightly heavier than offering
    // search but still no embeddings. Same cost class as digest.
    options.AddPolicy("public-search-agents", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    // Recent-hires is a thin wrapper around the gainers query. Cheap.
    options.AddPolicy("public-recent-hires", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    // Per-agent on-chain job ledger. RPC-heavy (JobCreated + JobFunded +
    // JobCompleted/Rejected/Expired chunked scans), so a tighter cap.
    options.AddPolicy("public-agent-recent-jobs", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    // Plugin boot beacon — fired once per MCP-server process start to give
    // the operator a clean activation funnel signal (install -> boot -> tool-call).
    // 30/IP/hr leaves plenty of headroom for dev cycles where someone keeps
    // restarting their MCP client; abuse here is harmless (zero-cost endpoint
    // returning 204) but rate-limiting keeps it from being a free amplifier
    // for someone trying to flood the request_log table.
    options.AddPolicy("public-plugin-boot", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
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
var apiKeyBytes = string.IsNullOrEmpty(apiKey)
    ? Array.Empty<byte>()
    : System.Text.Encoding.UTF8.GetBytes(apiKey);
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    if (apiKeyBytes.Length == 0)
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsync("INTERNAL_API_KEY is not configured");
        return;
    }
    // Constant-time compare to defang timing oracles. UTF-8 byte arrays must
    // match in length first; FixedTimeEquals throws on mismatch otherwise.
    var providedHeader = ctx.Request.Headers.TryGetValue("X-API-Key", out var raw)
        ? raw.ToString() : "";
    var providedBytes = System.Text.Encoding.UTF8.GetBytes(providedHeader);
    var ok = providedBytes.Length == apiKeyBytes.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, apiKeyBytes);
    if (!ok)
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
// Trust-boundary caps for buyer-supplied free-form inputs. Both bound the
// cost we'll spend in Voyage / Claude per request and shrink the prompt-
// injection surface (a 50KB query has more room to escape than a 1KB one).
const int MaxQueryLen = 1000;
const int MaxUseCaseLen = 2000;

async Task<IResult> HandleSearch(SearchRequest req, SearchService svc, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });
    if (req.Query.Length > MaxQueryLen)
        return Results.BadRequest(new { error = $"query must be {MaxQueryLen} characters or fewer" });
    var limit = req.Limit is null ? 10 : Math.Clamp(req.Limit.Value, 1, 50);
    var minScore = req.MinScore ?? 0.0;
    var priceMax = req.PriceMaxUsdc ?? double.PositiveInfinity;
    // freshness is the cleaner numeric alias for staleAfterDays; both are
    // accepted, freshness wins when both arrive.
    var staleAfterDays = req.Freshness ?? req.StaleAfterDays;
    if (req.Freshness is int fr && (fr < 1 || fr > 365))
        return Results.BadRequest(new { error = "freshness must be an integer between 1 and 365" });
    if (req.MinReputation is int mr && (mr < 0 || mr > 100))
        return Results.BadRequest(new { error = "minReputation must be an integer between 0 and 100" });
    HashSet<string>? chainFilter = null;
    if (req.Chains is { Length: > 0 } cs)
    {
        if (cs.Length > 8)
            return Results.BadRequest(new { error = "chain accepts at most 8 entries" });
        chainFilter = new HashSet<string>(cs.Select(c => (c ?? "").Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        chainFilter.Remove(""); // discard empty strings rather than 400-ing
        if (chainFilter.Count == 0) chainFilter = null;
    }
    // Default rerank ON — pure cosine bumps relevance ~5-15% for ambiguous
    // queries and the cost is negligible. Callers can disable explicitly.
    var rerank = req.Rerank ?? true;
    var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim();
    // Optional marketplace filter; null = both v1 and v2.
    var marketplace = NormalizeMarketplace(req.Marketplace);
    if (req.Marketplace is not null && marketplace is null)
        return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });
    var offset = req.Offset is null ? 0 : Math.Clamp(req.Offset.Value, 0, 1000);
    var results = await svc.SearchAsync(req.Query, limit, offset, minScore, priceMax, staleAfterDays,
        rerank, category, chainFilter, req.MinReputation, marketplace, ct);

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
    if (req.UseCase.Length > MaxUseCaseLen)
        return Results.BadRequest(new { error = $"useCase must be {MaxUseCaseLen} characters or fewer" });
    var max = req.MaxOfferings is null ? 5 : Math.Clamp(req.MaxOfferings.Value, 1, 10);
    var marketplace = NormalizeMarketplace(req.Marketplace);
    if (req.Marketplace is not null && marketplace is null)
        return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });

    HashSet<string>? chainFilter = null;
    if (req.Chains is { Length: > 0 } cs)
    {
        if (cs.Length > 8)
            return Results.BadRequest(new { error = "chain accepts at most 8 entries" });
        chainFilter = new HashSet<string>(cs.Select(c => (c ?? "").Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        chainFilter.Remove("");
        if (chainFilter.Count == 0) chainFilter = null;
    }

    var stack = await svc.ComposeAsync(req.UseCase, req.BudgetUsdc, max, marketplace, chainFilter, ct);
    return Results.Ok(stack);
}

// Local helper: map a raw marketplace string into the canonical "v1"/"v2"
// or null. Returns null for both "missing" and "invalid" — callers do their
// own 400-on-invalid by inspecting whether req.Marketplace was non-null.
static string? NormalizeMarketplace(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var trimmed = raw.Trim().ToLowerInvariant();
    return trimmed is "v1" or "v2" ? trimmed : null;
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
        // Keep full diagnostics in the server log; never echo internal details
        // (RPC URLs, DB messages, chain-scan internals) back to the client.
        log.LogError(ex, "[reputation] compute failed for {addr}", addr);
        return Results.Json(
            new { error = "compute_failed", message = "reputation compute failed; please retry" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

async Task<IResult> HandleDigest(int? days, string? marketplace,
    string[]? chain, double? priceMaxUsdc, DigestService svc)
{
    var window = days is null ? 1 : Math.Clamp(days.Value, 1, 30);
    var marketplaceFilter = NormalizeMarketplace(marketplace);
    if (marketplace is not null && marketplaceFilter is null)
        return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });

    HashSet<string>? chainFilter = null;
    if (chain is { Length: > 0 })
    {
        if (chain.Length > 8)
            return Results.BadRequest(new { error = "chain accepts at most 8 entries" });
        chainFilter = new HashSet<string>(chain.Select(c => (c ?? "").Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        chainFilter.Remove("");
        if (chainFilter.Count == 0) chainFilter = null;
    }

    if (priceMaxUsdc is double cap && (double.IsNaN(cap) || cap < 0))
        return Results.BadRequest(new { error = "priceMaxUsdc must be a non-negative number" });

    var result = await svc.BuildAsync(window, marketplaceFilter, chainFilter, priceMaxUsdc);
    return Results.Ok(result);
}

async Task<IResult> HandleBrowseAgent(string address,
    OfferingRepository repo, ReputationService reputation,
    CrossPresenceBuilder crossPresence, PricePercentileCalculator pricePercentile,
    SearchService search)
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
    var cp = await crossPresence.BuildAsync(addr);

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
            var category = search.GetCategoryForOffering(o.Id) ?? string.Empty;
            var mv = o.MarketplaceVersion ?? "v1";
            var pp = pricePercentile.Compute(o.Id, category, mv, o.PriceUsdc);
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
                Reputation: reputation.BuildSearchSummary(o),
                MarketplaceVersion: mv,
                PricePercentile: new PricePercentileDto(pp.Value, pp.PeerN, pp.LowN));
        })
        .ToArray();

    var result = new AgentBrowseResult(
        AgentAddress: addr,
        AgentName: offerings[0].AgentName,
        Reputation: rep,
        Offerings: browseOfferings,
        CrossPresence: cp);

    return Results.Ok(result);
}

app.MapPost("/search", HandleSearch);
app.MapPost("/composeStack", HandleCompose);
app.MapPost("/agentReputation", HandleReputation);
app.MapGet("/digest", (int? days, string? marketplace, string[]? chain, double? priceMaxUsdc, DigestService svc)
    => HandleDigest(days, marketplace, chain, priceMaxUsdc, svc));
app.MapGet("/agent/{address}", (string address,
    OfferingRepository repo, ReputationService reputation,
    CrossPresenceBuilder crossPresence, PricePercentileCalculator pricePercentile,
    SearchService search)
    => HandleBrowseAgent(address, repo, reputation, crossPresence, pricePercentile, search));
app.MapGet("/categories", (CategoryService svc) => Results.Ok(new { categories = svc.Categories }));

// Public gateway — same logic, no X-API-Key, IP rate-limited.
app.MapPost("/v1/search", HandleSearch).RequireRateLimiting("public-search");
app.MapPost("/v1/composeStack", HandleCompose).RequireRateLimiting("public-compose");
app.MapGet("/v1/agentReputation", async ([FromQuery] string agent,
    ReputationService reputation) =>
{
    if (string.IsNullOrWhiteSpace(agent))
        return Results.BadRequest(new { error = "invalid_address", message = "agent query param is required" });
    var addr = agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });

    // Cache-only — never triggers compute. GetCachedAsync also attaches the
    // 30d trajectory and re-computes sub-score percentiles against the current
    // corpus.
    var result = await reputation.GetCachedAsync(addr);
    if (result is null)
        return Results.NotFound(new
        {
            error = "not_cached",
            hint = "hire the agentReputation offering for live computation"
        });

    var hash = System.Security.Cryptography.SHA1.HashData(
        System.Text.Encoding.UTF8.GetBytes(result.ComputedAt));
    var sb = new System.Text.StringBuilder(40);
    foreach (var b in hash) sb.Append(b.ToString("x2"));
    var etag = $"\"{sb.ToString()}\"";

    return new HeaderedJsonResult(result, new[] {
        ("Cache-Control", "public, max-age=3600"),
        ("ETag", etag),
    });
}).RequireRateLimiting("public-reputation");

// Public + internal — day-by-day reputation trajectory. Cache-only-ish in the
// sense that it reads from agent_reputation_history without triggering a chain
// scan; rows are written by every paid hire and warmer pass via ReputationService.
async Task<IResult> HandleReputationHistory(string agent, int? days,
    AgentReputationHistoryRepository histRepo)
{
    if (string.IsNullOrWhiteSpace(agent))
        return Results.BadRequest(new { error = "invalid_address", message = "agent query param is required" });
    var addr = agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });
    var window = days is null ? 30 : Math.Clamp(days.Value, 1, 90);
    var points = await histRepo.GetTrajectoryAsync(addr, window);
    return Results.Ok(new { agentAddress = addr, days = window, history = points });
}

app.MapGet("/agentReputationHistory",
    ([FromQuery] string agent, [FromQuery] int? days,
        AgentReputationHistoryRepository histRepo)
    => HandleReputationHistory(agent, days, histRepo));

app.MapGet("/v1/agentReputationHistory",
    ([FromQuery] string agent, [FromQuery] int? days,
        AgentReputationHistoryRepository histRepo)
    => HandleReputationHistory(agent, days, histRepo))
    .RequireRateLimiting("public-reputation");
app.MapGet("/v1/digest", (int? days, string? marketplace, string[]? chain, double? priceMaxUsdc, DigestService svc)
    => HandleDigest(days, marketplace, chain, priceMaxUsdc, svc))
    .RequireRateLimiting("public-digest");
app.MapGet("/v1/agent/{address}", (string address,
    OfferingRepository repo, ReputationService reputation,
    CrossPresenceBuilder crossPresence, PricePercentileCalculator pricePercentile,
    SearchService search)
    => HandleBrowseAgent(address, repo, reputation, crossPresence, pricePercentile, search))
    .RequireRateLimiting("public-browse-agent");
// Static list — no per-IP limit; CDN-cacheable in front of Caddy if abuse appears.
// Now includes offeringCount per category (computed from the live corpus
// so it reflects active, non-tombstoned offerings).
app.MapGet("/v1/categories", (CategoryService cats, SearchService search) =>
{
    var counts = search.CategoryCounts();
    var items = cats.Categories.Select(c => new
    {
        name = c.Name,
        description = c.Description,
        offeringCount = counts.TryGetValue(c.Name, out var n) ? n : 0
    }).ToArray();
    return Results.Ok(new { categories = items });
});

// Public diagnostic — used by the acp-find plugin's acp_health tool.
// Cheap (in-memory reads only); no rate-limit policy needed.
app.MapGet("/v1/health", (SearchService search, MarketplaceIndexerService idx, CategoryService cats) =>
{
    var byMarketplace = search.CorpusByMarketplace();
    return Results.Ok(new
    {
        status = "ok",
        time = DateTime.UtcNow.ToString("O"),
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
        corpus = new
        {
            count = search.CorpusCount,
            v1Count = byMarketplace.TryGetValue("v1", out var v1) ? v1 : 0,
            v2Count = byMarketplace.TryGetValue("v2", out var v2) ? v2 : 0,
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
    });
});

// Plugin activation beacon. Fired once per MCP-server boot by acp-find-mcp
// (>= 0.6.0) right after handling the MCP `initialize` request — proves the
// server actually started under a real client, separate from npx-cache or
// scanner downloads. Body is ignored; the metrics middleware records the
// User-Agent (`acp-find-plugin/X.Y.Z`) and remote_ip via request_log, which
// `/metrics/clients/endpoints?family=acp-find-plugin` then surfaces.
//
// Returns 204 No Content with empty body. Side-effect free.
//
// Privacy: no body content is read or persisted. The signal is purely
// (User-Agent, IP, timestamp) — same shape every other request already
// captures. Plugin users can opt out with `ACP_DISABLE_BOOT_BEACON=1`.
app.MapPost("/v1/plugin/boot", () => Results.NoContent())
    .RequireRateLimiting("public-plugin-boot");

// Public read-only watch status. Returns the watch's public state without the
// sensitive fields (buyer_address, webhook_url) — those identify the buyer
// and would let abusers spam the webhook destination if leaked. Same rate
// limit class as agent browse since both are single-row reads.
app.MapGet("/v1/watches/{id}", async (string id, WatchRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(new { error = "watchId is required" });
    var w = await repo.GetByIdAsync(id);
    if (w is null) return Results.NotFound(new { error = "watch_not_found" });
    return Results.Ok(new
    {
        watchId = w.Id,
        status = w.Status,
        query = w.Query,
        createdAt = w.CreatedAt.ToString("O"),
        expiresAt = w.ExpiresAt.ToString("O"),
        intervalHours = w.IntervalHours,
        maxAlerts = w.MaxAlerts,
        alertsDelivered = w.AlertsDelivered,
        lastPolledAt = w.LastPolledAt?.ToString("O"),
        marketplace = w.Marketplace,
        minScore = w.MinScore,
        priceMaxUsdc = w.PriceMaxUsdc
    });
}).RequireRateLimiting("public-browse-agent");

// Public recent-hires: top offerings by absolute hire-count delta in window.
// Different surface from /v1/digest (which mixes new + gainers); this is
// purely "what's getting hired right now". Reuses DigestService's gainers
// computation with the same chain/marketplace/price filters.
async Task<IResult> HandleRecentHires(int? days, int? limit, string? marketplace,
    string[]? chain, double? priceMaxUsdc, string? category, DigestService svc)
{
    var window = days is null ? 7 : Math.Clamp(days.Value, 1, 30);
    var cap = limit is null ? 10 : Math.Clamp(limit.Value, 1, 50);
    var marketplaceFilter = NormalizeMarketplace(marketplace);
    if (marketplace is not null && marketplaceFilter is null)
        return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });

    HashSet<string>? chainFilter = null;
    if (chain is { Length: > 0 })
    {
        if (chain.Length > 8)
            return Results.BadRequest(new { error = "chain accepts at most 8 entries" });
        chainFilter = new HashSet<string>(chain.Select(c => (c ?? "").Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        chainFilter.Remove("");
        if (chainFilter.Count == 0) chainFilter = null;
    }

    if (priceMaxUsdc is double p && (double.IsNaN(p) || p < 0))
        return Results.BadRequest(new { error = "priceMaxUsdc must be a non-negative number" });

    var digest = await svc.BuildAsync(window, marketplaceFilter, chainFilter, priceMaxUsdc);
    var gainers = digest.Gainers.Take(cap).ToArray();
    return Results.Ok(new
    {
        windowDays = window,
        snapshotComparison = digest.SnapshotComparison,
        count = gainers.Length,
        results = gainers,
        // category isn't enforced here yet — gainer tuples don't carry the
        // pre-tagged category. Pass-through hint so the plugin sees what
        // was requested even if the gateway can't filter on it.
        categoryRequested = category
    });
}
app.MapGet("/v1/recentHires",
    (int? days, int? limit, string? marketplace, string[]? chain,
        double? priceMaxUsdc, string? category, DigestService svc) =>
        HandleRecentHires(days, limit, marketplace, chain, priceMaxUsdc, category, svc))
    .RequireRateLimiting("public-recent-hires");

// Public agent-level search (v1.7). Dispatches through AgentSearchService which
// runs a BM25 + dense cosine + RRF fusion + optional rerank pipeline and enriches
// each hit with TopOfferings records, cross-presence, and cached agentScore.
// Replaces the v1.6 direct OfferingRepository.SearchAgentsAsync call.
async Task<IResult> HandleSearchAgents(SearchAgentsRequest req,
    AgentSearchService svc, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });
    if (req.Query.Length > MaxQueryLen)
        return Results.BadRequest(new { error = $"query must be {MaxQueryLen} characters or fewer" });
    var limit = req.Limit is null ? 5 : Math.Clamp(req.Limit.Value, 1, 50);
    var marketplaceFilter = NormalizeMarketplace(req.Marketplace);
    if (req.Marketplace is not null && marketplaceFilter is null)
        return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });

    var hits = await svc.SearchAsync(req.Query, limit, marketplaceFilter, ct);
    return Results.Ok(new { query = req.Query, count = hits.Count, agents = hits });
}
app.MapPost("/searchAgents",
    (SearchAgentsRequest req, AgentSearchService svc,
        CancellationToken ct) => HandleSearchAgents(req, svc, ct));
app.MapPost("/v1/searchAgents",
    (SearchAgentsRequest req, AgentSearchService svc,
        CancellationToken ct) => HandleSearchAgents(req, svc, ct))
    .RequireRateLimiting("public-search-agents");

// Per-agent on-chain job ledger. RPC-heavy — every call hits the chain via
// chunked filters across the requested window. Tight rate limit; the plugin
// caches the response for 5 minutes which absorbs most refresh storms.
async Task<IResult> HandleAgentRecentJobs(string agent, int? days, int? limit,
    ChainEventScanner scanner, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(agent))
        return Results.BadRequest(new { error = "invalid_address", message = "agent query param is required" });
    var addr = agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });
    var window = days is null ? 30 : Math.Clamp(days.Value, 1, 90);
    var cap = limit is null ? 25 : Math.Clamp(limit.Value, 1, 100);

    try
    {
        var jobs = await scanner.ListAgentRecentJobsAsync(addr, window, cap, ct);
        return Results.Ok(new
        {
            agentAddress = addr,
            days = window,
            count = jobs.Count,
            jobs
        });
    }
    catch (Exception)
    {
        // Don't echo RPC internals back to the client.
        return Results.Json(new { error = "compute_failed", message = "chain scan failed; please retry" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
app.MapGet("/agentRecentJobs",
    ([FromQuery] string agent, [FromQuery] int? days, [FromQuery] int? limit,
        ChainEventScanner scanner, CancellationToken ct) =>
        HandleAgentRecentJobs(agent, days, limit, scanner, ct));
app.MapGet("/v1/agentRecentJobs",
    ([FromQuery] string agent, [FromQuery] int? days, [FromQuery] int? limit,
        ChainEventScanner scanner, CancellationToken ct) =>
        HandleAgentRecentJobs(agent, days, limit, scanner, ct))
    .RequireRateLimiting("public-agent-recent-jobs");

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

app.MapGet("/metrics/clients",
    async (int? days, int? limit, string? family, string? excludeFamilies,
           RequestMetricsRepository repo) =>
    {
        var exclude = string.IsNullOrWhiteSpace(excludeFamilies)
            ? null
            : excludeFamilies.Split(',', StringSplitOptions.RemoveEmptyEntries
                                       | StringSplitOptions.TrimEntries);
        return Results.Ok(await repo.ClientsAsync(days ?? 7, limit ?? 50, family, exclude));
    });

app.MapGet("/metrics/clients/summary",
    async (int? days, RequestMetricsRepository repo) =>
        Results.Ok(await repo.ClientsSummaryAsync(days ?? 7)));

app.MapGet("/metrics/clients/endpoints",
    async (int? days, int? limit, string? family, RequestMetricsRepository repo) =>
        Results.Ok(await repo.ClientEndpointsAsync(days ?? 7, limit ?? 50, family)));

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

public record SearchRequest(
    string Query,
    int? Limit,
    double? MinScore,
    double? PriceMaxUsdc,
    int? StaleAfterDays,
    bool? Rerank,
    string? Category,
    [property: System.Text.Json.Serialization.JsonPropertyName("chain")] string[]? Chains,
    int? MinReputation,
    int? Freshness,
    [property: System.Text.Json.Serialization.JsonPropertyName("marketplace")] string? Marketplace,
    int? Offset = null);
public record ComposeRequest(
    string UseCase,
    double? BudgetUsdc,
    int? MaxOfferings,
    [property: System.Text.Json.Serialization.JsonPropertyName("marketplace")] string? Marketplace,
    [property: System.Text.Json.Serialization.JsonPropertyName("chain")] string[]? Chains = null);
public record SearchAgentsRequest(
    string Query,
    int? Limit,
    [property: System.Text.Json.Serialization.JsonPropertyName("marketplace")] string? Marketplace);
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
