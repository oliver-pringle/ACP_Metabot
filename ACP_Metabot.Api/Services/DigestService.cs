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

    public async Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter = null)
    {
        var nowUtc = DateTime.UtcNow;
        var sinceUtc = nowUtc.AddDays(-windowDays);
        var snapshotDate = nowUtc.Date.AddDays(-windowDays).ToString("yyyy-MM-dd");

        // Fetch a wider net than MaxNewOfferings so the post-filter still has
        // headroom when marketplace is set. ListNewSinceAsync orders by
        // popularity then recency so the filter doesn't bias toward old rows.
        var fetchLimit = marketplaceFilter is null ? MaxNewOfferings : MaxNewOfferings * 4;
        var newOfferings = await _repo.ListNewSinceAsync(sinceUtc, fetchLimit);
        if (marketplaceFilter is not null)
        {
            newOfferings = newOfferings
                .Where(o => string.Equals(o.MarketplaceVersion, marketplaceFilter, StringComparison.OrdinalIgnoreCase))
                .Take(MaxNewOfferings)
                .ToList();
        }

        var snapshotExists = await _repo.SnapshotExistsAsync(snapshotDate);
        var gainers = snapshotExists
            ? await _repo.ListGainersAsync(snapshotDate, marketplaceFilter is null ? MaxGainers : MaxGainers * 4)
            : new List<OfferingGainer>();
        if (marketplaceFilter is not null)
        {
            gainers = gainers
                .Where(g => string.Equals(g.MarketplaceVersion, marketplaceFilter, StringComparison.OrdinalIgnoreCase))
                .Take(MaxGainers)
                .ToList();
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
            MarketplaceVersion: o.MarketplaceVersion)).ToArray();

        return new DigestResult(
            WindowDays: windowDays,
            SnapshotComparison: snapshotExists ? "available" : "insufficient_history",
            NewOfferings: newOfferingDtos,
            Gainers: gainers,
            ComputedAt: nowUtc.ToString("O", CultureInfo.InvariantCulture));
    }
}
