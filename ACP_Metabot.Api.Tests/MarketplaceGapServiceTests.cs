using ACP_Metabot.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

// v1.10.1 — marketplace slice {v1, v2, both} on MarketplaceGapService.
//
// CategoryService is constructed with a null embedder + NullLogger; we only
// touch its .Categories property, which is initialised empty and never reads
// from disk in this hot path. Description-join therefore falls back to "" —
// these tests assert structural correctness, not description content.
public class MarketplaceGapServiceTests
{
    private static float[] Vec(params float[] xs) => xs;

    private static (SaturationCalculator sat, MarketplaceGapService svc) BuildHarness(
        params (long id, string category, string marketplace, float[] embedding)[] corpus)
    {
        var sat = new SaturationCalculator(threshold: 0.85);
        sat.Refresh(corpus);
        var cats = new CategoryService(null!, NullLogger<CategoryService>.Instance);
        var svc  = new MarketplaceGapService(sat, cats);
        return (sat, svc);
    }

    [Fact]
    public void Analyze_DefaultMarketplace_IsV2()
    {
        var (_, svc) = BuildHarness(
            (1, "wallet", "v1", Vec(1f, 0f)),
            (2, "wallet", "v2", Vec(0f, 1f)));

        var result = svc.Analyze(category: null, limit: 5);

        Assert.Equal("v2", result.Marketplace);
        // V2 slice has 1 wallet offering, V1 has 1 — totals must differ
        // proving the slice was actually applied.
        var row = result.Opportunities.Single();
        Assert.Equal(1, row.Total);
    }

    [Theory]
    [InlineData("v1", 1)]
    [InlineData("v2", 1)]
    [InlineData("both", 2)]
    public void Analyze_Marketplace_PassesThroughToSaturation(string marketplace, int expectedTotal)
    {
        var (_, svc) = BuildHarness(
            (1, "wallet", "v1", Vec(1f, 0f)),
            (2, "wallet", "v2", Vec(0f, 1f)));

        var result = svc.Analyze(category: null, limit: 5, marketplace: marketplace);

        Assert.Equal(marketplace, result.Marketplace);
        var row = result.Opportunities.Single();
        Assert.Equal(expectedTotal, row.Total);
    }

    [Fact]
    public void Analyze_NormalisesMarketplaceCaseAndWhitespace()
    {
        var (_, svc) = BuildHarness(
            (1, "wallet", "v2", Vec(1f, 0f)));

        var upper = svc.Analyze(null, 5, " V2 ");

        Assert.Equal("v2", upper.Marketplace);
    }

    [Fact]
    public void Analyze_InvalidMarketplaceValue_CoercesToV2()
    {
        // Endpoint-layer validation rejects unknowns with 400; the SERVICE
        // layer coerces unknowns to "v2" so a typo doesn't crash background
        // workers. NormalizeMarketplaceTag returns "v1" for unknowns at the
        // corpus layer (safety for bad upstream data), but the gap-service
        // layer chooses v2 because it's the new default.
        var (_, svc) = BuildHarness(
            (1, "wallet", "v2", Vec(1f, 0f)));

        var result = svc.Analyze(null, 5, "ferengi");

        Assert.Equal("v2", result.Marketplace);
    }

    [Fact]
    public void Analyze_EmptySlice_PopulatesPerSliceNote()
    {
        // Only V1 offerings; asking for V2 → all rows have Total=0.
        var (_, svc) = BuildHarness(
            (1, "wallet",  "v1", Vec(1f, 0f)),
            (2, "trading", "v1", Vec(0f, 1f)));

        var result = svc.Analyze(null, 5, "v2");

        Assert.Equal("v2", result.Marketplace);
        Assert.NotNull(result.Note);
        Assert.Contains("no v2 offerings", result.Note!);
    }

    [Fact]
    public void Analyze_ColdBoot_EmptyCorpus_PopulatesColdBootNote()
    {
        // Empty corpus → SaturationCalculator's _byCategory is empty →
        // per.Count == 0. The cold-boot note fires regardless of slice.
        var (_, svc) = BuildHarness();

        var result = svc.Analyze(null, 5, "v2");

        Assert.NotNull(result.Note);
        Assert.Contains("saturationMap not yet computed", result.Note!);
        Assert.Empty(result.Opportunities);
    }

    [Fact]
    public void Analyze_PopulatedSlice_NoNote()
    {
        var (_, svc) = BuildHarness(
            (1, "wallet", "v2", Vec(1f, 0f)));

        var result = svc.Analyze(null, 5, "v2");

        Assert.Null(result.Note);
        Assert.NotEmpty(result.Opportunities);
    }

    [Fact]
    public void Analyze_BothSlice_PreservesPreV1101Numbers()
    {
        // Q3: cross-marketplace near-dup edges count. A V1 offering near-
        // duped to a V2 offering must show as saturated in the "both" view
        // (matching pre-v1.10.1 behaviour where all offerings were pooled
        // anyway). The "v1" and "v2" slices INDEPENDENTLY mark the same
        // offerings as saturated — Q3's resolved semantics.
        var (_, svc) = BuildHarness(
            (1, "wallet", "v1", Vec(1f, 0f)),
            (2, "wallet", "v2", Vec(0.99f, 0.05f)));   // near-dup of 1

        var both = svc.Analyze(null, 5, "both").Opportunities.Single();
        var v1   = svc.Analyze(null, 5, "v1").Opportunities.Single();
        var v2   = svc.Analyze(null, 5, "v2").Opportunities.Single();

        Assert.Equal(2, both.Total);
        Assert.Equal(2, both.SaturatedCount);
        Assert.Equal(1.0, both.SaturationPct, 3);

        Assert.Equal(1, v1.Total);
        Assert.Equal(1, v1.SaturatedCount);

        Assert.Equal(1, v2.Total);
        Assert.Equal(1, v2.SaturatedCount);
    }

    [Fact]
    public void Analyze_LimitClampedTo1And20()
    {
        var (_, svc) = BuildHarness(
            (1, "wallet", "v2", Vec(1f, 0f)));

        var clampedLow  = svc.Analyze(null, limit: 0,  marketplace: "v2");
        var clampedHigh = svc.Analyze(null, limit: 99, marketplace: "v2");

        Assert.True(clampedLow.Opportunities.Count <= 1);
        Assert.True(clampedHigh.Opportunities.Count <= 20);
    }
}
