using System.Globalization;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

// Computes the daily / N-day digest: newly-launched offerings + offerings
// whose hire count grew most over the window. Pure read path — relies on
// the snapshot table written by MarketplaceIndexerService.
public class DigestService
{
    private readonly OfferingRepository _repo;
    private readonly ReputationService _reputation;

    private const int MaxNewOfferings = 25;
    private const int MaxGainers = 25;

    public DigestService(OfferingRepository repo, ReputationService reputation)
    {
        _repo = repo;
        _reputation = reputation;
    }

    public Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter = null)
        => BuildAsync(windowDays, marketplaceFilter,
            chainFilter: null, priceMaxUsdc: null);

    /// <summary>
    /// Filterable overload. Optional <paramref name="chainFilter"/> (lowercased
    /// HashSet) and <paramref name="priceMaxUsdc"/> are applied to both
    /// `newOfferings` and `gainers`. Wider initial fetch window when filters
    /// are present so we keep the same final list size.
    /// </summary>
    public async Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter,
        HashSet<string>? chainFilter, double? priceMaxUsdc)
    {
        var nowUtc = DateTime.UtcNow;
        var sinceUtc = nowUtc.AddDays(-windowDays);
        var snapshotDate = nowUtc.Date.AddDays(-windowDays).ToString("yyyy-MM-dd");

        // Fetch a wider net when ANY filter is set so the post-filter still
        // has headroom. ListNewSinceAsync orders by popularity then recency so
        // the filter doesn't bias toward old rows.
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
        // Gainers don't carry chain/price natively; intersect with the offering
        // table when those filters are set so the response respects them.
        if (chainFilter is not null || priceMaxUsdc is double)
        {
            // Build an ad-hoc lookup for the gainers we have. List<Offering>
            // would be cleaner but ListGainersAsync intentionally returns a
            // narrower DTO to keep the snapshot-comparison query cheap.
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
            MarketplaceVersion: o.MarketplaceVersion)).ToArray();

        return new DigestResult(
            WindowDays: windowDays,
            SnapshotComparison: snapshotExists ? "available" : "insufficient_history",
            NewOfferings: newOfferingDtos,
            Gainers: gainers,
            ComputedAt: nowUtc.ToString("O", CultureInfo.InvariantCulture));
    }
}
