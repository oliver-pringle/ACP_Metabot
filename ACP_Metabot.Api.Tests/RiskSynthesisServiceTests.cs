using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

// v1.8 closed without tests on the risk_* surfaces. This catches up on
// the pure-math layer that buyers depend on: the composite-score formula
// and grade thresholds. Both are static methods so a test failure pins
// the regression to a single line rather than the orchestrator graph.
//
// Out of scope (covered post-deploy by /admin/risk-watch/tick-now smokes):
//   - peer-fanout failure recovery → integration territory
//   - HMAC webhook payload shape   → mirrors the pulse-sub pin
public class RiskSynthesisServiceTests
{
    // ── ToGrade boundary table ────────────────────────────────────────────

    [Theory]
    [InlineData(100, "A")]
    [InlineData(85,  "A")]
    [InlineData(84,  "B")]
    [InlineData(70,  "B")]
    [InlineData(69,  "C")]
    [InlineData(55,  "C")]
    [InlineData(54,  "D")]
    [InlineData(40,  "D")]
    [InlineData(39,  "F")]
    [InlineData(0,   "F")]
    public void ToGrade_RespectsBoundaries(int score, string expected)
    {
        Assert.Equal(expected, RiskSynthesisService.ToGrade(score));
    }

    // ── ComposeScore: full set ───────────────────────────────────────────

    [Fact]
    public void ComposeScore_AllFresh_WeightedBlend()
    {
        // weights: healthFactor 0.30, approvals 0.30, mevExposure 0.20, reputation 0.20
        // expected = 80*0.30 + 60*0.30 + 70*0.20 + 50*0.20 = 24 + 18 + 14 + 10 = 66
        var components = new Dictionary<string, RiskComponent>
        {
            ["healthFactor"] = new() { Score = 80, Source = "LG", Details = "", Status = "fresh" },
            ["approvals"]    = new() { Score = 60, Source = "RB", Details = "", Status = "fresh" },
            ["mevExposure"]  = new() { Score = 70, Source = "MV", Details = "", Status = "fresh" },
            ["reputation"]   = new() { Score = 50, Source = "TM", Details = "", Status = "fresh" },
        };
        Assert.Equal(66, RiskSynthesisService.ComposeScore(components));
    }

    [Fact]
    public void ComposeScore_OneUnavailable_RenormalisesAcrossRest()
    {
        // healthFactor unavailable → its 0.30 weight is dropped, remaining
        // weights 0.30+0.20+0.20=0.70 are renormalised by dividing the sum
        // by 0.70.  expected = (60*0.30 + 70*0.20 + 50*0.20) / 0.70
        //                    = (18 + 14 + 10) / 0.70 = 42/0.70 = 60.0
        var components = new Dictionary<string, RiskComponent>
        {
            ["healthFactor"] = new() { Score = 0,  Source = "LG", Details = "", Status = "unavailable" },
            ["approvals"]    = new() { Score = 60, Source = "RB", Details = "", Status = "fresh" },
            ["mevExposure"]  = new() { Score = 70, Source = "MV", Details = "", Status = "fresh" },
            ["reputation"]   = new() { Score = 50, Source = "TM", Details = "", Status = "fresh" },
        };
        Assert.Equal(60, RiskSynthesisService.ComposeScore(components));
    }

    [Fact]
    public void ComposeScore_AllUnavailable_ReturnsNeutralFifty()
    {
        // When every peer is silent there's no signal — fall back to 50
        // (neither A nor F). The service surfaces this via fallbacks[] but
        // the score itself stays interpretable for downstream charts.
        var components = new Dictionary<string, RiskComponent>
        {
            ["healthFactor"] = new() { Score = 0, Source = "LG", Details = "", Status = "unavailable" },
            ["approvals"]    = new() { Score = 0, Source = "RB", Details = "", Status = "unavailable" },
            ["mevExposure"]  = new() { Score = 0, Source = "MV", Details = "", Status = "unavailable" },
            ["reputation"]   = new() { Score = 0, Source = "TM", Details = "", Status = "unavailable" },
        };
        Assert.Equal(50, RiskSynthesisService.ComposeScore(components));
    }

    [Fact]
    public void ComposeScore_EmptyDict_ReturnsNeutralFifty()
    {
        // Defensive — RiskOrchestrationService's pre-checks SHOULD prevent
        // this in production, but if a refactor breaks the invariant we
        // want neutral, not div-by-zero.
        Assert.Equal(50, RiskSynthesisService.ComposeScore(new Dictionary<string, RiskComponent>()));
    }

    // ── RiskOrchestrationService.NormalizeChain ──────────────────────────

    [Theory]
    [InlineData(null,        "base")]
    [InlineData("",          "base")]
    [InlineData("   ",       "base")]
    [InlineData("base",      "base")]
    [InlineData("Base",      "base")]
    [InlineData("ethereum",  "ethereum")]
    [InlineData("ETHEREUM",  "ethereum")]
    [InlineData("  Base  ",  "base")]
    public void NormalizeChain_DefaultsToBaseAndLowercases(string? input, string expected)
    {
        Assert.Equal(expected, RiskOrchestrationService.NormalizeChain(input));
    }
}
