using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.10 Phase 1 — exercises SearchService.ApplyNegativeFilters in isolation.
/// The filter is a pure static function applied between rerank and pagination,
/// so we can test it without spinning up the full DI graph.
///
/// OfferingMatch (today) has no Requirements field, so the
/// ExcludeRequirements filter is a no-op for any hit produced by the current
/// search path. We assert that explicit no-op behaviour in the requirements
/// test below — it documents the Phase 1 limitation and will need updating
/// once Phase 2's schema_facets work adds a requirements-bearing field.
/// </summary>
public class SearchServiceFilterTests
{
    // Factory that mirrors the OfferingMatch ctor used throughout the
    // codebase. Only the fields under test are parameterised.
    private static OfferingMatch Hit(long id, string agentAddress, string chain, double priceUsdc) =>
        new OfferingMatch(
            OfferingId: id,
            AgentName: "TestAgent",
            AgentAddress: agentAddress,
            OfferingName: $"offering_{id}",
            Description: "test description",
            PriceUsdc: priceUsdc,
            PriceType: "one_time",
            Chain: chain,
            Score: 0.9);

    [Fact]
    public void ExcludeAgents_drops_matching_agent_addresses()
    {
        var hits = new[]
        {
            Hit(1, "0xAAAA", "base", 0.10),
            Hit(2, "0xBBBB", "base", 0.20),
        };
        // Exclusion list uses mixed case; filter is case-insensitive after lowercase.
        var filtered = SearchService.ApplyNegativeFilters(hits,
            new SearchFilters(ExcludeAgents: new[] { "0xaaaa" }));
        Assert.Single(filtered);
        Assert.Equal(2L, filtered[0].OfferingId);
    }

    [Fact]
    public void ExcludeChains_drops_matching_chains()
    {
        var hits = new[]
        {
            Hit(1, "0xa", "base", 0.10),
            Hit(2, "0xb", "polygon", 0.20),
            Hit(3, "0xc", "Polygon", 0.30),
        };
        // Mixed-case chain entries should be matched too.
        var filtered = SearchService.ApplyNegativeFilters(hits,
            new SearchFilters(ExcludeChains: new[] { "polygon" }));
        Assert.Single(filtered);
        Assert.Equal(1L, filtered[0].OfferingId);
    }

    [Fact]
    public void MaxPriceUsd_drops_offerings_above_cap()
    {
        var hits = new[]
        {
            Hit(1, "0xa", "base", 0.05),
            Hit(2, "0xb", "base", 0.20),
            Hit(3, "0xc", "base", 0.50),
            Hit(4, "0xd", "base", 1.00),
        };
        // Boundary: equal to the cap should pass (filter uses strict >).
        var filtered = SearchService.ApplyNegativeFilters(hits,
            new SearchFilters(MaxPriceUsd: 0.20));
        Assert.Equal(2, filtered.Count);
        Assert.Equal(1L, filtered[0].OfferingId);
        Assert.Equal(2L, filtered[1].OfferingId);
    }

    [Fact]
    public void ExcludeRequirements_is_noop_when_OfferingMatch_carries_no_requirements_field()
    {
        // Phase 1 limitation: OfferingMatch doesn't expose the requirements
        // schema, so the reflection-based TryGetRequirements helper returns
        // null and the filter passes every hit through. This test documents
        // that contract — once Phase 2 adds a Requirements field (or similar),
        // update this test to assert real exclusion behaviour.
        var hits = new[]
        {
            Hit(1, "0xa", "base", 0.10),
            Hit(2, "0xb", "base", 0.10),
        };
        var filtered = SearchService.ApplyNegativeFilters(hits,
            new SearchFilters(ExcludeRequirements: new[] { "LINK", "USDC" }));
        Assert.Equal(2, filtered.Count);
    }
}
