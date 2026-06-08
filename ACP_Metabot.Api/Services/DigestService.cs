using System.Globalization;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

// Computes the daily / N-day digest: newly-launched offerings + offerings
// whose hire count grew most over the window. Pure read path — relies on
// the snapshot table written by MarketplaceIndexerService.
//
// v1.7 additions: newAgents / churnRate / cohortSurvival / saturationMap /
//   windowStart / partial flag. Hourly per-(filter-set) in-memory cache
//   prevents thundering-herd on cold filter combinations.
public class DigestService
{
    private readonly OfferingRepository _repo;
    private readonly ReputationService _reputation;
    private readonly SaturationCalculator _saturation;
    private readonly AgentResourcesRepository? _resourcesRepo;
    private readonly SecurityVerdictRepository? _securityRepo;

    private const int MaxNewOfferings = 25;
    private const int MaxGainers = 25;
    // v1.7.4: cap on new-Resources block size in the today/digest payload.
    private const int MaxNewResources = 25;

    // Sub-task E: hourly per-filter-set cache
    private readonly Dictionary<string, (DateTime Bucket, DigestResult Result)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static string CacheKey(int days, string? mv, HashSet<string>? chain, double? price, bool sec)
    {
        var c = chain is null ? "" : string.Join(",", chain.OrderBy(s => s));
        return $"d={days}|m={mv ?? ""}|c={c}|p={price?.ToString() ?? ""}|s={(sec ? 1 : 0)}";
    }

    public DigestService(OfferingRepository repo, ReputationService reputation,
        SaturationCalculator saturation, AgentResourcesRepository? resourcesRepo = null,
        SecurityVerdictRepository? securityRepo = null)
    {
        _repo = repo;
        _reputation = reputation;
        _saturation = saturation;
        _resourcesRepo = resourcesRepo;
        _securityRepo = securityRepo;
    }

    // Backward-compat overload — delegate to the full version.
    public Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter = null)
        => BuildAsync(windowDays, marketplaceFilter, chainFilter: null, priceMaxUsdc: null, includeSecurity: true);

    public Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter,
        HashSet<string>? chainFilter, double? priceMaxUsdc)
        => BuildAsync(windowDays, marketplaceFilter, chainFilter, priceMaxUsdc, includeSecurity: true);

    /// <summary>
    /// Filterable overload. Optional <paramref name="chainFilter"/> (lowercased
    /// HashSet) and <paramref name="priceMaxUsdc"/> are applied to all result
    /// fields. Hourly cache keyed on the full filter tuple.
    /// </summary>
    public async Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter,
        HashSet<string>? chainFilter, double? priceMaxUsdc, bool includeSecurity)
    {
        var hourBucket = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month,
            DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc);
        var key = CacheKey(windowDays, marketplaceFilter, chainFilter, priceMaxUsdc, includeSecurity);

        if (_cache.TryGetValue(key, out var entry) && entry.Bucket == hourBucket)
            return entry.Result;

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out entry) && entry.Bucket == hourBucket)
                return entry.Result;

            var fresh = await BuildUncachedAsync(windowDays, marketplaceFilter, chainFilter, priceMaxUsdc, includeSecurity);
            _cache[key] = (hourBucket, fresh);
            return fresh;
        }
        finally { _lock.Release(); }
    }

    private async Task<DigestResult> BuildUncachedAsync(int windowDays, string? marketplaceFilter,
        HashSet<string>? chainFilter, double? priceMaxUsdc, bool includeSecurity)
    {
        var nowUtc = DateTime.UtcNow;
        var sinceUtc = nowUtc.AddDays(-windowDays);
        var snapshotDate = nowUtc.Date.AddDays(-windowDays).ToString("yyyy-MM-dd");

        var hasFilter = marketplaceFilter is not null || chainFilter is not null || priceMaxUsdc is not null;
        var fetchLimit = hasFilter ? MaxNewOfferings * 4 : MaxNewOfferings;
        var newOfferings = await _repo.ListNewSinceAsync(sinceUtc, fetchLimit);
        if (marketplaceFilter is not null)
        {
            newOfferings = newOfferings
                .Where(o => string.Equals(o.MarketplaceVersion, marketplaceFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        if (chainFilter is not null)
        {
            newOfferings = newOfferings
                .Where(o => chainFilter.Contains(o.Chain.ToLowerInvariant()))
                .ToList();
        }
        if (priceMaxUsdc is double priceCap)
        {
            newOfferings = newOfferings.Where(o => o.PriceUsdc <= priceCap).ToList();
        }
        if (newOfferings.Count > MaxNewOfferings)
            newOfferings = newOfferings.Take(MaxNewOfferings).ToList();

        var snapshotExists = await _repo.SnapshotExistsAsync(snapshotDate);
        var gainers = snapshotExists
            ? await _repo.ListGainersAsync(snapshotDate, hasFilter ? MaxGainers * 4 : MaxGainers)
            : new List<OfferingGainer>();
        if (marketplaceFilter is not null)
        {
            gainers = gainers
                .Where(g => string.Equals(g.MarketplaceVersion, marketplaceFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        if (chainFilter is not null || priceMaxUsdc is double)
        {
            var allOfferings = await _repo.ListAllAsync();
            var byId = allOfferings.ToDictionary(o => o.Id);
            gainers = gainers.Where(g =>
                byId.TryGetValue(g.OfferingId, out var o)
                && (chainFilter is null || chainFilter.Contains(o.Chain.ToLowerInvariant()))
                && (priceMaxUsdc is not double cap || o.PriceUsdc <= cap)
            ).ToList();
        }
        if (gainers.Count > MaxGainers)
            gainers = gainers.Take(MaxGainers).ToList();

        // Security enrichment (decoupled cache; populated by SecurityScanWorker).
        IReadOnlyDictionary<string, SecurityVerdict> verdicts =
            new Dictionary<string, SecurityVerdict>();
        if (includeSecurity && _securityRepo is not null && newOfferings.Count > 0)
        {
            var addrs = newOfferings.Select(o => o.AgentAddress.ToLowerInvariant()).Distinct().ToList();
            verdicts = await _securityRepo.GetManyAsync(addrs);
        }

        OfferingSecurity? SecurityFor(string agentAddress)
        {
            if (!includeSecurity) return null;
            var key2 = agentAddress.ToLowerInvariant();
            return verdicts.TryGetValue(key2, out var v)
                ? OfferingSecurity.FromVerdict(v)
                : OfferingSecurity.Pending;
        }

        var newOfferingDtos = newOfferings.Select(o => new NewOffering(
            OfferingId: o.Id,
            AgentName: o.AgentName,
            AgentAddress: o.AgentAddress,
            OfferingName: o.OfferingName,
            Description: o.Description,
            PriceUsdc: o.PriceUsdc,
            PriceType: o.PriceType,
            Chain: o.Chain,
            FirstSeenAt: o.FirstSeenAt.ToString("O", CultureInfo.InvariantCulture),
            Reputation: _reputation.BuildSearchSummary(o),
            MarketplaceVersion: o.MarketplaceVersion,
            Security: SecurityFor(o.AgentAddress))).ToArray();

        // Sub-task A: newAgents + windowStart + partial flag
        var (newAgentsTotal, newAgentsTop) = await _repo.ListNewAgentsSinceAsync(
            sinceUtc, marketplaceFilter, chainFilter, priceMaxUsdc, topLimit: 10);

        // Sub-task B: churnRate
        var (churnBaseline, churnchurned) = await _repo.ComputeChurnAsync(
            sinceUtc, marketplaceFilter, chainFilter, priceMaxUsdc);

        // Sub-task C: cohortSurvival (only when windowDays >= 30)
        IReadOnlyList<CohortSurvivalRow>? cohortSurvival = null;
        if (windowDays >= 30)
        {
            var buckets = await _repo.ListCohortBucketsAsync(sinceUtc, marketplaceFilter, chainFilter, priceMaxUsdc);
            var rows = new List<CohortSurvivalRow>();
            foreach (var b in buckets.Take(12))
            {
                var surviving = await _repo.CountSurvivingInCohortAsync(
                    b.WeekStart, b.WeekEnd, marketplaceFilter, chainFilter, priceMaxUsdc);
                var rate = b.Size == 0 ? 0.0 : (double)surviving / b.Size;
                rows.Add(new CohortSurvivalRow(
                    CohortWeek: b.WeekIso,
                    CohortStart: b.WeekStart.ToString("O", CultureInfo.InvariantCulture),
                    CohortSize: b.Size,
                    Surviving: surviving,
                    SurvivalRate: rate));
            }
            cohortSurvival = rows;
        }

        // Sub-task D: saturationMap (global — not filter-scoped).
        // v1.10.1: explicit "both" preserves the digest's combined-corpus
        // semantics — only marketplaceGap defaults to V2-only.
        var saturationMap = _saturation.PerCategory("both")
            .Select(c => new SaturationMapRow(c.Category, c.Total, c.SaturatedCount, c.SaturationPct))
            .ToList();

        // v1.7.4: newResources — Resources observed for the first time within
        // the window. Resources are V2-only today (V1 marketplace doesn't have
        // a Resources surface), so the marketplaceFilter narrows to v2 OR is
        // null; chain/price filters don't apply to Resources.
        IReadOnlyList<NewResource> newResources = Array.Empty<NewResource>();
        if (_resourcesRepo is not null)
        {
            var resourceMvFilter = marketplaceFilter is null ? null
                : (marketplaceFilter == "v2" ? "v2" : null);
            // If caller filtered to V1, return zero new Resources (correct — they don't exist there).
            if (marketplaceFilter is null || marketplaceFilter == "v2")
            {
                var rows = await _resourcesRepo.ListNewSinceAsync(sinceUtc, resourceMvFilter, MaxNewResources);
                newResources = rows.Select(r => new NewResource(
                    AgentName:          r.AgentName,
                    AgentAddress:       r.AgentAddress,
                    Name:               r.Name,
                    Url:                r.Url,
                    Description:        r.Description,
                    FirstSeenAt:        r.FirstSeenAt.ToString("O", CultureInfo.InvariantCulture),
                    MarketplaceVersion: r.MarketplaceVersion)).ToArray();
            }
        }

        return new DigestResult
        {
            WindowDays         = windowDays,
            WindowStart        = sinceUtc.ToString("O", CultureInfo.InvariantCulture),
            SnapshotComparison = snapshotExists ? "available" : "insufficient_history",
            Partial            = !snapshotExists,
            NewOfferings       = newOfferingDtos,
            NewResources       = newResources,
            Gainers            = gainers,
            NewAgents          = new NewAgentsBlock(newAgentsTotal, newAgentsTop),
            ChurnRate          = churnBaseline == 0
                ? new Models.ChurnRate(0.0, churnchurned, churnBaseline)
                : new Models.ChurnRate((double)churnchurned / churnBaseline, churnchurned, churnBaseline),
            CohortSurvival     = cohortSurvival,
            SaturationMap      = saturationMap,
            ComputedAt         = nowUtc.ToString("O", CultureInfo.InvariantCulture),
        };
    }
}
