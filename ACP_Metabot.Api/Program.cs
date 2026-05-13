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
builder.Services.AddSingleton<AgentResourcesRepository>();
builder.Services.AddSingleton<ReputationFeedRepository>();
builder.Services.AddSingleton<MetricsChannel>();

builder.Services.AddHttpClient();
// M1 — Resilient HttpClients for both embedding providers. Standard handler
// gives us retries with exponential backoff + jitter, circuit breaker, and
// per-attempt + total-request timeouts. AttemptTimeout bumped to 60s
// because embedding requests for large batches legitimately take 20-40s;
// the default 10s would shred well-behaved upstream into spurious retries.
// SamplingDuration must be >= 2x AttemptTimeout (library validation rule).
builder.Services.AddHttpClient(nameof(VoyageEmbeddingProvider))
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
        o.Retry.MaxRetryAttempts = 3;
    });
builder.Services.AddHttpClient(nameof(CohereEmbeddingProvider))
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
        o.Retry.MaxRetryAttempts = 3;
    });
// Cross-bot HTTP client to ACP_ChainlinkBot. BaseAddress comes from
// TheChainlinkBot:BaseUrl (default: the acp-shared bridge service name);
// the THECHAINLINKBOT_API_KEY env var is read by the typed client itself.
// Empty key + unreachable URL are tolerated at startup — calls fail at
// runtime when the cross-bot relationship hasn't been provisioned yet.
builder.Services.AddHttpClient("thechainlinkbot", c =>
{
    var baseUrl = builder.Configuration["TheChainlinkBot:BaseUrl"]
        ?? "http://acp-chainlinkbot-api:5000/";
    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout = TimeSpan.FromSeconds(15);
}).AddStandardResilienceHandler();
// AddStandardResilienceHandler: 3 retries with jittered exponential backoff
// on 5xx + transient network errors, plus an outer circuit breaker. Defaults
// (10s attempt / 30s total) are sufficient for /v1/internal/functions which
// returns immediately with the requestId; fulfilment is polled separately.
builder.Services.AddSingleton<TheChainlinkBotClient>();
builder.Services.AddSingleton<ITheChainlinkBotClient>(sp => sp.GetRequiredService<TheChainlinkBotClient>());
builder.Services.AddSingleton<AcpOffChainClient>();
builder.Services.AddSingleton<ChainEventScanner>();
builder.Services.AddSingleton<ScoreCalculator>();
builder.Services.AddSingleton<VoyageEmbeddingProvider>();
// Optional Cohere fallback (1024 dim, matches voyage-finance-2). Wired in
// only when Embeddings:Fallback:Enabled=true so deployments without a
// COHERE_API_KEY don't crash on boot. See CohereEmbeddingProvider.cs.
var embeddingFallbackEnabled =
    builder.Configuration.GetValue<bool?>("Embeddings:Fallback:Enabled") ?? false;
if (embeddingFallbackEnabled)
{
    builder.Services.AddSingleton<CohereEmbeddingProvider>();
}
builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
{
    var providers = new List<IEmbeddingProvider>
    {
        sp.GetRequiredService<VoyageEmbeddingProvider>()
    };
    if (sp.GetService<CohereEmbeddingProvider>() is { } cohere) providers.Add(cohere);
    if (providers.Count == 1) return providers[0];
    return new ChainedEmbeddingProvider(
        providers, sp.GetRequiredService<ILogger<ChainedEmbeddingProvider>>());
});
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
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddSingleton<ReputationFeedPublisherWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReputationFeedPublisherWorker>());
builder.Services.AddSingleton<ReputationFeedSyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReputationFeedSyncWorker>());

// v1.7 Bundle A — Arena marketplace integration. Cross-bot calls into
// ArenaBot's free Resources surface over the acp-shared docker network.
builder.Services.AddSingleton<AgentArenaParticipationRepository>();
builder.Services.AddHttpClient<TheArenaBotClient>();
builder.Services.AddSingleton<ArenaSourceService>();
builder.Services.AddSingleton<ArenaSourceWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ArenaSourceWorker>());

// v1.7 Bundle A + B + C paid offerings — extracted into a single service for
// clarity. See Services/V17PaidOfferingsService.cs.
builder.Services.AddSingleton<V17PaidOfferingsService>();

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

    // ACP v2 Resources — public free metadata endpoints buyer / orchestrator
    // agents call BEFORE hiring an offering. All three current handlers are
    // cheap (in-memory reads or static), but they're explicitly designed to
    // be called frequently (one call per buyer per hire-decision), so the
    // limit is generous — 120/IP/hr.
    options.AddPolicy("public-resources", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    // R7-IDEA-C: cross-agent Resource discovery. Per-agent reads are cheap
    // single-table queries; cross-agent search is a LIKE on at most a few
    // hundred rows in v1. Both well below the budget for a 120/IP/hr cap.
    options.AddPolicy("public-marketplace-resources", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
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
//
// Exception within /v1/*: /v1/agents/active and /v1/internal/* are internal
// cross-bot endpoints (consumed over the acp-shared docker network — e.g.
// ACP_ChainlinkBot calls /v1/agents/active to enumerate agents and
// /v1/internal/agentReputation to read cached scores without hitting the
// per-IP public rate limiter). They MUST require X-API-Key — unlike the
// other /v1/* paths they are never reached from the public Caddy gateway.
var apiKey = builder.Configuration["INTERNAL_API_KEY"];
var apiKeyBytes = string.IsNullOrEmpty(apiKey)
    ? Array.Empty<byte>()
    : System.Text.Encoding.UTF8.GetBytes(apiKey);
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    var isInternalV1 = path.StartsWithSegments("/v1/agents/active",
                           StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/v1/internal",
                           StringComparison.OrdinalIgnoreCase);
    if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        (path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase) && !isInternalV1))
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
        // NOTE: v1.7 Bundle A intentionally does NOT wrap this result — the
        // /v1/agentReputation response shape is stable for downstream clients
        // (acp-find-plugin, marketplace agents). To get Arena participation
        // for the same agent, callers hit the sibling GET /v1/agent/{addr}/arena
        // endpoint, which Metabot also serves and ArenaSourceWorker keeps
        // fresh on a 15-min cadence.
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
    var window = days is null ? 1 : Math.Clamp(days.Value, 1, 90);
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

// ===== v1.7 paid offerings (5 of 6 shipped; arenaDigestPro deferred) =====
//
// All five live under /v1/* and share the public-compose rate-limit budget.
// The internal (sidecar-only) names are kept as legacy POST aliases without
// V1 prefix to make the apiClient calls symmetrical with the older offerings.

app.MapPost("/v1/arena/participants-bulk",
    async (ArenaParticipantsBulkRequest req, V17PaidOfferingsService svc) =>
{
    if (req.Addresses is null || req.Addresses.Length == 0)
        return Results.BadRequest(new { error = "addresses array required" });
    if (req.Addresses.Length > 25)
        return Results.BadRequest(new { error = "max 25 addresses per call" });
    return Results.Ok(await svc.ArenaParticipantsAsync(req.Addresses));
}).RequireRateLimiting("public-compose");

app.MapPost("/v1/buyer/orchestrate",
    async (BuyerOrchestrationRequest req, V17PaidOfferingsService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.UseCase))
        return Results.BadRequest(new { error = "useCase required" });
    if (req.UseCase.Length > 2000)
        return Results.BadRequest(new { error = "useCase must be ≤ 2000 chars" });
    return Results.Ok(await svc.BuyerStackOrchestrationAsync(req.UseCase, req.BudgetUsdc, req.MaxOfferings, ct));
}).RequireRateLimiting("public-compose");

app.MapPost("/v1/buyer/budget-check",
    async (BudgetCheckRequest req, V17PaidOfferingsService svc) =>
{
    if (req.OfferingIds is null || req.OfferingIds.Length == 0)
        return Results.BadRequest(new { error = "offeringIds array required" });
    if (req.OfferingIds.Length > 25)
        return Results.BadRequest(new { error = "max 25 offerings per call" });
    return Results.Ok(await svc.PreHireBudgetCheckAsync(req.OfferingIds));
}).RequireRateLimiting("public-compose");

app.MapPost("/v1/seller/coaching",
    async (SellerCoachingRequest req, V17PaidOfferingsService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Agent))
        return Results.BadRequest(new { error = "agent required" });
    var addr = req.Agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address" });
    return Results.Ok(await svc.SellerCoachingPackAsync(addr, ct));
}).RequireRateLimiting("public-compose");

app.MapPost("/v1/seller/migration",
    async (V1Tov2MigrationRequest req, V17PaidOfferingsService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.Agent))
        return Results.BadRequest(new { error = "agent required" });
    var addr = req.Agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address" });
    return Results.Ok(await svc.V1Tov2MigrationAsync(addr));
}).RequireRateLimiting("public-compose");
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

// Internal cross-bot variant. Same cache-only semantics as the public
// /v1/agentReputation but exempt from the 60/hr per-IP limiter so callers
// like ChainlinkBot's ScoringPushWorker can sweep the active-agents set.
// Special-cased in the X-API-Key middleware above (path prefix /v1/internal).
app.MapGet("/v1/internal/agentReputation", async ([FromQuery] string agent,
    ReputationService reputation) =>
{
    if (string.IsNullOrWhiteSpace(agent))
        return Results.BadRequest(new { error = "invalid_address", message = "agent query param is required" });
    var addr = agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });
    var result = await reputation.GetCachedAsync(addr);
    return result is null
        ? Results.NotFound(new { error = "not_cached" })
        : Results.Ok(result);
});

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

// ACP v2 Resources — public, free, parameterised endpoints that buyer /
// orchestrator agents (Butler etc.) call BEFORE paying for an offering.
// Mirrored 1:1 with acp-v2/src/resources.ts; run `npm run print-resources`
// in acp-v2/ and paste each block into app.virtuals.io's Resources tab.
//
// /v1/* already bypasses the X-API-Key middleware, so no extra wiring is
// needed for these to stay public. Rate-limited via the public-resources
// policy (120/IP/hr — Resources are explicitly designed to be called
// frequently as pre-hire introspection).
//
// Add new Resources here in lockstep with new entries in resources.ts.
app.MapGet("/v1/resources/searchStatus", (SearchService search, MarketplaceIndexerService idx) =>
{
    var byMarketplace = search.CorpusByMarketplace();
    var refreshedAt = search.CorpusRefreshedAtUtc == default
        ? (string?)null
        : search.CorpusRefreshedAtUtc.ToString("O");
    return Results.Ok(new
    {
        corpus = new
        {
            total = search.CorpusCount,
            v1Count = byMarketplace.TryGetValue("v1", out var v1) ? v1 : 0,
            v2Count = byMarketplace.TryGetValue("v2", out var v2) ? v2 : 0,
            refreshedAt
        },
        indexer = new
        {
            lastFetchAt = idx.LastFetchAt?.ToString("O"),
            lastFetchCount = idx.LastFetchCount
        },
        time = DateTime.UtcNow.ToString("O")
    });
}).RequireRateLimiting("public-resources");

// Capabilities — TheMetaBot's offerings list. Kept in lockstep with
// acp-v2/src/offerings/registry.ts and acp-v2/src/pricing.ts. Buyers
// hitting this Resource see name + 1-line description + USDC price +
// SLA, enough to decide whether to hire without paying for `browseAgent`.
// When you add/remove/reprice an offering, update BOTH this list AND the
// TS registry — docs-lockstep rule from CLAUDE.md applies.
app.MapGet("/v1/resources/capabilities", () =>
{
    return Results.Ok(new
    {
        agent = "TheMetaBot",
        offerings = new object[]
        {
            // v1.7.2: search / searchAgents / browseAgent moved to free Resources
            // (callable at /v1/resources/search etc) — they were never economic
            // to hire as paid offerings at the $0.01 price floor.
            new { name = "today",            priceUsdc = 0.02, slaMinutes = 5, description = "Daily digest of new offerings, new Resources, biggest hire-count gainers, new agents, churn rate, cohort survival, and category saturation across the marketplace; configurable lookback window." },
            new { name = "composeStack",     priceUsdc = 0.50, slaMinutes = 5, description = "LLM-curated multi-agent stack for a stated use case: an ordered list of complementary offerings plus rationale. More expensive — runs Claude over top-K candidates." },
            new { name = "watchOffering",            priceUsdc = 0.50, slaMinutes = 5, description = "Subscribe to webhook alerts when new offerings match a query. Polls on a configurable cadence over the watch window." },
            new { name = "agentReputation",          priceUsdc = 0.05, slaMinutes = 10, description = "Live computed reputation for an agent address: composite 0-100 score with on-chain behavioural signals (90-day window)." },
            // v1.7 Bundle A — Arena integration
            new { name = "arenaParticipants",        priceUsdc = 0.05, slaMinutes = 5, description = "Bulk pre-hire gate. For 1-25 agent addresses, returns per-address Degen Arena participation: indexed yes/no, lifetime + 30d ranks, PnL, last-week Council pick. Cached." },
            // v1.7 Bundle B — Buyer Agent Toolkit
            new { name = "buyerOrchestrate",         priceUsdc = 0.10, slaMinutes = 5, description = "composeStack with reputation + Arena participation badges. Returns a use-case-driven stack with each seller's cached reputation summary and Arena rank attached as a trust signal." },
            new { name = "preHireBudgetCheck",       priceUsdc = 0.05, slaMinutes = 5, description = "Given 1-25 offering IDs, returns per-offering price + total USDC + any missing IDs. Lets a buyer agent compute exact escrow before issuing any individual hire." },
            // v1.7 Bundle C — Seller-Success Coach + V1↔V2 portage
            new { name = "sellerCoachingPack",       priceUsdc = 1.00, slaMinutes = 5, description = "Premium seller-success report: per-offering 0-100 health score, overall verdict (STRONG / OK_WITH_GAPS / WEAK), and prioritised remediation list (missing schemas, sub-min prices, short descriptions, zero-hire offerings, missing Resources)." },
            new { name = "v1Tov2Migration",          priceUsdc = 0.50, slaMinutes = 5, description = "Per-offering V1→V2 migration plan: split + verdict + ordered migration steps (most-hired V1 offering first) with the V2 marketplace requirements you must satisfy. Pairs with the free /v1/resources/marketplaceVersionMap Resource." }
        },
        notes = "Prices in USDC, slaMinutes is the wall-clock window from hire to deliverable. Full requirement and deliverable schemas live on each offering's marketplace card."
    });
}).RequireRateLimiting("public-resources");

// Chain coverage — distinguishes WHERE TheMetaBot accepts hires (operatedOn)
// from WHICH chains its indexer covers (indexed). Static answer: TheMetaBot's
// indexer pulls V1 (Base mainnet only) + V2 (Base mainnet + Base Sepolia)
// from the upstream ACP API.
app.MapGet("/v1/resources/chainCoverage", () =>
{
    return Results.Ok(new
    {
        operatedOn = new object[]
        {
            new { chainId = 8453, name = "Base mainnet", role = "TheMetaBot accepts hires here." }
        },
        indexed = new object[]
        {
            new { chainId = 8453,  name = "Base mainnet", marketplaceVersions = new[] { "v1", "v2" } },
            new { chainId = 84532, name = "Base Sepolia", marketplaceVersions = new[] { "v2" } }
        },
        notes = "search / searchAgents / today / composeStack / browseAgent default to spanning both V1 + V2. Use the marketplace filter to restrict to one."
    });
}).RequireRateLimiting("public-resources");

// ===== v1.7 Bundle B — Buyer Agent Toolkit (R6-IDEA-4 promoted) =====
//
// Free Resources that let buyer agents self-orchestrate without paying a
// single Metabot hire. Demand-side primitive — pairs naturally with the
// acp_estimate_stack_cost MCP tool shipped in acp-find-plugin v0.8.0.
//
// IMPORTANT: the two wallet-check Resources are informational-only in v1.7.
// They DO NOT make live RPC calls (Metabot doesn't carry a per-buyer RPC
// budget); they return the canonical procedure plus where to look on-chain
// so the buyer agent can self-verify. v1.8 may add a real Alchemy probe
// once we wire Metabot to ChainlinkBot's RPC budget.

app.MapGet("/v1/resources/buyerWalletDelegationCheck", () =>
{
    return Results.Ok(new
    {
        description = "How to verify a buyer wallet has the EIP-7702 delegation the ACP v2 SDK requires before issuing any hire. Returns the expected ModularAccountV2 delegation prefix, a probe procedure, and the recovery path when drift is detected.",
        expectedDelegationPrefix = "0xef010069007702764179f14F51cdce752f4f775d74E139",
        probeRpcCall = new
        {
            method = "eth_getCode",
            paramsTemplate = new object[] { "<bufferWallet>", "latest" }
        },
        baseRpcDefault = "https://base.publicnode.com",
        passCondition  = "Response starts with 0xef0100<impl> where impl = SUPPORTED_DELEGATION_ADDRESSES[0] in @alchemy/wallet-api-types/dist/esm/capabilities/eip7702Auth.js.",
        failRecoveryHint = "If the wallet is undelegated or pointing at the wrong impl, sign an EIP-7702 type-4 authorization via Privy's signer.signAuthorization and broadcast it from a sponsor EOA. ACP_BasicBot and ACP_BasicSubscriptionBot ship acp-v2/src/walletDelegation.ts that does this end-to-end."
    });
});

app.MapGet("/v1/resources/buyerUsdcReadiness", () =>
{
    return Results.Ok(new
    {
        description = "How to verify a buyer smart-account wallet holds enough USDC on its target chain BEFORE attempting an ACP hire. Returns the canonical USDC contract addresses + balanceOf call shape per chain. Reminder: buyers using Privy WaaS smart accounts must check the SMART-ACCOUNT address, not the owner EOA — USDC lands at the smart account.",
        usdcContracts = new[]
        {
            new { chainId = 8453,  symbol = "USDC", address = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913" },
            new { chainId = 84532, symbol = "USDC", address = "0x036CbD53842c5426634e7929541eC2318f3dCF7e" },
            new { chainId = 1,     symbol = "USDC", address = "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48" }
        },
        balanceOfCallTemplate = "balanceOf(address) -> uint256 — selector 0x70a08231",
        smartAccountReminder = "If the buyer is on Privy WaaS, ACP_WALLET_ADDRESS in their .env is the ERC-4337 smart account, NOT the owner EOA. USDC top-ups must hit the smart account."
    });
});

app.MapGet("/v1/resources/offeringSchemaTemplate",
    async ([Microsoft.AspNetCore.Mvc.FromQuery] string? offeringId, OfferingRepository repo) =>
{
    // v1.7.1: bind as string so ASP.NET model binding can't 400-empty-body when
    // a buyer agent passes the offering NAME (e.g. "searchAgents") instead of
    // the numeric id. Parse manually and surface a helpful 400 either way.
    if (string.IsNullOrWhiteSpace(offeringId))
        return Results.BadRequest(new
        {
            error = "offeringId query param required",
            hint  = "Pass the numeric offering id from /v1/search or /v1/agent/{addr} results (the `id` field on each offering), not the offering name."
        });

    if (!long.TryParse(offeringId.Trim(), System.Globalization.NumberStyles.Integer,
                       System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
        return Results.BadRequest(new
        {
            error    = "offeringId must be a positive integer",
            received = offeringId,
            hint     = "If you only know the offering name, call /v1/agent/{addr} (free) and grab the numeric `id` field from the matching offering. The marketplace assigns one integer id per offering, per agent."
        });

    var off = await repo.GetByIdAsync(id);
    if (off is null)
        return Results.NotFound(new { error = "offering_not_found", offeringId = id });

    System.Text.Json.JsonElement? schema = null;
    if (!string.IsNullOrEmpty(off.RequirementSchemaJson))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(off.RequirementSchemaJson);
            schema = doc.RootElement.Clone();
        }
        catch { /* malformed schema — surface as null */ }
    }

    return Results.Ok(new
    {
        offeringId          = off.Id,
        offeringName        = off.OfferingName,
        agentAddress        = off.AgentAddress,
        marketplaceVersion  = off.MarketplaceVersion ?? "v1",
        priceUsdc           = off.PriceUsdc,
        requirementSchema   = schema,
        note                = schema is null
            ? "No requirement schema indexed for this offering — buyer should browse the offering's marketplace page directly."
            : "Requirement schema as indexed by Metabot. Buyers can use this to pre-validate their requirement payload."
    });
});

app.MapGet("/v1/resources/supportedChainsByCategory",
    (CategoryService cats) =>
{
    return Results.Ok(new
    {
        description = "List of canonical marketplace categories plus the chain(s) they are predominantly offered on. v1 returns the canonical list; per-category live chain rollup is planned for v1.8 once the offerings GROUP BY is moved off the hot path.",
        defaultChains = new[]
        {
            new { chainId = 8453, name = "Base mainnet" },
            new { chainId = 84532, name = "Base Sepolia" }
        },
        categories = cats.Categories
    });
});

// ===== v1.7 Bundle C — Seller-Success Coach + V1↔V2 portage =====

app.MapGet("/v1/resources/sellerDiagnose",
    async ([Microsoft.AspNetCore.Mvc.FromQuery] string? agent, OfferingRepository repo, AgentResourcesRepository resRepo, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(agent))
        return Results.BadRequest(new { error = "agent query param required" });
    var addr = agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address" });

    var offerings = await repo.ListByAgentAsync(addr);
    if (offerings.Count == 0)
        return Results.Ok(new
        {
            agentAddress = addr,
            verdict = "NOT_INDEXED",
            issues = new[]
            {
                "Agent address has zero indexed offerings on either V1 or V2. Verify the agent is provisioned on app.virtuals.io and run `npm run print-offerings` against the bot's acp-v2/ dir to confirm registration."
            }
        });

    var issues = new List<string>();
    foreach (var o in offerings)
    {
        if (string.IsNullOrEmpty(o.RequirementSchemaJson))
            issues.Add($"offering `{o.OfferingName}` has no requirement_schema indexed — buyers can't pre-validate; re-register with a schema.");
        if (o.OfferingName.Length > 20)
            issues.Add($"offering `{o.OfferingName}` exceeds the 20-char marketplace name cap.");
        if (o.PriceUsdc <= 0)
            issues.Add($"offering `{o.OfferingName}` has non-positive price ({o.PriceUsdc}) — marketplace minimum is $0.01.");
        if (string.IsNullOrEmpty(o.Description) || o.Description.Length < 30)
            issues.Add($"offering `{o.OfferingName}` description is too short (< 30 chars) — buyer agents skip these in search.");
    }

    var resources = await resRepo.ListByAgentAsync(addr, ct);
    if (resources.Count == 0)
        issues.Add("Agent has not registered any free Resources. Resources are the demand-side primitive — buyer / orchestrator agents (Butler-style) discover you via Resources before paying. Add at least a `capabilities` Resource.");

    return Results.Ok(new
    {
        agentAddress  = addr,
        verdict       = issues.Count == 0 ? "HEALTHY" : "ISSUES_FOUND",
        offeringCount = offerings.Count,
        resourceCount = resources.Count,
        issues
    });
});

app.MapGet("/v1/resources/marketplaceVersionMap",
    async ([Microsoft.AspNetCore.Mvc.FromQuery] string? agent, OfferingRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(agent))
        return Results.BadRequest(new { error = "agent query param required" });
    var addr = agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address" });

    var offerings = await repo.ListByAgentAsync(addr);
    var grouped = offerings
        .GroupBy(o => o.MarketplaceVersion ?? "v1")
        .ToDictionary(g => g.Key, g => g.Count());
    var v1Count = grouped.TryGetValue("v1", out var v1) ? v1 : 0;
    var v2Count = grouped.TryGetValue("v2", out var v2) ? v2 : 0;
    return Results.Ok(new
    {
        agentAddress = addr,
        v1OfferingCount = v1Count,
        v2OfferingCount = v2Count,
        dominantMarketplace = v1Count > v2Count ? "v1" : (v2Count > v1Count ? "v2" : (v1Count + v2Count == 0 ? "none" : "tied")),
        migrationHint = v1Count > 0 && v2Count == 0
            ? "Agent is V1-only. V2 marketplace (api.acp.virtuals.io) is the new generation; migrating brings access to V2-native features like Resources, Subscription tiers, and the new ACP escrow flow. See acp-find-plugin/docs."
            : null
    });
});

// v1.7.2: search / searchAgents / browseAgent demoted from $0.01 paid
// offerings to free Resources (see acp-v2/src/resources.ts for the rationale
// + acp-v2/src/offerings/registry.ts for the removal). Same backing services
// as the legacy POST endpoints (/search, /searchAgents, /v1/agent/{addr});
// just a simpler GET query-param surface and the public-resources rate-limit
// policy (120/IP/hr).

app.MapGet("/v1/resources/search",
    async ([FromQuery] string? query, [FromQuery] int? limit,
        SearchService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "query query param required" });
    if (query.Length > MaxQueryLen)
        return Results.BadRequest(new { error = $"query must be {MaxQueryLen} characters or fewer" });
    var lim = limit is null ? 10 : Math.Clamp(limit.Value, 1, 50);
    var results = await svc.SearchAsync(
        query, lim,
        minScore: 0.0, priceMaxUsdc: double.PositiveInfinity,
        staleAfterDays: null, rerank: true, categoryFilter: null,
        chainFilter: null, minReputation: null, marketplaceFilter: null, ct);
    return Results.Ok(new { query, count = results.Count, results });
}).RequireRateLimiting("public-resources");

app.MapGet("/v1/resources/searchAgents",
    async ([FromQuery] string? query, [FromQuery] int? limit,
        AgentSearchService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "query query param required" });
    if (query.Length > MaxQueryLen)
        return Results.BadRequest(new { error = $"query must be {MaxQueryLen} characters or fewer" });
    var lim = limit is null ? 5 : Math.Clamp(limit.Value, 1, 50);
    var hits = await svc.SearchAsync(query, lim, marketplaceFilter: null, ct);
    return Results.Ok(new { query, count = hits.Count, agents = hits });
}).RequireRateLimiting("public-resources");

app.MapGet("/v1/resources/browseAgent",
    ([FromQuery] string? agent,
        OfferingRepository repo, ReputationService reputation,
        CrossPresenceBuilder crossPresence, PricePercentileCalculator pricePercentile,
        SearchService search) =>
{
    if (string.IsNullOrWhiteSpace(agent))
        return Task.FromResult(Results.BadRequest((object)new { error = "agent query param required" }));
    return HandleBrowseAgent(agent, repo, reputation, crossPresence, pricePercentile, search);
}).RequireRateLimiting("public-resources");

// R7-IDEA-C: cross-agent ACP v2 Resource index. AcpV2MarketplaceSource
// persists each indexed agent's `resources` array into agent_resources as
// a side-effect of its per-wallet fetch (see Services/MarketplaceSource/
// AcpV2MarketplaceSource.cs). These endpoints expose that index so the
// acp-find MCP server can surface Resources marketplace-wide.
//
// Path namespace note: Metabot's OWN Resources (registered via R7-IDEA-A)
// live at /v1/resources/<name>. THIS surface — for OTHER agents' Resources
// indexed from upstream — lives under /v1/agent/{address}/resources +
// /v1/marketplace/resources/search to avoid name collision.

// Per-agent Resource list. Single-table query keyed on agent_address.
// Returns 200 with empty list when the agent has no indexed Resources
// (instead of 404) so a buyer agent can distinguish "agent not indexed"
// from "agent has zero Resources" via the broader /v1/agent/{address}.
app.MapGet("/v1/agent/{address}/resources",
    async (string address, AgentResourcesRepository repo, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(address))
            return Results.BadRequest(new { error = "agentAddress is required" });
        var addr = address.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
            return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });

        var rows = await repo.ListByAgentAsync(addr, ct);
        var dtos = rows.Select(r =>
        {
            System.Text.Json.JsonElement? schema = null;
            if (!string.IsNullOrEmpty(r.ParamsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(r.ParamsJson);
                    schema = doc.RootElement.Clone();
                }
                catch
                {
                    // Malformed cached params — surface as null rather than
                    // 500 on the whole response.
                }
            }
            return new
            {
                agentAddress       = r.AgentAddress,
                agentName          = r.AgentName,
                name               = r.Name,
                url                = r.Url,
                paramsSchema       = schema,
                description        = r.Description,
                marketplaceVersion = r.MarketplaceVersion,
                firstSeenAt        = r.FirstSeenAt.ToString("O"),
                lastSeenAt         = r.LastSeenAt.ToString("O")
            };
        }).ToArray();

        return Results.Ok(new
        {
            agentAddress = addr,
            count = dtos.Length,
            resources = dtos
        });
    }).RequireRateLimiting("public-marketplace-resources");

// Per-agent reputation feed lookup (v1.6 #1 v0.1+v0.2). Returns the on-chain
// AggregatorV3 contract address ChainlinkBot deployed for this agent — the
// same shape DeFi protocols use to read Chainlink price feeds. Public, so
// buyer-side code can discover the feed address without going through the
// operator-only /feeds/published endpoint. 404 when the agent hasn't been
// published to the on-chain feed yet (i.e. not in reputation_feeds, or
// publisher worker recorded an error row with empty aggregator_address).
app.MapGet("/v1/agent/{address}/feed-address",
    async (string address, ReputationFeedRepository repo) =>
    {
        if (string.IsNullOrWhiteSpace(address))
            return Results.BadRequest(new { error = "invalid_address", message = "agentAddress is required" });
        var addr = address.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
            return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });

        var row = await repo.GetAsync(addr);
        if (row is null || string.IsNullOrEmpty(row.AggregatorAddress))
            return Results.NotFound(new
            {
                error = "no_feed",
                agentAddress = addr,
                hint = "This agent hasn't been published as an on-chain reputation feed yet. Top-N agents by cached score get a feed when the daily ReputationFeedPublisherWorker runs. Subscribe via /v1/watches/* (not implemented for feeds yet) or check back after the next daily run."
            });

        return Results.Ok(new
        {
            agentAddress      = row.AgentAddress,
            // Base mainnet — ChainlinkBot deploys feeds on chain 8453 only.
            // If feeds are ever multi-chain, source this from config.
            chainId           = 8453,
            aggregatorAddress = row.AggregatorAddress,
            methodologyHash   = row.MethodologyHash,
            decimals          = row.Decimals,
            latestScore       = row.LatestScore,
            deployedAt        = row.DeployedAt.ToString("O"),
            firstSeenAt       = row.FirstSeenAt.ToString("O"),
            lastPushedRound   = row.LastPushedRound,
            lastPushedAt      = row.LastPushedAt?.ToString("O"),
            // Convenience: a Basescan link to the deployed aggregator so the
            // caller can verify the contract by eye / wire up their reader.
            explorerUrl       = $"https://basescan.org/address/{row.AggregatorAddress}",
            notes             = "Reads conform to Chainlink AggregatorV3Interface (decimals=8, range 0..100*1e8). See ACP_ChainlinkBot/docs/REPUTATION_FEEDS.md."
        });
    }).RequireRateLimiting("public-marketplace-resources");

// ===== v1.7 Bundle A — Arena marketplace integration =====

// Single-agent Arena state. Returns the same envelope shape that
// /v1/agentReputation's arenaParticipation sub-block would inline, but
// exposed as a standalone endpoint for orchestrators that want only
// the Arena slice without paying for full reputation evidence.
app.MapGet("/v1/agent/{address}/arena",
    async (string address, AgentArenaParticipationRepository repo) =>
    {
        if (string.IsNullOrWhiteSpace(address))
            return Results.BadRequest(new { error = "invalid_address" });
        var addr = address.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
            return Results.BadRequest(new { error = "invalid_address" });

        var row = await repo.GetByAddressAsync(addr);
        if (row is null)
            return Results.Ok(new
            {
                agentAddress  = addr,
                isParticipant = false,
                note          = "Agent not yet indexed against Degen Arena. Either not a participant, or ArenaSourceWorker hasn't ingested this address yet."
            });
        return Results.Ok(new
        {
            agentAddress       = row.AgentAddress,
            isParticipant      = row.IsParticipant,
            rankLifetime       = row.RankLifetime,
            rank30d            = row.Rank30d,
            pnlLifetimeUsd     = row.PnlLifetimeUsd,
            pnl30dUsd          = row.Pnl30dUsd,
            lastWeekPick       = row.LastWeekPick,
            firstSeenInArenaAt = row.FirstSeenInArenaAt?.ToString("O"),
            lastObservedAt     = row.LastObservedAt.ToString("O"),
            source             = row.Source
        });
    }).RequireRateLimiting("public-marketplace-resources");

// Bulk list of indexed Arena participants ordered by 30-day rank.
app.MapGet("/v1/arena/agents",
    async (int? limit, AgentArenaParticipationRepository repo) =>
    {
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var rows = await repo.ListAsync(lim);
        return Results.Ok(new
        {
            count = rows.Count,
            agents = rows.Select(r => new
            {
                agentAddress = r.AgentAddress,
                rankLifetime = r.RankLifetime,
                rank30d      = r.Rank30d,
                pnl30dUsd    = r.Pnl30dUsd,
                lastWeekPick = r.LastWeekPick,
                lastObservedAt = r.LastObservedAt.ToString("O")
            }).ToArray()
        });
    }).RequireRateLimiting("public-marketplace-resources");

// Recent council picks cached by Metabot for cohort-overlap queries.
app.MapGet("/v1/arena/council-picks",
    async (int? weeks, AgentArenaParticipationRepository repo) =>
    {
        var w = Math.Clamp(weeks ?? 4, 1, 26);
        var rows = await repo.GetRecentCouncilCacheAsync(w);
        var byWeek = rows.GroupBy(r => r.WeekStart)
                          .OrderByDescending(g => g.Key)
                          .Select(g => new
                          {
                              weekStart = g.Key.ToString("O"),
                              picks = g.OrderBy(p => p.PickRank)
                                       .Select(p => new { p.AgentAddress, p.PickRank })
                                       .ToArray()
                          }).ToArray();
        return Results.Ok(new { weeks = w, data = byWeek });
    }).RequireRateLimiting("public-marketplace-resources");

// Marketplace cohort overlap — how many of the active Arena Top-N are ALSO
// active ACP service sellers. Powers a quick metric: "does Arena talent
// also sell offerings on app.virtuals.io?"
app.MapGet("/v1/marketplace-overlap",
    async (int? topN,
        AgentArenaParticipationRepository arenaRepo,
        OfferingRepository offRepo) =>
    {
        var n = Math.Clamp(topN ?? 50, 10, 500);
        var arenaTop = await arenaRepo.ListAsync(n);
        var matches = new List<object>();
        foreach (var a in arenaTop)
        {
            var offerings = await offRepo.ListByAgentAsync(a.AgentAddress);
            if (offerings.Count == 0) continue;
            matches.Add(new
            {
                agentAddress  = a.AgentAddress,
                arenaRank30d  = a.Rank30d,
                offeringCount = offerings.Count
            });
        }
        return Results.Ok(new
        {
            arenaTopN       = n,
            arenaSampled    = arenaTop.Count,
            sellingOnAcp    = matches.Count,
            overlapFraction = arenaTop.Count == 0 ? 0 : Math.Round((double)matches.Count / arenaTop.Count, 3),
            agents          = matches
        });
    }).RequireRateLimiting("public-marketplace-resources");

// ===== v1.7 Bundle A Resources =====

app.MapGet("/v1/resources/arenaParticipantCount",
    async (AgentArenaParticipationRepository repo) =>
    {
        var count = await repo.CountParticipantsAsync();
        var lastObs = await repo.GetLastObservedAtAsync();
        return Results.Ok(new
        {
            count,
            lastObservedAt = lastObs?.ToString("O"),
            description = "Total ACP agents Metabot has cross-indexed against the Degen Arena leaderboard. Refreshed by ArenaSourceWorker (default 15-min cadence) from ArenaBot's free Resources surface."
        });
    });

app.MapGet("/v1/resources/lastArenaPollAt",
    async (AgentArenaParticipationRepository repo) =>
    {
        var last = await repo.GetLastObservedAtAsync();
        return Results.Ok(new
        {
            lastObservedAt = last?.ToString("O"),
            stale          = last is null || (DateTime.UtcNow - last.Value).TotalHours > 1
        });
    });

app.MapGet("/v1/resources/cohortOverlap",
    async (AgentArenaParticipationRepository arenaRepo, OfferingRepository offRepo) =>
    {
        var top50 = await arenaRepo.ListAsync(50);
        var alsoSelling = 0;
        foreach (var a in top50)
        {
            var offerings = await offRepo.ListByAgentAsync(a.AgentAddress);
            if (offerings.Count > 0) alsoSelling++;
        }
        return Results.Ok(new
        {
            sampleSize      = top50.Count,
            alsoSellOnAcp   = alsoSelling,
            overlapFraction = top50.Count == 0 ? 0 : Math.Round((double)alsoSelling / top50.Count, 3),
            description     = "Of the Top-50 Arena agents Metabot has indexed, how many also sell ACP offerings? A 'high' overlap means Arena performance correlates with marketplace presence."
        });
    });

// Cross-agent Resource search. LIKE-based match on name + description +
// agent_name. v1 is fine at the current ~500-row scale; v1.1 may upgrade
// to FTS5 + a new agent_resources_fts virtual table.
app.MapGet("/v1/marketplace/resources/search",
    async (string? query, int? limit, string? marketplace,
        AgentResourcesRepository repo, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { error = "query is required" });
        if (query.Length > 200)
            return Results.BadRequest(new { error = "query must be 200 characters or fewer" });
        var cap = limit is null ? 25 : Math.Clamp(limit.Value, 1, 100);
        string? mvFilter = null;
        if (!string.IsNullOrWhiteSpace(marketplace))
        {
            var mv = marketplace.Trim().ToLowerInvariant();
            if (mv is not ("v1" or "v2"))
                return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });
            mvFilter = mv;
        }

        var rows = await repo.SearchAsync(query, cap, mvFilter, ct);
        var dtos = rows.Select(r =>
        {
            System.Text.Json.JsonElement? schema = null;
            if (!string.IsNullOrEmpty(r.ParamsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(r.ParamsJson);
                    schema = doc.RootElement.Clone();
                }
                catch { /* surface as null */ }
            }
            return new
            {
                agentAddress       = r.AgentAddress,
                agentName          = r.AgentName,
                name               = r.Name,
                url                = r.Url,
                paramsSchema       = schema,
                description        = r.Description,
                marketplaceVersion = r.MarketplaceVersion,
                lastSeenAt         = r.LastSeenAt.ToString("O")
            };
        }).ToArray();

        return Results.Ok(new
        {
            query,
            marketplace = mvFilter,
            count = dtos.Length,
            results = dtos
        });
    }).RequireRateLimiting("public-marketplace-resources");

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

// Internal cross-bot endpoint — returns lowercased addresses of agents with at
// least one non-tombstoned offering seen in the last 30 days. Consumed by
// ACP_ChainlinkBot (over the acp-shared docker network) to enumerate which
// agents to score on-chain. Path lives under /v1/* but is special-cased in the
// X-API-Key middleware above so callers must hold INTERNAL_API_KEY.
//
// `windowDays` is the configurable lookback (default 30, clamped 1..365).
app.MapGet("/v1/agents/active",
    async (int? windowDays, OfferingRepository repo, CancellationToken ct) =>
    {
        var window = windowDays is null ? 30 : Math.Clamp(windowDays.Value, 1, 365);
        var addrs = await repo.ListActiveAgentAddressesAsync(window, ct);
        return Results.Ok(new
        {
            windowDays = window,
            count = addrs.Count,
            agents = addrs
        });
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

// Operator-only: list all ReputationAggregator feeds that TheMetaBot has
// asked ChainlinkBot to publish (v1.6 #1). Outside /v1/* so the X-API-Key
// middleware gates it. Consumers (e.g. DeFi protocols wanting to wire an
// AggregatorV3 read on an ACP agent's reputation) currently learn feed
// addresses out-of-band; v0.2 will surface this as a public Resource.
app.MapGet("/feeds/published", async (ReputationFeedRepository repo) =>
{
    var rows = await repo.ListAllAsync(limit: 500);
    return Results.Ok(new
    {
        count = rows.Count,
        feeds = rows.Select(r => new
        {
            agentAddress      = r.AgentAddress,
            aggregatorAddress = r.AggregatorAddress,
            methodologyHash   = r.MethodologyHash,
            decimals          = r.Decimals,
            latestScore       = r.LatestScore,
            deployedAt        = r.DeployedAt.ToString("O"),
            lastPushedRound   = r.LastPushedRound,
            lastPushedAt      = r.LastPushedAt?.ToString("O"),
            lastError         = r.LastError
        })
    });
});

// Operator-only: trigger a publish run immediately (useful in dev and for
// catching agents that crossed the score threshold between daily cron ticks).
app.MapPost("/feeds/publish-now", async (
    ReputationFeedPublisherWorker worker, CancellationToken ct) =>
{
    var published = await worker.RunOnceAsync(DateTime.UtcNow, ct);
    return Results.Ok(new { ok = true, published });
});

// Operator-only: trigger a sync run immediately (v0.2). Polls ChainlinkBot
// for every deployed feed and refreshes last_pushed_round + last_pushed_at.
app.MapPost("/feeds/sync-now", async (
    ReputationFeedSyncWorker worker, CancellationToken ct) =>
{
    var (synced, notPushed, failed) = await worker.RunOnceAsync(ct);
    return Results.Ok(new { ok = true, synced, notPushed, failed });
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

// v1.7 paid offerings DTOs
public record ArenaParticipantsBulkRequest(string[] Addresses);
public record BuyerOrchestrationRequest(string UseCase, double? BudgetUsdc, int? MaxOfferings);
public record BudgetCheckRequest(long[] OfferingIds);
public record SellerCoachingRequest(string Agent);
public record V1Tov2MigrationRequest(string Agent);

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
