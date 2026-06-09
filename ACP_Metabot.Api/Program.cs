using System.Threading.RateLimiting;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Middleware;
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
// 2026-05-24 hardening: AES-256-GCM cipher for webhook_secret at rest. Opt-in
// via WEBHOOK_SECRET_ENCRYPTION_KEY (base64 32 bytes); no-op plaintext
// passthrough when unset. Wrapped into PulseSubscriptionRepository +
// RiskSubscriptionRepository transparently. Lazy migration via "v1:" prefix.
builder.Services.AddSingleton<WebhookSecretCipher>();
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
builder.Services.AddSingleton<SecurityVerdictRepository>();
builder.Services.AddSingleton<SecurityScanHistoryRepository>(); // worker scope resolves it; also the seam for the deferred read endpoint
// Shared scan-and-persist seam. Depends on ITheSecurityBotClient (registered
// below at the thesecuritybot HttpClient block). Resolved by SecurityScanWorker's
// per-tick scope AND the on-demand POST /admin/securityScan endpoint — one write-path.
builder.Services.AddSingleton<SecurityScanService>();
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
// Cross-bot HTTP client to ACP_SecurityBot's free internal scan endpoint over
// acp-shared. BaseAddress from TheSecurityBot:BaseUrl (default: the bridge
// service name); THESECURITYBOT_API_KEY (-> TheSecurityBot:ApiKey via compose)
// read by the typed client. P39: pin the resolved IP at connect time + refuse
// 3xx so a DNS-rebind / compromised-peer redirect can't bounce the key-bearing
// request to cloud-metadata / link-local.
builder.Services.AddHttpClient("thesecuritybot", c =>
{
    var baseUrl = builder.Configuration["TheSecurityBot:BaseUrl"]
        ?? "http://securitybot-api:5000/";
    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout = TimeSpan.FromSeconds(30); // scans probe a live surface; allow a few s
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectCallback   = ACP_Metabot.Api.Services.AcpClients.InternalConnectCallbacks.PinResolvedIp,
});
builder.Services.AddSingleton<TheSecurityBotClient>();
builder.Services.AddSingleton<ITheSecurityBotClient>(sp => sp.GetRequiredService<TheSecurityBotClient>());
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
// 2026-05-25 hardening (audit #1): ClaudeApiClient was using the default
// IHttpClientFactory client (no timeout, no resilience) — a hanging Anthropic
// request would tie up /v1/composeStack + /v1/searchNarrative requests. Now
// the named client has an explicit 60s timeout + StandardResilienceHandler
// (3 retries with jittered exponential backoff on 5xx + transient errors).
builder.Services.AddHttpClient(nameof(ClaudeApiClient), c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
}).AddStandardResilienceHandler();
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
// v1.10 Phase 1: DeFi glossary loaded once at boot. The Content Update entry
// in ACP_Metabot.Api.csproj copies Data/DeFiGlossary.json into the publish
// output (/app/Data/... on the droplet, bin/Debug/.../Data/... under
// dotnet test). The walk-up fallback covers `dotnet run` invocations whose
// AppContext.BaseDirectory diverges from the project layout (rare, but
// cheap to handle).
builder.Services.AddSingleton<QueryExpander>(_ =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Data", "DeFiGlossary.json");
    if (!File.Exists(path))
    {
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !File.Exists(Path.Combine(root, "Data", "DeFiGlossary.json")); i++)
            root = Path.GetDirectoryName(root) ?? root;
        path = Path.Combine(root, "Data", "DeFiGlossary.json");
    }
    return QueryExpander.LoadFromFile(path);
});
// v1.10 Phase 3: Claude-backed query rewriter. Opt-in via filters.Expand=true
// on /search; falls through to passthrough on degraded paths (cap breached,
// key missing, LLM error). All deps already registered: IClaudeClient (line 101),
// Db (default container), IConfiguration (framework-provided).
builder.Services.AddSingleton<LlmQueryRewriter>();
// v1.10 Phase 3 T4: Claude-backed search narrator. Wraps the top-5 offerings
// in a 3-5 sentence summary + per-result reasoning. Caches via
// search_narratives_cache for `Search:NarrativeCacheTtlSeconds` (default 1h).
// Never throws — degraded envelope on LLM/cache/parse failure.
builder.Services.AddSingleton<SearchNarrator>();
builder.Services.AddSingleton<SearchService>();
// v1.10 Phase 3 T5: defensive scam-risk scorer. 4 signals × 25 = 100 binned
// to low / medium / high / critical. Cached per (agent_address, chain_id)
// for `Search:AgentRiskCacheTtlSeconds` (default 6h). Depends on
// SearchService for in-corpus category lookup (pricing-outlier signal);
// degrades gracefully when the corpus hasn't refreshed yet.
builder.Services.AddSingleton<AgentRiskScorer>();
// ACPPurchaser Path A (R16 #1 cold-start fix). IAgentRiskSource seams the
// scorer for the verdict logic; PurchaserService owns quote/precheck/settle.
builder.Services.AddSingleton<IAgentRiskSource, AgentRiskScorerSource>();
builder.Services.AddSingleton<PurchaserBudgetService>();
builder.Services.AddSingleton<PurchaserService>();
builder.Services.AddSingleton<CrossPresenceBuilder>();
builder.Services.AddSingleton<AgentSearchService>();
builder.Services.AddSingleton<StackComposerService>();
// SSRF-safe outbound HTTP primitive — shared by WebhookDeliveryService,
// MarketplacePulseService, and RiskWatchWorker. Owns one HttpClient with
// AllowAutoRedirect=false and per-hop WebhookUrlValidator. Must be a
// singleton: HttpClient instances are intended to be long-lived, and the
// SocketsHttpHandler connection pool is what makes per-tick delivery fast.
builder.Services.AddSingleton<SafeWebhookHttpClient>();
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
builder.Services.AddHostedService<SecurityScanWorker>();
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

// v1.9 marketplaceGap ($0.30) — repackages the saturationMap that today/digest
// already computes into a structured opportunity ranking with recommendation
// tags. See Services/MarketplaceGapService.cs.
builder.Services.AddSingleton<MarketplaceGapService>();

// v1.9 marketplacePulseSub ($4 / 30-day daily digest subscription). HMAC-
// signed webhook tick loop on the BasicSubscriptionBot pattern. Default OFF;
// flip MarketplacePulse:Worker:Enabled=true after first hire.
builder.Services.AddSingleton<PulseSubscriptionRepository>();
builder.Services.AddSingleton<MarketplacePulseService>();
builder.Services.AddSingleton<MarketplacePulseWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketplacePulseWorker>());

// v1.10 Phase 1: ResourcesEmbeddingsWorker — default OFF. Flip
// Resources:EmbedWorker:Enabled=true (env: RESOURCES_EMBED_WORKER_ENABLED=true)
// on the droplet to start lazy-backfilling resources_embeddings. Phase 1 ships
// the plumbing; FTS-based SearchHybridAsync (T6) is the primary Resource-
// search lane until embedding-based ranking lands in Phase 2+.
var resourcesEmbedEnabled =
    builder.Configuration.GetValue<bool?>("Resources:EmbedWorker:Enabled") == true
    || string.Equals(
        Environment.GetEnvironmentVariable("RESOURCES_EMBED_WORKER_ENABLED"),
        "true", StringComparison.OrdinalIgnoreCase);
if (resourcesEmbedEnabled)
{
    builder.Services.AddHostedService<ResourcesEmbeddingsWorker>();
}

// ── v1.8 Portfolio Risk Bot — 5 cross-bot orchestrator offerings ─────────
//
// risk_snapshot fans out to LiquidGuard + RevokeBot + MEVProtect + the
// internal reputation cache. risk_attestation also calls EASIssuer.
// Cross-bot calls go over the acp-shared docker bridge; per-bot API keys
// are loaded from the disambiguated *_API_KEY env vars per the
// cross-bot-key-sync convention. RiskWatchWorker is default OFF (set
// RiskWatch:Worker:Enabled=true to activate the daily push loop).
builder.Services.AddSingleton<RiskSubscriptionRepository>();
builder.Services.AddSingleton<IRiskPeerClients, RiskPeerClients>();
builder.Services.AddSingleton<RiskSynthesisService>();
builder.Services.AddSingleton<RiskOrchestrationService>();
builder.Services.AddSingleton<RiskWatchWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RiskWatchWorker>());

// v1.0 riskAttestPro Task 3 — WitnessBot peer client. Distinct from
// IRiskPeerClients because the response is a typed manifest (not opaque
// JsonDocument) and the orchestrator (Task 6) needs to distinguish the
// "fresh + no manifest" 404 path from transport-class "unavailable" failures.
// When WitnessBot:BaseUrl / :ApiKey are unset the client itself returns the
// "unavailable" sentinel — no need for a separate Noop registration.
builder.Services.AddSingleton<IWitnessBotClient, WitnessBotClient>();

// v1.0 riskAttestPro Task 7 — EAS schema bootstrap. Idempotent registration
// of the riskAttestPro AgentRisk schema on first boot via the EASIssuer
// cross-bot lane. v1.0 ships with the live registration deferred (worker
// catches the NotImplementedException + LogWarning so the bot still boots);
// v1.0.1 will plumb the injector to easissuer-api:5000/v1/internal/schema.
builder.Services.AddHostedService<RiskAttestProSchemaBootstrapWorker>();

// v1.0 riskAttestPro Task 8 — services backing /v1/risk/attest-pro.
// Trajectory store, LLM narrator, the two test-shaped lookup seams over
// the reputation cache + Arena client, and the 7-lane orchestrator.
// Singletons (matching the RiskOrchestrationService singleton above) so
// the in-memory state inside RiskAttestProLlm's caches survives the request.
builder.Services.AddSingleton<RiskTrajectoryStore>();
builder.Services.AddSingleton<RiskAttestProLlm>();
builder.Services.AddSingleton<IRiskReputationLookup, RiskReputationLookup>();
builder.Services.AddSingleton<IRiskArenaLookup, RiskArenaLookup>();
builder.Services.AddSingleton<RiskAttestProService>();

// R12 Tier 1.2 — portfolioRollup service. Singleton so the 5-min cache is
// shared across all requests. No sibling HTTP work in v1; v1.1 will plumb
// sibling-probe via the existing IHttpClientFactory.
builder.Services.AddSingleton<PortfolioRollupService>();
// R18 (2026-06-07) — agentic-commerce discovery manifest. Read-only projection
// of the live portfolioRollup; backs /.well-known/agentic-commerce.json + /llms.txt.
builder.Services.AddSingleton<DiscoveryManifestService>();

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

// v1.10 Phase 2 (T3b): one-time schema_facets backfill if the index is sparse.
// Idempotent — re-running on a warm DB is a no-op via the UNIQUE constraint
// on schema_facets (offering_id, field_name, role). Non-fatal: a failed
// backfill logs a warning but doesn't block startup, since sub-offering
// filters degrade to "return empty results for that filter" rather than
// crashing the service. The < 10 threshold treats both a truly empty index
// AND a tiny dev DB as "needs backfill"; production warm DBs will skip.
try
{
    using var scope = app.Services.CreateScope();
    var bfRepo = scope.ServiceProvider.GetRequiredService<OfferingRepository>();
    var bfLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var existing = await bfRepo.CountSchemaFacetsAsync();
    if (existing < 10)
    {
        bfLogger.LogInformation(
            "[schema-facets] backfill starting (existing={n})", existing);
        var written = await bfRepo.BackfillSchemaFacetsAsync();
        bfLogger.LogInformation(
            "[schema-facets] backfill wrote up to {n} facet rows", written);
    }
    else
    {
        bfLogger.LogInformation(
            "[schema-facets] skipping backfill ({n} rows already present)",
            existing);
    }
}
catch (Exception ex)
{
    using var scope = app.Services.CreateScope();
    var bfLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    bfLogger.LogWarning(ex,
        "[schema-facets] backfill failed; sub-offering filters may return empty");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders();

// Defence-in-depth HSTS. Caddy is the only public ingress (terminates TLS
// at api.acp-metabot.dev) and the C# API never publishes ports to the host,
// so the practical attack surface is "someone misconfigures the reverse
// proxy to forward HTTP". Emitted unconditionally on non-Development —
// app.UseHsts() defers to Request.IsHttps, which depends on
// ForwardedHeaders applying X-Forwarded-Proto from Caddy at the right
// point in the pipeline; safer to just always emit, since this container
// is never directly exposed without TLS termination upstream.
// UseHttpsRedirection is intentionally NOT added because the container only
// listens on plain HTTP inside the docker bridge — a redirect would loop.
if (!app.Environment.IsDevelopment())
{
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Strict-Transport-Security"]
            = "max-age=31536000; includeSubDomains";
        await next();
    });
}

app.UseRateLimiter();

// 2026-05-24 hardening: per-X-API-Key + per-IP sliding-window limit on
// heavy internal endpoints that the existing ASP.NET RateLimiter doesn't
// cover (the existing per-policy limits attach by [EnableRateLimiting]
// attribute to public /v1/* endpoints only). My middleware adds a backstop
// per-X-API-Key bucket for cross-bot consumers (e.g. ChainlinkBot reading
// /v1/internal/agentReputation hot) and a per-IP backstop for the
// subscription-bind paths. Runs AFTER UseRateLimiter so public-policy
// 429s still fire first; my bucket is the second layer.
app.UseMiddleware<RateLimitMiddleware>();

// 2026-05-24 hardening: minimal security headers on every response.
// nosniff + Referrer-Policy always; Cache-Control: no-store on non-Resource
// paths so paid responses + subscription metadata aren't cached.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var p = ctx.Request.Path.Value ?? string.Empty;
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"]        = "no-referrer";
        ctx.Response.Headers["X-Frame-Options"]        = "DENY";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        if (!p.StartsWith("/v1/resources/", StringComparison.Ordinal) && p != "/health")
            ctx.Response.Headers["Cache-Control"] = "no-store";
        return Task.CompletedTask;
    });
    await next();
});

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
// Exception within /v1/*: /v1/agents/active and /v1/internal/* are
// authenticated endpoints. /v1/internal/* covers two kinds of caller:
//
//   1. Cross-bot reads over the acp-shared docker bridge (e.g.
//      ChainlinkBot reads /v1/internal/agentReputation without hitting
//      the per-IP public rate limit).
//   2. Sidecar-driven subscription creation
//      (/v1/internal/marketplace/pulse/subscribe,
//       /v1/internal/risk/watch). These mutate state and schedule
//      background work; only the ACP sidecar — which has proven escrow
//      lock — should be allowed to call them. Treating them as public
//      /v1/* would be a cost-abuse + free-subscription bypass.
//
// All /v1/internal/* paths require X-API-Key.
var apiKey = builder.Configuration["INTERNAL_API_KEY"];
// 2026-05-24 hardening: fail-fast on missing or short INTERNAL_API_KEY in
// any non-Development environment. Pre-hardening, every protected request
// returned 500 "INTERNAL_API_KEY is not configured" — that's a per-request
// failure mode rather than a boot-time signal. The min length floor catches
// operators who paste a short demo key. Portfolio pattern (ACP_OracleBot v0.7,
// ACP_ChainlinkBot v1.3.1).
if (!app.Environment.IsDevelopment())
{
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException(
            "INTERNAL_API_KEY is required in non-Development environments. " +
            $"Current environment: {app.Environment.EnvironmentName}. " +
            "Set the env var to a high-entropy random string (>= 32 chars).");
    if (apiKey.Length < 32)
        throw new InvalidOperationException(
            $"INTERNAL_API_KEY is only {apiKey.Length} characters; require >= 32 in non-Development. " +
            "Generate a stronger one: `openssl rand -hex 32`.");

    // 2026-05-25 hardening (audit #3): WEBHOOK_SECRET_ENCRYPTION_KEY now
    // REQUIRED in non-Development. Pre-sweep, the cipher silently became a
    // plaintext passthrough when the env var was unset and subscription
    // webhook_secret values sat plaintext in SQLite + backups. Opt-out via
    // METABOT_ALLOW_PLAINTEXT_WEBHOOK_SECRETS=true for transitional deploys.
    var webhookCipher = app.Services.GetRequiredService<WebhookSecretCipher>();
    var allowPlaintextSecrets = string.Equals(
        builder.Configuration["METABOT_ALLOW_PLAINTEXT_WEBHOOK_SECRETS"]
            ?? Environment.GetEnvironmentVariable("METABOT_ALLOW_PLAINTEXT_WEBHOOK_SECRETS"),
        "true", StringComparison.OrdinalIgnoreCase);
    if (!webhookCipher.IsEncryptionEnabled && !allowPlaintextSecrets)
        throw new InvalidOperationException(
            "WEBHOOK_SECRET_ENCRYPTION_KEY is required in non-Development environments. " +
            $"Current environment: {app.Environment.EnvironmentName}. Generate a 32-byte " +
            "random base64 key (`openssl rand -base64 32`) and set the env var, or set " +
            "METABOT_ALLOW_PLAINTEXT_WEBHOOK_SECRETS=true for a transitional deploy.");
}
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
    // R18: public agentic-commerce discovery surface — no X-API-Key. /.well-known/*
    // and /llms.txt are meant to be crawler-reachable (matches the serious-vendor
    // cohort). Only the manifest endpoints are mapped under these prefixes; any
    // other path 404s. This is the fix for the gateway returning 401 on the
    // discovery paths.
    if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/llms.txt", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase) ||
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

async Task<IResult> HandleSearch(SearchRequest req, SearchService svc,
    AgentResourcesRepository resourcesRepo, CancellationToken ct)
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

    // v1.10 Phase 1 negative-filter validation. Caps mirror the trust-boundary
    // limits applied to query/useCase above — buyer-supplied lists can't blow
    // the per-request CPU budget through pathological exclusion sets.
    const int MaxExcludeListLen = 50;
    const int MaxExcludeStringLen = 200;
    foreach (var (list, name) in new[] {
        (req.ExcludeRequirements, "excludeRequirements"),
        (req.ExcludeAgents, "excludeAgents"),
        (req.ExcludeChains, "excludeChains"),
    })
    {
        if (list is null) continue;
        if (list.Length > MaxExcludeListLen)
            return Results.BadRequest(new { error = $"{name} accepts at most {MaxExcludeListLen} entries" });
        foreach (var s in list)
        {
            if (s is null) continue;
            if (s.Length > MaxExcludeStringLen)
                return Results.BadRequest(new { error = $"{name} entries must be {MaxExcludeStringLen} chars or fewer" });
        }
    }
    if (req.MaxPriceUsd is double mpu && mpu < 0)
        return Results.BadRequest(new { error = "maxPriceUsd must be >= 0" });

    // v1.10 Phase 2 sub-offering filter validation. Identifier-shape only
    // (letters / digits / underscore / hyphen) so we can bind the value
    // directly into the schema_facets lookup without escaping concerns, and
    // capped at 80 chars to bound the per-request cost. Blank => no filter.
    foreach (var (val, name) in new[] {
        (req.RequiresField, "requiresField"),
        (req.ProducesField, "producesField"),
    })
    {
        if (string.IsNullOrWhiteSpace(val)) continue;
        if (val!.Length > 80)
            return Results.BadRequest(new { error = $"{name} must be 80 chars or fewer" });
        if (!System.Text.RegularExpressions.Regex.IsMatch(val, "^[A-Za-z0-9_-]+$"))
            return Results.BadRequest(new {
                error = $"{name} must be an identifier (letters, digits, underscore, hyphen)"
            });
    }

    var filters = new SearchFilters(
        ExcludeRequirements: req.ExcludeRequirements,
        ExcludeAgents: req.ExcludeAgents,
        ExcludeChains: req.ExcludeChains,
        MaxPriceUsd: req.MaxPriceUsd,
        IncludeResources: req.IncludeResources ?? true,
        Expand: req.Expand ?? false,
        IncludeRisk: req.IncludeRisk ?? false,
        RequiresField: req.RequiresField,
        ProducesField: req.ProducesField);

    var (results, expansion) = await svc.SearchWithFiltersAsync(req.Query, limit, offset, minScore, priceMax,
        staleAfterDays, rerank, category, chainFilter, req.MinReputation, marketplace, filters, ct);

    // v1.10 Phase 1 (T6): unified search returns free Resources alongside
    // paid offerings when includeResources is true (default). The sub-limit
    // is derived from the caller's `limit` so Resources never crowd out
    // offerings — the offerings list is still the primary surface.
    IReadOnlyList<ResourceMatch> resources = Array.Empty<ResourceMatch>();
    if (filters.IncludeResources)
    {
        var resLimit = Math.Max(1, limit / 2);
        var raw = await resourcesRepo.SearchHybridAsync(req.Query, resLimit, marketplace, ct);
        resources = raw.Select(r => new ResourceMatch(
            Id: r.Id,
            AgentAddress: r.AgentAddress,
            AgentName: r.AgentName,
            Name: r.Name,
            Description: r.Description,
            Url: r.Url,
            MarketplaceVersion: r.MarketplaceVersion)).ToList();
    }

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

    return Results.Ok(new
    {
        query = req.Query,
        count = results.Count,
        results,
        resources,
        bestMatch,
        expansion = new
        {
            glossaryHits = expansion.GlossaryHits,
            synonymQueries = expansion.Synonyms,
            // v1.10 Phase 3: best-effort echo of the rewriter cost when
            // filters.Expand=true. Estimated per-call cost (Haiku 4.5,
            // ~250 in + ~150 out tokens). When the rewriter degrades
            // (daily cap breached / key missing / LLM error) the call is
            // skipped and the actual cost is $0 — this field is a hint
            // for callers, not a billing source of truth.
            rewriterCostUsd = filters.Expand ? 0.0008 : 0.0,
        },
        filtersApplied = new
        {
            excludeRequirements = filters.ExcludeRequirements ?? (IReadOnlyList<string>)Array.Empty<string>(),
            excludeAgents = filters.ExcludeAgents ?? (IReadOnlyList<string>)Array.Empty<string>(),
            excludeChains = filters.ExcludeChains ?? (IReadOnlyList<string>)Array.Empty<string>(),
            maxPriceUsd = filters.MaxPriceUsd,
            requiresField = filters.RequiresField,
            producesField = filters.ProducesField,
        }
    });
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
    string[]? chain, double? priceMaxUsdc, bool? includeSecurity, DigestService svc)
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

    var result = await svc.BuildAsync(window, marketplaceFilter, chainFilter, priceMaxUsdc, includeSecurity ?? true);
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
    => HandleDigest(days, marketplace, chain, priceMaxUsdc, includeSecurity: null, svc));
app.MapGet("/agent/{address}", (string address,
    OfferingRepository repo, ReputationService reputation,
    CrossPresenceBuilder crossPresence, PricePercentileCalculator pricePercentile,
    SearchService search)
    => HandleBrowseAgent(address, repo, reputation, crossPresence, pricePercentile, search));
app.MapGet("/categories", (CategoryService svc) => Results.Ok(new { categories = svc.Categories }));

// Public gateway — same logic, no X-API-Key, IP rate-limited.
app.MapPost("/v1/search", HandleSearch).RequireRateLimiting("public-search");
app.MapPost("/v1/composeStack", HandleCompose).RequireRateLimiting("public-compose");

// v1.10 Phase 3 T4: searchNarrative — runs a search via SearchWithFiltersAsync
// for the top-5, then NarrateAsync wraps the hits in a Claude-Haiku summary +
// per-result reasoning. The plugin layer will apply its untrusted-content
// envelope on top; the API just returns the structured payload.
app.MapPost("/v1/searchNarrative",
    async (SearchNarrativeRequest req, SearchService search, SearchNarrator narrator,
        CancellationToken ct) =>
{
    if (req.Search is null || string.IsNullOrWhiteSpace(req.Search.Query))
        return Results.BadRequest(new { error = "search.query is required" });
    if (req.Search.Query.Length > MaxQueryLen)
        return Results.BadRequest(new { error = $"search.query must be {MaxQueryLen} chars or fewer" });
    if (req.PreviousQueries is { Length: > 5 })
        return Results.BadRequest(new { error = "previousQueries accepts at most 5 entries" });
    if (req.PreviousQueries is not null)
    {
        foreach (var p in req.PreviousQueries)
            if (p != null && p.Length > 200)
                return Results.BadRequest(new { error = "previousQueries entries must be 200 chars or fewer" });
    }

    var marketplace = NormalizeMarketplace(req.Search.Marketplace);
    if (req.Search.Marketplace is not null && marketplace is null)
        return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });

    // Narration runs over the top-5 hits regardless of the request's
    // limit/offset — the summary is meant to be a tight wrap of the very
    // top of the ranking, not a paginated digest.
    var filters = new SearchFilters(IncludeResources: false, Expand: false, IncludeRisk: false);
    var (results, _) = await search.SearchWithFiltersAsync(
        req.Search.Query,
        limit: 5,
        offset: 0,
        minScore: 0.0,
        priceMaxUsdc: double.PositiveInfinity,
        staleAfterDays: null,
        rerank: true,
        categoryFilter: null,
        chainFilter: null,
        minReputation: null,
        marketplaceFilter: marketplace,
        filters: filters,
        ct: ct);

    var narrative = await narrator.NarrateAsync(req.Search.Query, results, req.PreviousQueries, ct);

    return Results.Ok(new
    {
        query = req.Search.Query,
        count = results.Count,
        results,
        summary = narrative.Summary,
        perResultReason = narrative.PerResultReason,
        citedOfferings = narrative.CitedOfferings,
        cacheHit = narrative.CacheHit,
        status = narrative.Status,
    });
}).RequireRateLimiting("public-compose");

// v1.10 Phase 3 T5: agentRiskCheck — defensive scam-risk score for a single
// agent on a single chain. 4 signals × 25 = 100 binned to low / medium /
// high / critical. Cached per (agent_address, chain_id) for 6h. NEVER
// throws on signal failures (each signal degrades to 0 with a "lookup
// failed" detail); only invalid agent addresses produce a 400.
app.MapPost("/v1/agentRiskCheck",
    async (AgentRiskCheckRequest req, AgentRiskScorer scorer, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AgentAddress))
        return Results.BadRequest(new { error = "agentAddress is required" });
    var chainId = req.ChainId ?? 8453;
    if (chainId != 1 && chainId != 8453)
        return Results.BadRequest(new { error = "chainId must be 1 (Ethereum) or 8453 (Base)" });
    try
    {
        var result = await scorer.ScoreAsync(req.AgentAddress, chainId, ct);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireRateLimiting("public-compose");

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

// ACPPurchaser Path A — purchase_quote ($0.02). Pre-flight cost + safety verdict.
// The sidecar resolves the live downstream price (C# can't speak ACP) and passes
// downstreamUsdc + fixedPrice; this endpoint adds the risk verdict + escrow total.
app.MapPost("/v1/buyer/purchase/quote",
    async (PurchaseQuoteRequest req, PurchaserService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.TargetAgent))
        return Results.BadRequest(new { error = "targetAgent required" });
    var q = await svc.QuoteAsync(req.TargetAgent, req.DownstreamUsdc, req.FixedPrice, ct);
    return Results.Ok(q);
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

// ===== v1.9 marketplaceGap ($0.30), v1.10.1 marketplace slice =====
//
// "Where should I build a new ACP bot?" — ranks every canonical marketplace
// category by an opportunity score derived from the existing saturationMap.
// No new data computed; the saturation snapshot is reused. The added value
// is the score formula + per-row recommendation_tag taxonomy.
//
// v1.10.1: accepts marketplace ∈ {v1, v2, both}. Default "v2" — flipped from
// pre-v1.10.1 "both" because V2 is the marketplace where new ACP bots
// actually deploy. Pass marketplace: "both" to recover the prior default.
app.MapPost("/v1/marketplace/gap",
    async (MarketplaceGapRequest req, MarketplaceGapService svc) =>
{
    var limit = req?.Limit ?? 5;
    var marketplace = (req?.Marketplace ?? "v2").Trim().ToLowerInvariant();
    if (marketplace is not ("v1" or "v2" or "both"))
        return Results.BadRequest(new
        {
            error   = "invalid_marketplace",
            allowed = new[] { "v1", "v2", "both" },
        });
    return Results.Ok(svc.Analyze(req?.Category, limit, marketplace));
}).RequireRateLimiting("public-compose");

// ===== v1.9 marketplacePulseSub ($4 / 30-day daily digest subscription) =====
//
// HMAC-signed webhook push. Buyer hires once, receives daily digest snapshots
// over the 30-day window. Tick scheduler is MarketplacePulseWorker (default
// OFF) — flip MarketplacePulse:Worker:Enabled=true after first hire and the
// docker compose up. /admin/pulse/tick-now lets the operator drive a one-shot
// tick for verification without waiting 24h.
//
// Sub-creation is /v1/internal/* — gated by INTERNAL_API_KEY so only the ACP
// sidecar (which knows the buyer paid escrow before invoking) can create a
// subscription row. The previous /v1/marketplace/pulse/subscribe route was
// publicly callable and would let anyone schedule a 30-day daily-tick worker
// against an arbitrary webhookUrl without payment proof (cost-abuse risk).
app.MapPost("/v1/internal/marketplace/pulse/subscribe",
    async (CreatePulseSubscriptionRequest req, MarketplacePulseService svc,
        CancellationToken ct) =>
{
    try { return Results.Ok(await svc.CreateAsync(req, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/admin/pulse/tick-now",
    async (MarketplacePulseWorker worker, CancellationToken ct) =>
{
    var delivered = await worker.TickOnceAsync(ct);
    return Results.Ok(new { ok = true, delivered });
});

// Operator-only on-demand SecurityBot scan of any marketplace bot. Gated by the
// inline X-API-Key middleware (/admin/* is NOT in the bypass list) — same gate as
// /admin/pulse/tick-now. Body: { agentAddress }. Scans fresh, persists to the same
// security_verdicts cache + security_scan_history log the worker writes (via the
// shared SecurityScanService), and returns the full verdict + per-finding detail.
// Free internal path (acp-shared, no escrow). lastError is never surfaced.
// operator-only behind X-API-Key; no rate-limit, matching /admin/pulse/tick-now.
app.MapPost("/admin/securityScan",
    async (ACP_Metabot.Api.Endpoints.AdminSecurityScanRequest req,
           SecurityScanService svc,
           SecurityVerdictRepository repo,
           SecurityScanHistoryRepository historyRepo,
           CancellationToken ct) =>
    {
        return await ACP_Metabot.Api.Endpoints.SecurityScanEndpoint.HandleAsync(
            req, repo, historyRepo, svc.ScanAndPersistAsync, ct);
    });

// ===== v1.8 Portfolio Risk Bot — 5 paid offerings =====
//
// All five share the public-compose rate-limit budget. The four one-shot
// endpoints (snapshot/deep-dive/compare/attestation) are synchronous fans
// out to peer bots; daily_risk_watch creates a subscription row and the
// RiskWatchWorker (default OFF) drives the daily pushes.
//
// Validation: every wallet is regex-checked here too even though the
// sidecar validators already do it — the C# tier must never trust the
// sidecar to do its own input checks. chain defaults to "base" downstream.

static bool IsAddr(string? s) =>
    !string.IsNullOrWhiteSpace(s) && System.Text.RegularExpressions.Regex.IsMatch(s, "^0x[0-9a-fA-F]{40}$");

// R12 Tier 1.3 — agent_smoke_check ($0.10)
// Static-analysis smoke test for any V2 ACP agent's offering. v1 ships a
// schema-validation verdict; v0.2 will invoke ACP_Tester via docker-ops to
// actually hire and time-the-deliverable.
app.MapPost("/v1/smoke/check",
    async (AgentSmokeCheckRequest req,
           OfferingRepository offRepo,
           AgentReputationCacheRepository repRepo,
           CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.TargetAgent) ||
        !System.Text.RegularExpressions.Regex.IsMatch(req.TargetAgent, "^0x[a-fA-F0-9]{40}$"))
    {
        return Results.BadRequest(new { error = "invalid_address", field = "targetAgent" });
    }
    var target = req.TargetAgent.ToLowerInvariant();
    var nowUtc = DateTime.UtcNow;

    // Look up the agent's offerings.
    var allOfferings = await offRepo.ListByAgentAsync(target);
    var liveOfferings = allOfferings.Where(o => !o.IsRemoved).ToList();
    if (liveOfferings.Count == 0)
    {
        return Results.Ok(new
        {
            verdict = "AGENT_NOT_FOUND",
            targetAgent = target,
            offeringName = req.OfferingName ?? string.Empty,
            offeringPriceUsdc = (double?)null,
            confidence = 0.0,
            findings = new[]
            {
                new { severity = "error", check = "agent_indexed", message = "Agent has no offerings in TheMetaBot's index. Either the agent is brand-new and the indexer hasn't seen it, or the address doesn't correspond to a V2 ACP agent." }
            },
            reputation = (object?)null,
            checkedAt = nowUtc.ToString("O"),
            v1Note = "v1 static analysis only. v0.2 will run a real ACP_Tester hire via docker-ops-sidecar."
        });
    }

    // Select the offering: explicit name if given, otherwise the cheapest.
    ACP_Metabot.Api.Models.Offering? offering = null;
    if (!string.IsNullOrWhiteSpace(req.OfferingName))
    {
        offering = liveOfferings.FirstOrDefault(o =>
            string.Equals(o.OfferingName, req.OfferingName, StringComparison.OrdinalIgnoreCase));
        if (offering is null)
        {
            return Results.Ok(new
            {
                verdict = "OFFERING_NOT_FOUND",
                targetAgent = target,
                offeringName = req.OfferingName,
                offeringPriceUsdc = (double?)null,
                confidence = 0.0,
                findings = new[]
                {
                    new { severity = "error", check = "offering_indexed", message = $"Agent has no offering named '{req.OfferingName}'. Available: {string.Join(", ", liveOfferings.Take(10).Select(o => o.OfferingName))}." }
                },
                reputation = (object?)null,
                checkedAt = nowUtc.ToString("O"),
                v1Note = "v1 static analysis."
            });
        }
    }
    else
    {
        offering = liveOfferings.OrderBy(o => o.PriceUsdc).First();
    }

    // Run structural checks against the offering.
    var findings = new List<object>();
    int errors = 0, warns = 0;

    // 1. Schema present + parseable.
    System.Text.Json.JsonElement? reqSchema = null;
    if (!string.IsNullOrWhiteSpace(offering.RequirementSchemaJson))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(offering.RequirementSchemaJson);
            reqSchema = doc.RootElement.Clone();
            findings.Add(new { severity = "info", check = "schema_present", message = "requirementSchema present and well-formed JSON." });
        }
        catch
        {
            findings.Add(new { severity = "error", check = "schema_parseable", message = "requirementSchema is present but malformed JSON. Buyer-orchestrators will fail to derive sample requirements." });
            errors++;
        }
    }
    else
    {
        findings.Add(new { severity = "warn", check = "schema_present", message = "requirementSchema missing. Buyer agents cannot pre-validate or auto-fill sample requirements." });
        warns++;
    }

    // 2. Required fields list (if schema is present).
    if (reqSchema is not null)
    {
        try
        {
            if (reqSchema.Value.TryGetProperty("required", out var reqArr) && reqArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var reqList = reqArr.EnumerateArray().Select(e => e.GetString() ?? "?").ToList();
                findings.Add(new { severity = "info", check = "required_fields", message = $"Required fields ({reqList.Count}): {string.Join(", ", reqList)}." });
            }
            else
            {
                findings.Add(new { severity = "info", check = "required_fields", message = "No required-fields list (all params optional)." });
            }
        }
        catch { /* swallow */ }
    }

    // 3. Price floor — portfolio convention is $0.01 minimum.
    if (offering.PriceUsdc < 0.01)
    {
        findings.Add(new { severity = "error", check = "price_floor", message = $"priceUsdc = {offering.PriceUsdc:F4} (below portfolio $0.01 floor — sub-floor offerings are structurally uneconomic)." });
        errors++;
    }
    else if (offering.PriceUsdc < 0.02)
    {
        findings.Add(new { severity = "warn", check = "price_floor", message = $"priceUsdc = {offering.PriceUsdc:F4} (at $0.01 floor — recheck whether the hire-lifecycle overhead justifies this price)." });
        warns++;
    }
    else
    {
        findings.Add(new { severity = "info", check = "price_floor", message = $"priceUsdc = {offering.PriceUsdc:F4} (above portfolio floor)." });
    }

    // 4. Marketplace version.
    if (string.Equals(offering.MarketplaceVersion, "v2", StringComparison.OrdinalIgnoreCase))
    {
        findings.Add(new { severity = "info", check = "marketplace_v2", message = "Offering registered on V2 marketplace." });
    }
    else
    {
        findings.Add(new { severity = "warn", check = "marketplace_v2", message = $"Offering on {offering.MarketplaceVersion} marketplace — V2 buyer-orchestrators may skip." });
        warns++;
    }

    // 5. Description quality.
    if (string.IsNullOrWhiteSpace(offering.Description) || offering.Description.Length < 80)
    {
        findings.Add(new { severity = "warn", check = "description_length", message = $"Description is short ({offering.Description?.Length ?? 0} chars) — Hermes/Butler/Suede rank by description quality." });
        warns++;
    }
    else
    {
        findings.Add(new { severity = "info", check = "description_length", message = $"Description length OK ({offering.Description.Length} chars)." });
    }

    // 6. Sample requirement check (if buyer supplied).
    if (req.SampleRequirement is not null && reqSchema is not null)
    {
        try
        {
            if (reqSchema.Value.TryGetProperty("required", out var requiredArr) && requiredArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var missing = new List<string>();
                foreach (var rf in requiredArr.EnumerateArray())
                {
                    var fname = rf.GetString();
                    if (fname is null) continue;
                    if (!req.SampleRequirement.ContainsKey(fname)) missing.Add(fname);
                }
                if (missing.Count > 0)
                {
                    findings.Add(new { severity = "error", check = "sample_required_fields", message = $"Buyer's sampleRequirement is missing required fields: {string.Join(", ", missing)}." });
                    errors++;
                }
                else
                {
                    findings.Add(new { severity = "info", check = "sample_required_fields", message = "Buyer's sampleRequirement covers all required fields." });
                }
            }
        }
        catch { /* swallow */ }
    }

    // 7. Reputation pull (cached).
    object? reputation = null;
    try
    {
        var rep = await repRepo.GetAsync(target, nowUtc);
        if (rep is not null)
        {
            reputation = new
            {
                score = rep.AgentScore,
                source = rep.Source,
                computedAt = rep.ComputedAt.ToString("O")
            };
            findings.Add(new { severity = "info", check = "reputation_cached", message = $"Reputation cache score={rep.AgentScore}, computedAt={rep.ComputedAt:O}." });
        }
        else
        {
            findings.Add(new { severity = "warn", check = "reputation_cached", message = "No cached reputation. Either too new or the warmer hasn't reached this agent." });
            warns++;
        }
    }
    catch { /* graceful degrade */ }

    // Verdict synthesis.
    string verdict;
    double confidence;
    if (errors > 0)       { verdict = "FAIL"; confidence = 0.7; }
    else if (warns >= 3)  { verdict = "WARN"; confidence = 0.6; }
    else if (warns > 0)   { verdict = "WARN"; confidence = 0.7; }
    else                  { verdict = "PASS"; confidence = 0.82; }

    return Results.Ok(new
    {
        verdict,
        targetAgent = target,
        offeringName = offering.OfferingName,
        offeringPriceUsdc = (double?)offering.PriceUsdc,
        confidence,
        findings,
        reputation,
        checkedAt = nowUtc.ToString("O"),
        v1Note = "v1 static analysis. v0.2 wires real ACP_Tester hire via docker-ops-sidecar for end-to-end PASS/FAIL with latency + actual deliverable shape match."
    });
}).RequireRateLimiting("public-compose");

app.MapPost("/v1/risk/snapshot",
    async (RiskSnapshotRequest req, RiskOrchestrationService svc, CancellationToken ct) =>
{
    if (!IsAddr(req.Wallet)) return Results.BadRequest(new { error = "invalid_address", field = "wallet" });
    if (req.Chain is not null && req.Chain is not ("base" or "ethereum"))
        return Results.BadRequest(new { error = "chain must be 'base' or 'ethereum'" });
    return Results.Ok(await svc.SnapshotAsync(req.Wallet!, req.Chain, ct));
}).RequireRateLimiting("public-compose");

// R12 Tier PT-P0 — internal cross-bot risk bundle. NOT a marketplace
// offering, NOT in resources.ts. X-API-Key gated via the /v1/internal
// path-prefix in the middleware above. Used by ACP_PrivateTrader and any
// other cross-bot consumer that wants a one-shot reputation + risk + arena
// participation pack without three round-trips. Fan-out is fire-and-forget
// on the failure axis — whichever sub-call faults is returned as null and
// the bundle still serves.
app.MapPost("/v1/internal/agentRiskBundle", async (
    AgentRiskBundleRequest req,
    RiskOrchestrationService riskSvc,
    AgentReputationCacheRepository repRepo,
    AgentArenaParticipationRepository arenaRepo,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Wallet) ||
        !System.Text.RegularExpressions.Regex.IsMatch(req.Wallet, "^0x[a-fA-F0-9]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", field = "wallet" });
    if (req.Chain is not null && req.Chain is not ("base" or "ethereum"))
        return Results.BadRequest(new { error = "chain must be 'base' or 'ethereum'" });

    var wallet = req.Wallet.ToLowerInvariant();
    var chain  = req.Chain ?? "base";
    var nowUtc = DateTime.UtcNow;

    // Fan out: reputation cache read + risk snapshot + arena participation in parallel.
    // - repRepo.GetAsync returns null for unwarmed agents (≤24h TTL) — that's expected.
    // - riskSvc.SnapshotAsync runs the full RiskSynthesisService pipeline; can be slow
    //   (peer fan-out) but errors are surfaced as a degraded payload rather than throwing.
    // - arenaRepo.GetByAddressAsync returns null for non-Arena-participants.
    var repTask   = repRepo.GetAsync(wallet, nowUtc);
    var riskTask  = riskSvc.SnapshotAsync(wallet, chain, ct);
    var arenaTask = arenaRepo.GetByAddressAsync(wallet);

    try
    {
        await Task.WhenAll(repTask, riskTask, arenaTask);
    }
    catch
    {
        // Per-task status is checked below; bundle still serves with whatever succeeded.
    }

    return Results.Ok(new
    {
        wallet,
        chain,
        reputation = repTask.IsCompletedSuccessfully && repTask.Result is not null
            ? new
            {
                score      = repTask.Result.AgentScore,
                source     = repTask.Result.Source,
                computedAt = repTask.Result.ComputedAt.ToString("O")
            }
            : (object?)null,
        risk      = riskTask.IsCompletedSuccessfully  ? riskTask.Result  : null,
        arena     = arenaTask.IsCompletedSuccessfully ? arenaTask.Result : null,
        bundledAt = nowUtc.ToString("O")
    });
});

app.MapPost("/v1/risk/deep-dive",
    async (RiskSnapshotRequest req, RiskOrchestrationService svc, CancellationToken ct) =>
{
    if (!IsAddr(req.Wallet)) return Results.BadRequest(new { error = "invalid_address", field = "wallet" });
    if (req.Chain is not null && req.Chain is not ("base" or "ethereum"))
        return Results.BadRequest(new { error = "chain must be 'base' or 'ethereum'" });
    return Results.Ok(await svc.DeepDiveAsync(req.Wallet!, req.Chain, ct));
}).RequireRateLimiting("public-compose");

app.MapPost("/v1/risk/compare",
    async (RiskCompareRequest req, RiskOrchestrationService svc, CancellationToken ct) =>
{
    if (req.Wallets is null || req.Wallets.Length < 2 || req.Wallets.Length > 5)
        return Results.BadRequest(new { error = "wallets must contain 2 to 5 addresses" });
    foreach (var w in req.Wallets)
        if (!IsAddr(w)) return Results.BadRequest(new { error = "invalid_address", value = w });
    if (req.Chain is not null && req.Chain is not ("base" or "ethereum"))
        return Results.BadRequest(new { error = "chain must be 'base' or 'ethereum'" });
    return Results.Ok(await svc.CompareAsync(req.Wallets, req.Chain, ct));
}).RequireRateLimiting("public-compose");

app.MapPost("/v1/risk/attestation",
    async (RiskSnapshotRequest req, RiskOrchestrationService svc, CancellationToken ct) =>
{
    if (!IsAddr(req.Wallet)) return Results.BadRequest(new { error = "invalid_address", field = "wallet" });
    if (req.Chain is not null && req.Chain is not ("base" or "ethereum"))
        return Results.BadRequest(new { error = "chain must be 'base' or 'ethereum'" });
    return Results.Ok(await svc.AttestationAsync(req.Wallet!, req.Chain, ct));
}).RequireRateLimiting("public-compose");

// v1.0 riskAttestPro Task 8 — POST /v1/risk/attest-pro ($10 premium tier).
// Cross-bot 7-lane orchestrator (HF + approvals + MEV + reputation + Arena +
// Witness + trajectory). 1h wallet response cache via the
// risk_attest_pro_cache table; bypass with `fresh:true`. Floor breach (<4
// of 7 lanes fresh) returns 502 INSUFFICIENT_SIGNALS. Live EAS-publish
// wiring lands in v1.0.1; v1.0 ships with the schema bootstrap worker
// (Task 7) stubbed and the attestation block populated from the service.
app.MapPost("/v1/risk/attest-pro",
    async (ACP_Metabot.Api.Endpoints.RiskAttestProRequest req,
           Db db,
           RiskAttestProService svc,
           CancellationToken ct) =>
    {
        return await ACP_Metabot.Api.Endpoints.RiskAttestProEndpoint.HandleAsync(
            req, db, svc.GenerateAsync, ct);
    })
    .RequireRateLimiting("public-compose");

// Sub-creation is /v1/internal/* — gated by INTERNAL_API_KEY so only the ACP
// sidecar (which knows the buyer paid escrow before invoking) can create a
// risk-watch subscription. The previous /v1/risk/watch route was publicly
// callable and would let anyone schedule a 30-day daily-tick worker against
// an arbitrary wallet+webhookUrl without payment proof.

// ACPPurchaser Path A — internal precheck/settle (sidecar-only, X-API-Key gated).
// precheck = fixed/maxFunds/risk gate + atomic per-buyer/day cap reservation + audit.
// settle = audit state transition + refund-the-reservation on REJECTED.
app.MapPost("/v1/internal/buyer/purchase/precheck",
    async (PurchasePrecheckRequest req, PurchaserService svc, CancellationToken ct) =>
{
    var r = await svc.PrecheckAsync(req.OuterJobId, req.BuyerKey, req.TargetAgent, req.TargetOffering,
        req.DownstreamUsdc, req.MaxFundsUsdc, ct);
    return Results.Ok(r);
});

app.MapPost("/v1/internal/buyer/purchase/settle",
    async (PurchaseSettleRequest req, PurchaserService svc, CancellationToken ct) =>
{
    await svc.SettleAsync(req.OuterJobId, req.BuyerKey, req.State, req.InnerJobId, req.Reason, req.DownstreamUsdc, ct);
    return Results.Ok(new { ok = true });
});

app.MapPost("/v1/internal/risk/watch",
    async (RiskWatchRequest req, RiskOrchestrationService svc, CancellationToken ct) =>
{
    if (req.JobId <= 0) return Results.BadRequest(new { error = "jobId required" });
    if (!IsAddr(req.BuyerAddress))
        return Results.BadRequest(new { error = "invalid_address", field = "buyerAddress" });
    if (!IsAddr(req.Wallet))
        return Results.BadRequest(new { error = "invalid_address", field = "wallet" });
    if (req.Chain is not null && req.Chain is not ("base" or "ethereum"))
        return Results.BadRequest(new { error = "chain must be 'base' or 'ethereum'" });

    // Full SSRF guard — was previously a `StartsWith("https://")` prefix-only
    // check, which let an attacker register `https://attacker/redirect` that
    // 302s into 169.254.169.254 / loopback / RFC1918. RiskWatchWorker now
    // delivers through SafeWebhookHttpClient which re-validates each hop,
    // but registration-time fail-fast is still the right place to refuse
    // bad URLs before persisting state.
    var urlCheck = await WebhookUrlValidator.ValidateAsync(req.WebhookUrl ?? "", ct);
    if (!urlCheck.Ok)
        return Results.BadRequest(new { error = urlCheck.Reason });

    return Results.Ok(await svc.CreateWatchAsync(req.JobId, req.BuyerAddress!, req.Wallet!, req.WebhookUrl!, req.Chain));
});
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

// Public, summary-only read of the append-only security_scan_history table.
// Mirrors /v1/agentReputationHistory (public via the /v1/* X-API-Key bypass,
// rate-limited under public-reputation). Backs acp-find's acp_agent_security_history.
// SUMMARY ONLY — raw findings[] + last_error stay server-side (P9/P10/P30/P63);
// operators get full per-finding detail from the GATED POST /admin/securityScan.
app.MapGet("/v1/securityScanHistory",
    ([FromQuery] string agent, [FromQuery] int? limit,
        SecurityScanHistoryRepository histRepo, CancellationToken ct)
    => ACP_Metabot.Api.Endpoints.SecurityScanHistoryEndpoint.HandleAsync(agent, limit, histRepo, ct))
    .RequireRateLimiting("public-reputation");
app.MapGet("/v1/digest", (int? days, string? marketplace, string[]? chain, double? priceMaxUsdc, bool? includeSecurity, DigestService svc)
    => HandleDigest(days, marketplace, chain, priceMaxUsdc, includeSecurity, svc))
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

// ===== v1.8 Portfolio Risk Bot Resources =====
//
// riskDataSourceHealth probes the four cross-bot data sources risk_snapshot
// depends on. It calls each peer's cheapest free Resource (or /health) to
// produce a status board so buyer agents can pre-check freshness before
// hiring the paid offering.
app.MapGet("/v1/resources/riskDataSourceHealth",
    async (IRiskPeerClients peers, AgentReputationCacheRepository repCache, CancellationToken ct) =>
    {
        // Probe each peer with a known-cheap call. Use the well-known portfolio
        // dev address 0x6939...4633 — any 0x-shaped address works for the
        // probes since we only care about whether the peer responded, not the
        // data itself.
        const string probeAddress = "0x0000000000000000000000000000000000000001";
        var hfTask  = peers.GetHealthFactorAsync(probeAddress, "base", ct);
        var aprTask = peers.GetApprovalsQuoteAsync(probeAddress, "base", ct);
        var mevTask = peers.GetMevScoreAsync(probeAddress, ct);
        await Task.WhenAll(hfTask, aprTask, mevTask);

        // Internal reputation cache freshness: latest computed_at across the cache.
        // We don't probe the row for the probe address; reputation as a service is
        // a local SQLite table — "fresh" if any row was computed within 48h.
        DateTime? lastFreshAt = null;
        try
        {
            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                builder.Configuration.GetConnectionString("Sqlite"));
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(computed_at) FROM agent_reputation_cache;";
            var raw = await cmd.ExecuteScalarAsync(ct);
            if (raw is string s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                lastFreshAt = dt;
        }
        catch { /* don't fail the Resource for a probe-side error */ }

        string Status(System.Text.Json.JsonDocument? doc) => doc is null ? "unavailable" : "fresh";
        string RepStatus() => lastFreshAt is null
            ? "unavailable"
            : (DateTime.UtcNow - lastFreshAt.Value).TotalHours <= 48 ? "fresh" : "stale";

        var lg  = Status(hfTask.Result);
        var rv  = Status(aprTask.Result);
        var mev = Status(mevTask.Result);
        var rep = RepStatus();

        var anyDown = new[] { lg, rv, mev, rep }.Any(s => s == "unavailable");
        return Results.Ok(new
        {
            description = "Cross-bot data source health for risk_snapshot. Each component reports fresh/stale/unavailable. Pre-check before paying when sub-fresh data would compromise the use case.",
            verdict = anyDown ? "DEGRADED" : "HEALTHY",
            liquidguard = new { status = lg, source = "LiquidGuard" },
            revokebot   = new { status = rv, source = "RevokeBot" },
            mevprotect  = new { status = mev, source = "MEVProtect" },
            reputation  = new {
                status = rep,
                source = "TheMetaBot",
                lastFreshAt = lastFreshAt?.ToString("O")
            },
            time = DateTime.UtcNow.ToString("O"),
        });
    }).RequireRateLimiting("public-resources");

// riskScoreRubric — static methodology block. Lets buyer agents pre-validate
// the score recipe before paying for a risk_snapshot.
app.MapGet("/v1/resources/riskScoreRubric", () =>
{
    return Results.Ok(new
    {
        description = "Methodology for the risk_snapshot 0-100 score. Composite weighted blend across four components; unavailable peers are dropped from the blend and the remaining weights renormalised.",
        weights = new
        {
            healthFactor = RiskSynthesisService.WeightHealthFactor,
            approvals    = RiskSynthesisService.WeightApprovals,
            mevExposure  = RiskSynthesisService.WeightMevExposure,
            reputation   = RiskSynthesisService.WeightReputation,
        },
        grades = new[]
        {
            new { grade = "A", min = 85, label = "Strong" },
            new { grade = "B", min = 70, label = "Healthy" },
            new { grade = "C", min = 55, label = "Mixed" },
            new { grade = "D", min = 40, label = "Elevated risk" },
            new { grade = "F", min = 0,  label = "High risk" },
        },
        components = new
        {
            healthFactor = new {
                source = "LiquidGuard",
                buckets = new[]
                {
                    new { hfAtLeast = 3.0, score = 100 },
                    new { hfAtLeast = 2.0, score = 90 },
                    new { hfAtLeast = 1.5, score = 75 },
                    new { hfAtLeast = 1.25, score = 55 },
                    new { hfAtLeast = 1.1, score = 35 },
                    new { hfAtLeast = 0.0, score = 10 },
                },
                noPositionsScore = 80
            },
            approvals = new
            {
                source = "RevokeBot",
                buckets = new[]
                {
                    new { highRiskCount = 0, score = 100 },
                    new { highRiskCount = 1, score = 70 },
                    new { highRiskCount = 2, score = 55 },
                    new { highRiskCount = 3, score = 40 },
                    new { highRiskCount = 4, score = 25 },
                    new { highRiskCount = 5, score = 10 },
                }
            },
            mevExposure = new
            {
                source = "MEVProtect",
                note   = "Peer returns a 0-100 mevScore directly (100 = lowest exposure). Passed through with bounds-check."
            },
            reputation = new
            {
                source = "TheMetaBot",
                neutralBaselineScore = 50,
                note = "When the wallet is also a registered ACP agent, uses the cached agentScore. Otherwise 50 (neutral)."
            }
        },
        fallbackPolicy = "When a peer is unavailable, that component is marked 'unavailable' and its weight is renormalised across the remaining components. Recorded in deliverable.fallbacks[].",
        time = DateTime.UtcNow.ToString("O")
    });
}).RequireRateLimiting("public-resources");

// ─── R12 Tier 1.1 — witnessedCatalogue ──────────────────────────────────────
// Pointer to TheWitnessBot's signed manifest of THIS agent's offering catalogue.
// Free, public, parameterless. Activates the cross-portfolio trust moat.
const string MetabotAgentAddress   = "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c";
const string WitnessBotAddress     = "0xc834e81ebe0921fdf9458ac422861df441a6caf9";
const string WitnessGatewayBase    = "https://api.acp-metabot.dev/witnessbot/v1/resources/manifestByAgent";

app.MapGet("/v1/resources/witnessedCatalogue", () =>
{
    return Results.Ok(new
    {
        agentAddress = MetabotAgentAddress,
        agentName = "TheMetaBot",
        witnessAgent = WitnessBotAddress,
        witnessGateway = $"{WitnessGatewayBase}?agentAddress={MetabotAgentAddress}",
        signedManifestUid = (string?)null,
        signedAt = (string?)null,
        verificationGuide = "Call witnessGateway. If 200 OK, recover signer with " +
            "ethers.utils.verifyMessage(catalogueHash, signatureHex) and check " +
            "the recovered address matches TheWitnessBot's witnessKeyDirectory. " +
            "If 404, this catalogue has not been witnessed yet (v1 state).",
        snapshotRefs = new
        {
            offerings = "https://app.virtuals.io/acp/agents/" + MetabotAgentAddress,
            resources = "https://api.acp-metabot.dev/v1/resources"
        },
        notes = "v1 returns null signedManifestUid until the first manifest_sign " +
            "hire is made against TheWitnessBot. v1.1 will populate via " +
            "cross-bot client.",
        time = DateTime.UtcNow.ToString("O")
    });
}).RequireRateLimiting("public-resources");

// ─── R12 Tier 1.2 — portfolioRollup ─────────────────────────────────────────
// Single endpoint listing every Oliver-portfolio bot with offering counts,
// reputation, witnessedCatalogue pointers, and cross-bot edges. Designed for
// Butler Pro Mode + buyer-orchestrator pre-flight discovery. Free, public,
// parameterless, 5-min in-process cache via PortfolioRollupService.
app.MapGet("/v1/resources/portfolioRollup", async (
    PortfolioRollupService svc, CancellationToken ct) =>
{
    var rollup = await svc.GetRollupAsync(ct);
    return Results.Ok(rollup);
}).RequireRateLimiting("public-resources");

// ─── R18 — agentic-commerce discovery manifest ──────────────────────────────
// Public, no X-API-Key (whitelisted above). Read-only projection of the live
// portfolioRollup so it never goes stale. Matches the Strumly/Johnny-Suede
// discovery pattern; closes the gateway-401 gap on the discovery paths.
// NOTE: x402.json is intentionally NOT published — ButlerBridge still boots
// stub-x402; payment_rails advertises "virtuals-acp" only until that cutover.
app.MapGet("/.well-known/agentic-commerce.json", async (
    DiscoveryManifestService svc, CancellationToken ct) =>
{
    return Results.Ok(await svc.BuildAgenticCommerceAsync(ct));
}).RequireRateLimiting("public-resources");

app.MapGet("/llms.txt", async (
    DiscoveryManifestService svc, CancellationToken ct) =>
{
    var body = await svc.BuildLlmsTxtAsync(ct);
    return Results.Text(body, "text/plain; charset=utf-8");
}).RequireRateLimiting("public-resources");

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
    int? Offset = null,
    // v1.10 Phase 1 negative filters + future-flags
    string[]? ExcludeRequirements = null,
    string[]? ExcludeAgents = null,
    string[]? ExcludeChains = null,
    double? MaxPriceUsd = null,
    bool? IncludeResources = null,
    bool? Expand = null,
    bool? IncludeRisk = null,
    // v1.10 Phase 2 sub-offering filters
    string? RequiresField = null,
    string? ProducesField = null);
// v1.10 Phase 3 T4: /v1/searchNarrative wraps a SearchRequest + optional
// previousQueries (≤5 entries × 200 chars). The endpoint forces top-5 +
// no resources/expand/risk on the underlying search so the narrator wraps
// a stable, minimal payload.
public record SearchNarrativeRequest(SearchRequest Search, string[]? PreviousQueries);
// v1.10 Phase 3 T5: /v1/agentRiskCheck request. ChainId is validated against
// the v0.11.0 input-validator whitelist (1 = Ethereum, 8453 = Base) at the
// endpoint, defaulting to Base when unset.
public record AgentRiskCheckRequest(string AgentAddress, int? ChainId);
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
// ACPPurchaser Path A request DTOs.
public record PurchaseQuoteRequest(string TargetAgent, decimal DownstreamUsdc, bool FixedPrice);
public record PurchasePrecheckRequest(string OuterJobId, string BuyerKey, string TargetAgent, string TargetOffering, decimal DownstreamUsdc, decimal MaxFundsUsdc);
public record PurchaseSettleRequest(string OuterJobId, string BuyerKey, string State, string? InnerJobId, string? Reason, decimal DownstreamUsdc);
public record SellerCoachingRequest(string Agent);
public record V1Tov2MigrationRequest(string Agent);

// v1.8 Portfolio Risk Bot DTOs
public record RiskSnapshotRequest(string? Wallet, string? Chain);
public record RiskCompareRequest(string[] Wallets, string? Chain);
public record RiskWatchRequest(long JobId, string? BuyerAddress, string? Wallet,
    string WebhookUrl, string? Chain);

// R12 Tier PT-P0 — internal cross-bot risk-bundle request. Consumed by
// /v1/internal/agentRiskBundle; not part of the marketplace surface.
public record AgentRiskBundleRequest(string Wallet, string? Chain);

// R12 Tier 1.3 — agent_smoke_check DTO
public record AgentSmokeCheckRequest(
    string TargetAgent,
    string? OfferingName,
    Dictionary<string, object?>? SampleRequirement);

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
