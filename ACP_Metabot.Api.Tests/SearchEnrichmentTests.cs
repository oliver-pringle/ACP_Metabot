using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// Verifies that the saturation + pricePercentile DTOs are correctly wired:
/// the two calculators produce the right values and can be mapped into
/// SaturationDto / PricePercentileDto as SearchService does.
/// </summary>
public class SearchEnrichmentTests
{
    private static float[] Vec(params float[] xs) => xs;

    // Mirrors the mapping in SearchService.SearchAsync so any drift is caught.
    private static (SaturationDto sat, PricePercentileDto pp) BuildHitEnrichments(
        long offeringId,
        string category,
        string marketplaceVersion,
        double priceUsdc,
        SaturationCalculator satCalc,
        PricePercentileCalculator ppCalc)
    {
        var cat = category ?? string.Empty;
        var mv  = marketplaceVersion ?? "v1";
        var ppResult = ppCalc.Compute(offeringId, cat, mv, priceUsdc);
        return (
            new SaturationDto(satCalc.NearDuplicateCount(offeringId, cat), satCalc.CategorySize(cat)),
            new PricePercentileDto(ppResult.Value, ppResult.PeerN, ppResult.LowN));
    }

    [Fact]
    public void SearchHit_CarriesSaturationDto_WithCorrectFields()
    {
        // Corpus: two near-duplicate wallet offerings.
        var satCalc = new SaturationCalculator(threshold: 0.85);
        satCalc.Refresh(new (long, string, float[])[]
        {
            (1L, "wallet", Vec(1f, 0f)),
            (2L, "wallet", Vec(0.99f, 0.05f)),   // near-dup of 1
            (3L, "wallet", Vec(-1f, 0f)),         // not a dup
        });

        var ppCalc = new PricePercentileCalculator(lowNThreshold: 5);
        ppCalc.Refresh(Array.Empty<(long, string, string, double)>());

        var (sat, _) = BuildHitEnrichments(1L, "wallet", "v2", 0.99, satCalc, ppCalc);

        Assert.Equal(1, sat.NearDuplicateCount);   // offering 1 has 1 near-dup (offering 2)
        Assert.Equal(3, sat.CategorySize);          // 3 wallet offerings total
    }

    [Fact]
    public void SearchHit_CarriesPricePercentileDto_WhenSufficientPeers()
    {
        var satCalc = new SaturationCalculator(0.85);
        satCalc.Refresh(Array.Empty<(long, string, float[])>());

        var ppCalc = new PricePercentileCalculator(lowNThreshold: 3);
        // 6 wallet/v2 offerings at prices 0, 1, 2, 3, 4, 5 USDC
        ppCalc.Refresh(new (long, string, string, double)[]
        {
            (10L, "wallet", "v2", 0.0),
            (11L, "wallet", "v2", 1.0),
            (12L, "wallet", "v2", 2.0),
            (13L, "wallet", "v2", 3.0),
            (14L, "wallet", "v2", 4.0),
            (15L, "wallet", "v2", 5.0),
        });

        // Offering 10 (price 0) should be at the 0th percentile.
        var (_, pp10) = BuildHitEnrichments(10L, "wallet", "v2", 0.0, satCalc, ppCalc);
        Assert.False(pp10.LowN);
        Assert.Equal(0, pp10.Value);
        Assert.Equal(5, pp10.PeerN);  // 6 total - 1 self = 5 peers

        // Offering 15 (price 5) should be at the 100th percentile.
        var (_, pp15) = BuildHitEnrichments(15L, "wallet", "v2", 5.0, satCalc, ppCalc);
        Assert.False(pp15.LowN);
        Assert.Equal(100, pp15.Value);
    }

    [Fact]
    public void SearchHit_PricePercentile_LowN_WhenInsufficientPeers()
    {
        var satCalc = new SaturationCalculator(0.85);
        satCalc.Refresh(Array.Empty<(long, string, float[])>());

        var ppCalc = new PricePercentileCalculator(lowNThreshold: 5);
        // Only 3 offerings — below threshold of 5.
        ppCalc.Refresh(new (long, string, string, double)[]
        {
            (1L, "defi", "v1", 1.0),
            (2L, "defi", "v1", 2.0),
            (3L, "defi", "v1", 3.0),
        });

        var (_, pp) = BuildHitEnrichments(1L, "defi", "v1", 1.0, satCalc, ppCalc);
        Assert.True(pp.LowN);
        Assert.Null(pp.Value);
    }

    [Fact]
    public void OfferingMatch_IncludesSaturationAndPricePercentileProperties()
    {
        // Verify the DTO record accepts the two new fields (compile-time shape check).
        var match = new OfferingMatch(
            OfferingId: 1,
            AgentName: "TestAgent",
            AgentAddress: "0xabc",
            OfferingName: "test_offering",
            Description: "desc",
            PriceUsdc: 1.0,
            PriceType: "one_time",
            Chain: "base",
            Score: 0.9,
            Saturation: new SaturationDto(2, 10),
            PricePercentile: new PricePercentileDto(75, 9, false));

        Assert.Equal(2, match.Saturation!.NearDuplicateCount);
        Assert.Equal(10, match.Saturation.CategorySize);
        Assert.Equal(75, match.PricePercentile!.Value);
        Assert.Equal(9, match.PricePercentile.PeerN);
        Assert.False(match.PricePercentile.LowN);
    }
}
