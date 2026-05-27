using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// R12 Tier 1.2 — Portfolio rollup service.
//
// Backs GET /v1/resources/portfolioRollup. Returns a portfolio-wide snapshot
// in a single call so buyer orchestrators (Butler Pro, Hermes, Suede, Orion,
// Bay Buddy) can ladder multi-bot stacks without probing 13 agents.
//
// Cache: 5-minute TTL, in-process. Buyer orchestrators are expected to fetch
// every few minutes; per-fetch DB hits (live reputation + offering counts)
// are cheap but a tight loop would still beat the SQLite write path.
//
// Sibling liveness: the v1 envelope reports `synthetic = null` and
// `lastSeenAt = null` per bot. v1.1 would optionally HEAD each bot's
// witnessedCatalogue endpoint via acp-shared to set these — gated behind
// `PORTFOLIO_ROLLUP_PROBE_SIBLINGS=true` so local `dotnet run` smoke does
// NOT depend on every sibling being up. Off by default, plumbed but unused.
//
// Hard contract with acp-v2/src/resources.ts:
//   - `params: { type: "object", properties: {} }` — parameterless
//   - Path: /v1/resources/portfolioRollup
//   - Free (whitelisted by X-API-Key middleware)

public sealed class PortfolioRollupService
{
    private readonly AgentReputationCacheRepository _repRepo;
    private readonly OfferingRepository _offRepo;
    private readonly AgentResourcesRepository _resRepo;
    private readonly bool _probeSiblings;
    private readonly ILogger<PortfolioRollupService> _log;

    // Cache fields. Object is the rendered envelope (typed by the helper
    // record below); set under lock so a concurrent expiry doesn't race
    // multiple rebuilds. Reads outside the lock are safe — worst case is
    // one extra rebuild while two callers race past the staleness check.
    private readonly object _lock = new();
    private object? _cached;
    private DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan _ttl = TimeSpan.FromMinutes(5);

    public PortfolioRollupService(
        AgentReputationCacheRepository repRepo,
        OfferingRepository offRepo,
        AgentResourcesRepository resRepo,
        IConfiguration config,
        ILogger<PortfolioRollupService> log)
    {
        _repRepo = repRepo;
        _offRepo = offRepo;
        _resRepo = resRepo;
        _log = log;
        // Default OFF — never hits siblings during local dev / unit tests
        _probeSiblings = config.GetValue<bool?>("PortfolioRollup:ProbeSiblings") ?? false;
    }

    public async Task<object> GetRollupAsync(CancellationToken ct = default)
    {
        // Try cache outside the lock (cheap).
        var snap = _cached;
        if (snap is not null && (DateTime.UtcNow - _cachedAt) < _ttl)
            return snap;

        var rendered = await BuildRollupAsync(ct);

        lock (_lock)
        {
            _cached = rendered;
            _cachedAt = DateTime.UtcNow;
        }
        return rendered;
    }

    private async Task<object> BuildRollupAsync(CancellationToken ct)
    {
        var bots = PortfolioBots.All;
        var now = DateTime.UtcNow;
        var rendered = new List<object>(bots.Length);

        int totalPaidOfferings = 0;
        int totalResources = 0;
        int totalSubscriptions = 0;

        foreach (var bot in bots)
        {
            // Pull live reputation snapshot — graceful degrade on any error.
            object? reputation = null;
            try
            {
                var rep = await _repRepo.GetAsync(bot.AgentAddress, now);
                if (rep is not null)
                {
                    reputation = new
                    {
                        score = rep.AgentScore,
                        computedAt = rep.ComputedAt.ToString("O"),
                        source = rep.Source
                    };
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "portfolioRollup reputation lookup failed for {Bot}", bot.Slug);
            }

            // Pull live offering count from the index — falls back to the
            // hardcoded catalogue count when the index hasn't seen the bot
            // yet (e.g. fresh deploy before the first indexer pass).
            int liveOfferingCount = bot.OfferingCount;
            try
            {
                var offs = await _offRepo.ListByAgentAsync(bot.AgentAddress);
                var active = offs.Count(o => !o.IsRemoved);
                if (active > 0) liveOfferingCount = active;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "portfolioRollup offering lookup failed for {Bot}", bot.Slug);
            }

            // Pull live resource count from the indexed agent_resources rows.
            int liveResourceCount = bot.ResourceCount;
            DateTime? lastSeenAt = null;
            try
            {
                var ress = await _resRepo.ListByAgentAsync(bot.AgentAddress, ct);
                if (ress.Count > 0)
                {
                    liveResourceCount = ress.Count;
                    lastSeenAt = ress.Max(r => r.LastSeenAt);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "portfolioRollup resource lookup failed for {Bot}", bot.Slug);
            }

            // Roll up totals using LIVE counts where available — hardcoded
            // fallback ensures the rollup is sane even when the indexer is
            // still warming on a fresh boot. Subscription tier count stays
            // hardcoded; the upstream marketplace doesn't expose pricing
            // type and our DB normalises everything to "fixed".
            totalPaidOfferings += liveOfferingCount;
            totalResources += liveResourceCount;
            totalSubscriptions += bot.SubscriptionTierCount;

            // Per-bot synthetic flag is null in v1 — sibling-probing is the
            // v1.1 path (PORTFOLIO_ROLLUP_PROBE_SIBLINGS toggle). When the
            // probe lands it sets synthetic=false on reachable bots, true
            // on unreachable ones, plus a v1Note describing the failure.
            bool? synthetic = null;
            string? v1Note = null;
            if (_probeSiblings)
            {
                // Reserved for v1.1: HEAD against
                //   http://<slug>-api:5000/v1/resources/witnessedCatalogue
                // via acp-shared. Stay null until that lands.
                v1Note = "sibling-probing wired but not yet implemented";
            }

            string resourcesBase = $"https://api.acp-metabot.dev/{bot.Slug}/v1/resources";

            rendered.Add(new
            {
                slug = bot.Slug,
                displayName = bot.DisplayName,
                agentAddress = bot.AgentAddress,
                agentId = bot.AgentId,
                chains = bot.Chains,
                category = bot.Category,
                marketplaceUrl = $"https://app.virtuals.io/acp/agents/{bot.AgentAddress}",
                website = $"https://acp-metabot.dev/portfolio/{bot.Slug}",
                resourcesBaseUrl = resourcesBase,
                witnessedCatalogueUrl = $"{resourcesBase}/witnessedCatalogue",
                offeringCount = liveOfferingCount,
                resourceCount = liveResourceCount,
                subscriptionTierCount = bot.SubscriptionTierCount,
                reputation = reputation,
                synthetic = synthetic,
                lastSeenAt = lastSeenAt?.ToString("O"),
                v1Note = v1Note
            });
        }

        var edges = PortfolioBots.Edges
            .Select(e => (object)new
            {
                producer = e.Producer,
                consumer = e.Consumer,
                via = e.Via,
                verified = e.Verified
            })
            .ToArray();

        return new
        {
            asOfUtc = DateTime.UtcNow.ToString("O"),
            cacheTtlSeconds = (int)_ttl.TotalSeconds,
            portfolio = new
            {
                @operator = "@ACPMetaPortfolio",
                website = "https://acp-metabot.dev",
                totalBots = bots.Length,
                totalPaidOfferings,
                totalFreeResources = totalResources,
                totalSubscriptions
            },
            bots = rendered,
            crossBotEdges = edges
        };
    }
}
