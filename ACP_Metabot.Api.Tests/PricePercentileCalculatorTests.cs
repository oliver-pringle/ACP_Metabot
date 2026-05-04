using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class PricePercentileCalculatorTests
{
    private static (long id, string category, string marketplace, double price)[] Corpus(
        params (long, string, string, double)[] rows) => rows;

    [Fact]
    public void Percentile_OrderedAcrossPeers()
    {
        var corpus = Corpus(
            (1, "wallet", "v2", 0.05),
            (2, "wallet", "v2", 0.10),
            (3, "wallet", "v2", 0.20),
            (4, "wallet", "v2", 0.50),
            (5, "wallet", "v2", 1.00),
            (6, "wallet", "v2", 5.00));
        var calc = new PricePercentileCalculator(lowNThreshold: 5);
        calc.Refresh(corpus);

        var p1 = calc.Compute(1, "wallet", "v2", 0.05);
        Assert.Equal(0, p1.Value);
        Assert.Equal(5, p1.PeerN);
        Assert.False(p1.LowN);

        var p3 = calc.Compute(3, "wallet", "v2", 0.20);
        Assert.Equal(40, p3.Value);  // 2 of 5 peers cheaper
        Assert.False(p3.LowN);

        var p6 = calc.Compute(6, "wallet", "v2", 5.00);
        Assert.Equal(100, p6.Value);
    }

    [Fact]
    public void Percentile_ScopedByCategoryAndMarketplace()
    {
        var corpus = Corpus(
            (1, "wallet", "v1", 0.99),
            (2, "wallet", "v1", 0.99),
            (3, "wallet", "v1", 0.99),
            (4, "wallet", "v1", 0.99),
            (5, "wallet", "v1", 0.99),
            (6, "wallet", "v2", 0.05));   // different marketplace
        var calc = new PricePercentileCalculator(5);
        calc.Refresh(corpus);

        var p6 = calc.Compute(6, "wallet", "v2", 0.05);
        Assert.True(p6.LowN);
        Assert.Null(p6.Value);
        Assert.Equal(0, p6.PeerN);
    }

    [Fact]
    public void Percentile_LowN_FlagsBelowThreshold()
    {
        var corpus = Corpus(
            (1, "wallet", "v2", 0.10),
            (2, "wallet", "v2", 0.20),
            (3, "wallet", "v2", 0.30));
        var calc = new PricePercentileCalculator(5);
        calc.Refresh(corpus);

        var p2 = calc.Compute(2, "wallet", "v2", 0.20);
        Assert.True(p2.LowN);
        Assert.Equal(2, p2.PeerN);
        Assert.Null(p2.Value);
    }
}
