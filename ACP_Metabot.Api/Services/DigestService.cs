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

    public async Task<DigestResult> BuildAsync(int windowDays)
    {
        var nowUtc = DateTime.UtcNow;
        var sinceUtc = nowUtc.AddDays(-windowDays);
        var snapshotDate = nowUtc.Date.AddDays(-windowDays).ToString("yyyy-MM-dd");

        var newOfferings = await _repo.ListNewSinceAsync(sinceUtc, MaxNewOfferings);
        var snapshotExists = await _repo.SnapshotExistsAsync(snapshotDate);

        var gainers = snapshotExists
            ? await _repo.ListGainersAsync(snapshotDate, MaxGainers)
            : new List<OfferingGainer>();

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
            Reputation: _reputation.BuildSearchSummary(o))).ToArray();

        return new DigestResult(
            WindowDays: windowDays,
            SnapshotComparison: snapshotExists ? "available" : "insufficient_history",
            NewOfferings: newOfferingDtos,
            Gainers: gainers,
            ComputedAt: nowUtc.ToString("O", CultureInfo.InvariantCulture));
    }
}
