using System.Text.Json;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Tests;

public class AgentSearchHitSerializationTests
{
    [Fact]
    public void Serialization_IncludesAllV17Fields()
    {
        var hit = new AgentSearchHit(
            AgentAddress: "0xabc",
            AgentName: "AgentA",
            Score: 0.82,
            TotalOfferings: 5,
            TopOfferings: new[]
            {
                new AgentSearchHitOffering("wallet_scan", 0.10, "v2"),
            },
            TotalJobs: 47,
            TopOfferingNames: new[] { "wallet_scan" },
            Marketplaces: new[] { "v1", "v2" },
            DominantMarketplace: "v2",
            AgentScore: 78);

        var json = JsonSerializer.Serialize(hit);
        Assert.Contains("\"topOfferingNames\":[\"wallet_scan\"]", json);
        Assert.Contains("\"marketplaces\":[\"v1\",\"v2\"]", json);
        Assert.Contains("\"dominantMarketplace\":\"v2\"", json);
        Assert.Contains("\"agentScore\":78", json);
        Assert.Contains("\"offeringName\":\"wallet_scan\"", json);
        Assert.Contains("\"priceUsdc\":0.1", json);
        Assert.Contains("\"marketplaceVersion\":\"v2\"", json);
    }

    [Fact]
    public void Serialization_AgentScoreNull_OmittedFromJson()
    {
        var hit = new AgentSearchHit(
            "0xabc", "A", 0.5, 1,
            Array.Empty<AgentSearchHitOffering>(),
            10,
            Array.Empty<string>(),
            new[] { "v2" },
            "v2",
            AgentScore: null);

        var json = JsonSerializer.Serialize(hit);
        Assert.DoesNotContain("\"agentScore\"", json);
    }
}
