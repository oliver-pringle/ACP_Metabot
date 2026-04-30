using System.Text.Json;
using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class SearchFusionEvaluationTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "search-eval-queries.json");

    [Fact]
    public void Fixture_Loads_AndHasMinimumQueries()
    {
        Assert.True(File.Exists(FixturePath), $"fixture missing at {FixturePath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        Assert.True(doc.RootElement.TryGetProperty("queries", out var queries),
            "fixture must have a top-level 'queries' array");
        Assert.True(queries.GetArrayLength() >= 5,
            "fixture should contain at least 5 queries");
        foreach (var q in queries.EnumerateArray())
        {
            Assert.True(q.TryGetProperty("query", out var qProp));
            Assert.False(string.IsNullOrWhiteSpace(qProp.GetString()),
                "every fixture row must have a non-empty query string");
            Assert.True(q.TryGetProperty("expectedAgentAddress", out _));
            Assert.True(q.TryGetProperty("expectedOfferingName", out _));
        }
    }

    // RRF unit tests — pure-function, no DI required. Validates the core
    // fusion behaviour: agreement = boost, disagreement = average, missing-
    // from-one = lower than any agreed item.

    [Fact]
    public void Rrf_TopOfBothRankers_Wins()
    {
        var dense   = new long[] { 10, 20, 30 };
        var lexical = new long[] { 10, 40, 50 };
        var fused = SearchService.ReciprocalRankFusion(dense, lexical, k: 60);
        // Id 10 ranks #1 in both → highest fused score.
        var ranked = fused.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        Assert.Equal(10L, ranked[0]);
    }

    [Fact]
    public void Rrf_MissingFromOneRanker_Penalised()
    {
        var dense   = new long[] { 10, 20 };       // 10 is #1 dense only
        var lexical = new long[] { 30, 10 };       // 10 is #2 lexical only
        var fused = SearchService.ReciprocalRankFusion(dense, lexical, k: 60);
        // Id 10 in BOTH should beat id 30 (only in lexical at #1) once you
        // sum 1/(60+1) + 1/(60+2) > 1/(60+1).
        Assert.True(fused[10] > fused[30]);
    }

    [Fact]
    public void Rrf_KConstant_FlattensTail()
    {
        // Higher k → top items contribute relatively less; fused scores
        // compress toward equal. Sanity-check the math by comparing two ks.
        var dense   = new long[] { 10, 20, 30, 40 };
        var lexical = new long[] { 40, 30, 20, 10 };
        var fLow  = SearchService.ReciprocalRankFusion(dense, lexical, k: 1);
        var fHigh = SearchService.ReciprocalRankFusion(dense, lexical, k: 1000);
        // Spread (max - min) should be smaller at high k.
        var spreadLow  = fLow.Values.Max()  - fLow.Values.Min();
        var spreadHigh = fHigh.Values.Max() - fHigh.Values.Min();
        Assert.True(spreadHigh < spreadLow);
    }

    [Fact(Skip = "manual; requires populated fixture (TBDs filled in) + prod DB snapshot at data/acp_metabot.db. Run with `dotnet test --filter HybridBeatsCosineOn30Q`")]
    public Task HybridBeatsCosineOn30Q()
    {
        // Future: boot the real service stack, run both ranking paths, compute
        // MRR@10 for each, assert hybrid >= cosine. Requires:
        //   1. Voyage API credentials (VOYAGE_API_KEY)
        //   2. Local data/acp_metabot.db with embeddings populated
        //   3. All TBD fields in search-eval-queries.json filled in
        return Task.CompletedTask;
    }
}
