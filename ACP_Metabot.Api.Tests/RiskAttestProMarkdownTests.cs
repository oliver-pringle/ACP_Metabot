using ACP_Metabot.Api.Services;
using System.Text.Json;
using Xunit;

namespace ACP_Metabot.Api.Tests;

public sealed class RiskAttestProMarkdownTests
{
    private static JsonElement SampleComponents() => JsonDocument.Parse("""
        {
          "healthFactor": {"score":85,"source":"LiquidGuard","status":"fresh","details":"Aave V3 HF 1.87"},
          "approvals":    {"score":60,"source":"RevokeBot","status":"fresh","details":"2 high-risk","highRiskCount":2},
          "mev":          {"score":92,"source":"MEVProtect","status":"fresh","details":"0 sandwich in 30d"},
          "reputation":   {"score":50,"source":"TheMetaBot","status":"fresh","details":"not ACP"},
          "arena":        {"score":50,"source":"TheArenaBot","status":"fresh","details":"not a participant"},
          "witness":      {"score":50,"source":"TheWitnessBot","status":"fresh","details":"no manifest"},
          "trajectory":   {"score":75,"source":"history","status":"fresh","details":"improving","direction":"improving"}
        }
    """).RootElement;

    [Fact]
    public void Generate_includes_h1_title_with_wallet()
    {
        var md = RiskAttestProMarkdown.Generate("0xABC", "base", 72, "OK", "B", SampleComponents(), "summary");
        Assert.Contains("# riskAttestPro report — 0xabc", md);
    }

    [Fact]
    public void Generate_includes_verdict_grade_and_summary()
    {
        var md = RiskAttestProMarkdown.Generate("0xabc", "base", 72, "OK", "B", SampleComponents(), "test summary text");
        Assert.Contains("**Verdict:** OK (grade B, score 72/100)", md);
        Assert.Contains("test summary text", md);
    }

    [Fact]
    public void Generate_includes_section_per_signal()
    {
        var md = RiskAttestProMarkdown.Generate("0xabc", "base", 72, "OK", "B", SampleComponents(), "");
        foreach (var section in new[] { "## Health factor", "## Approvals", "## MEV exposure", "## Reputation", "## Arena overlap", "## Witness manifest", "## 30-day trajectory" })
            Assert.Contains(section, md);
    }

    [Fact]
    public void Generate_is_deterministic_across_runs()
    {
        var md1 = RiskAttestProMarkdown.Generate("0xabc", "base", 72, "OK", "B", SampleComponents(), "s");
        var md2 = RiskAttestProMarkdown.Generate("0xabc", "base", 72, "OK", "B", SampleComponents(), "s");
        Assert.Equal(md1, md2);
    }

    [Fact]
    public void Generate_marks_unavailable_components_explicitly()
    {
        var components = JsonDocument.Parse("""
            {"healthFactor":{"score":50,"source":"LiquidGuard","status":"unavailable","details":"timed out"}}
        """).RootElement;
        var md = RiskAttestProMarkdown.Generate("0xabc", "base", 50, "INSUFFICIENT_DATA", "F", components, "");
        Assert.Contains("⚠ Source unavailable", md);
    }
}
