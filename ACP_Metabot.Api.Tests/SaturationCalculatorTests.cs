using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class SaturationCalculatorTests
{
    private static float[] Vec(params float[] xs) => xs;

    private static (long id, string category, float[] embedding)[] Corpus(params (long, string, float[])[] rows) => rows;

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

        var rollup = calc.PerCategory();
        var wallet = rollup.Single(c => c.Category == "wallet");
        Assert.Equal(4, wallet.Total);
        Assert.Equal(2, wallet.SaturatedCount);  // ids 1, 2 each have one near-dup
        Assert.Equal(0.5, wallet.SaturationPct, 3);
    }

    [Fact]
    public void NearDuplicateCount_EmptyCategory_ReturnsZero()
    {
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(Array.Empty<(long, string, float[])>());
        Assert.Equal(0, calc.NearDuplicateCount(offeringId: 99, category: "wallet"));
    }
}
