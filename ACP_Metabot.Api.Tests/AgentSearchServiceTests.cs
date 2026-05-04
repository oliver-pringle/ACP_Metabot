using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class AgentSearchServiceTests
{
    [Fact]
    public void RrfFusion_AgreementBoosts()
    {
        // Both rankers agree 0xa is best, but put 0xb and 0xc in opposite order.
        // 0xa should win because it collects a high score from both legs.
        var bm25  = new[] { "0xa", "0xb", "0xc" };
        var dense = new[] { "0xa", "0xc", "0xb" };

        var fused = AgentSearchService.ReciprocalRankFusion(bm25, dense, k: 60);

        var ranked = fused.OrderByDescending(kv => kv.Value)
                          .Select(kv => kv.Key)
                          .ToList();
        Assert.Equal("0xa", ranked[0]);
    }

    [Fact]
    public void RrfFusion_MissingFromOneRanker_StillRanks()
    {
        // 0xc only appears in dense, 0xa and 0xb only in BM25.
        // All three should appear in the fused map.
        var bm25  = new[] { "0xa", "0xb" };
        var dense = new[] { "0xc" };

        var fused = AgentSearchService.ReciprocalRankFusion(bm25, dense, k: 60);

        Assert.Contains("0xa", fused.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("0xb", fused.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("0xc", fused.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
