using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class SaturationCalculatorTests
{
    private static float[] Vec(params float[] xs) => xs;

    // Default marketplace tag "v1" preserves pre-v1.10.1 single-pool behaviour
    // for tests that don't care about the marketplace dimension. The
    // marketplace-aware tests below construct corpus rows explicitly.
    private static (long id, string category, string marketplace, float[] embedding)[]
        Corpus(params (long, string, float[])[] rows)
            => rows.Select(r => (r.Item1, r.Item2, "v1", r.Item3)).ToArray();

    [Fact]
    public void NearDuplicateCount_CountsOnlyAboveThreshold()
    {
        var corpus = Corpus(
            (1, "wallet", Vec(1f, 0f, 0f)),
            (2, "wallet", Vec(0.99f, 0.1f, 0f)),   // ~ same direction
            (3, "wallet", Vec(0.5f, 0.5f, 0.7f)),  // different
            (4, "wallet", Vec(-1f, 0f, 0f)));      // opposite
        var calc = new SaturationCalculator(threshold: 0.85);
        calc.Refresh(corpus);

        Assert.Equal(1, calc.NearDuplicateCount(offeringId: 1, category: "wallet"));
        Assert.Equal(1, calc.NearDuplicateCount(offeringId: 2, category: "wallet"));
        Assert.Equal(0, calc.NearDuplicateCount(offeringId: 3, category: "wallet"));
        Assert.Equal(0, calc.NearDuplicateCount(offeringId: 4, category: "wallet"));
    }

    [Fact]
    public void NearDuplicateCount_ScopedToCategory()
    {
        var corpus = Corpus(
            (1, "wallet", Vec(1f, 0f)),
            (2, "wallet", Vec(0.99f, 0.05f)),
            (3, "trading", Vec(1f, 0f)));   // identical direction but different category
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        Assert.Equal(1, calc.NearDuplicateCount(1, "wallet"));
        Assert.Equal(0, calc.NearDuplicateCount(3, "trading"));
    }

    [Fact]
    public void CategorySaturation_ComputesFraction()
    {
        var corpus = Corpus(
            (1, "wallet", Vec(1f, 0f)),
            (2, "wallet", Vec(0.99f, 0.05f)),
            (3, "wallet", Vec(-1f, 0f)),
            (4, "wallet", Vec(0f, 1f)));
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        var rollup = calc.PerCategory("both");
        var wallet = rollup.Single(c => c.Category == "wallet");
        Assert.Equal(4, wallet.Total);
        Assert.Equal(2, wallet.SaturatedCount);  // ids 1, 2 each have one near-dup
        Assert.Equal(0.5, wallet.SaturationPct, 3);
    }

    [Fact]
    public void NearDuplicateCount_EmptyCategory_ReturnsZero()
    {
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(Array.Empty<(long, string, string, float[])>());
        Assert.Equal(0, calc.NearDuplicateCount(offeringId: 99, category: "wallet"));
    }

    // ===== v1.10.1 marketplace-slice tests =====

    [Fact]
    public void MarketplaceSlice_Rollups_HaveDistinctTotals()
    {
        // 2 V1 + 3 V2 wallet offerings, none near-dup of each other.
        var corpus = new (long, string, string, float[])[]
        {
            (1, "wallet", "v1", Vec(1f, 0f, 0f, 0f, 0f)),
            (2, "wallet", "v1", Vec(0f, 1f, 0f, 0f, 0f)),
            (3, "wallet", "v2", Vec(0f, 0f, 1f, 0f, 0f)),
            (4, "wallet", "v2", Vec(0f, 0f, 0f, 1f, 0f)),
            (5, "wallet", "v2", Vec(0f, 0f, 0f, 0f, 1f)),
        };
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        Assert.Equal(2, calc.PerCategory("v1").Single().Total);
        Assert.Equal(3, calc.PerCategory("v2").Single().Total);
        Assert.Equal(5, calc.PerCategory("both").Single().Total);
    }

    [Fact]
    public void MarketplaceSlice_CrossMarketplaceNearDupsCount()
    {
        // Q3: a V1 offering near-duped to a V2 offering bumps BOTH ids in
        // the flat _nearDup dict. So the V1 slice sees its V1 offering as
        // saturated, AND the V2 slice sees its V2 offering as saturated.
        var corpus = new (long, string, string, float[])[]
        {
            (1, "wallet", "v1", Vec(1f, 0f)),         // V1 — near-dup of V2 id 2
            (2, "wallet", "v2", Vec(0.99f, 0.05f)),   // V2 — near-dup of V1 id 1
        };
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        // Both ids have 1 cross-marketplace near-dup neighbour.
        Assert.Equal(1, calc.NearDuplicateCount(1, "wallet"));
        Assert.Equal(1, calc.NearDuplicateCount(2, "wallet"));

        var v1 = calc.PerCategory("v1").Single();
        var v2 = calc.PerCategory("v2").Single();
        var both = calc.PerCategory("both").Single();

        Assert.Equal(1, v1.Total);
        Assert.Equal(1, v1.SaturatedCount);   // V1's single offering IS saturated (by the V2 sibling)
        Assert.Equal(1, v2.Total);
        Assert.Equal(1, v2.SaturatedCount);   // V2's single offering IS saturated (by the V1 sibling)
        Assert.Equal(2, both.Total);
        Assert.Equal(2, both.SaturatedCount);
    }

    [Fact]
    public void MarketplaceSlice_EmptySlice_ReturnsZeroTotals()
    {
        // All V1; ask for V2 → every category row has Total=0.
        var corpus = Corpus(
            (1, "wallet", Vec(1f, 0f)),
            (2, "trading", Vec(0f, 1f)));
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        var v2 = calc.PerCategory("v2");
        Assert.NotEmpty(v2);
        Assert.All(v2, r => Assert.Equal(0, r.Total));
        Assert.All(v2, r => Assert.Equal(0.0, r.SaturationPct));
    }

    [Fact]
    public void MarketplaceSlice_DefaultIsV2()
    {
        // Calling PerCategory() with no args returns the V2 slice (default
        // flipped per Q2 in the v1.10.1 spec).
        var corpus = new (long, string, string, float[])[]
        {
            (1, "wallet", "v1", Vec(1f, 0f)),
            (2, "wallet", "v2", Vec(0f, 1f)),
        };
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        Assert.Equal(1, calc.PerCategory().Single().Total);  // V2 default
        Assert.Equal(1, calc.PerCategory("v2").Single().Total);
        Assert.Equal(1, calc.PerCategory("v1").Single().Total);
        Assert.Equal(2, calc.PerCategory("both").Single().Total);
    }

    [Fact]
    public void MarketplaceSlice_UnknownMarketplaceTag_CoercesToV1()
    {
        // Offerings with garbage marketplace tags are bucketed under "v1"
        // (the legacy default) — safety for any future upstream that emits
        // weird values.
        var corpus = new (long, string, string, float[])[]
        {
            (1, "wallet", "v3-future", Vec(1f, 0f)),
            (2, "wallet", "",          Vec(0f, 1f)),
        };
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        Assert.Equal(2, calc.PerCategory("v1").Single().Total);
        Assert.Equal(0, calc.PerCategory("v2").Single().Total);
    }
}
