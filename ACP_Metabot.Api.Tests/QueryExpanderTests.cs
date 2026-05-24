using System.Text.Json;
using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class QueryExpanderTests
{
    private static QueryExpander Build()
    {
        var entries = new[]
        {
            new GlossaryEntry("health_factor", new[] { "HF", "health factor" }),
            new GlossaryEntry("proof_of_reserve", new[] { "PoR", "proof of reserve" }),
            new GlossaryEntry("USDC", new[] { "USDC", "usdc" }),
        };
        return new QueryExpander(entries);
    }

    [Fact]
    public void Expand_unknown_query_returns_primary_only_with_no_hits()
    {
        var x = Build();
        var r = x.Expand("random query string");
        Assert.Equal("random query string", r.Primary);
        Assert.Empty(r.Synonyms);
        Assert.Empty(r.GlossaryHits);
    }

    [Fact]
    public void Expand_alias_substring_emits_canonical_synonym_and_hit()
    {
        var x = Build();
        var r = x.Expand("check my HF on aave");
        Assert.Equal("check my HF on aave", r.Primary);
        Assert.Contains("health_factor", r.Synonyms);
        Assert.Contains(r.GlossaryHits, h => h.Contains("HF") && h.Contains("health_factor"));
    }

    [Fact]
    public void Expand_is_case_insensitive_on_aliases()
    {
        var x = Build();
        var r = x.Expand("monitor hf");
        Assert.Contains("health_factor", r.Synonyms);
    }

    [Fact]
    public void Expand_deduplicates_when_multiple_aliases_map_to_same_canonical()
    {
        var x = Build();
        var r = x.Expand("HF health factor risk");
        Assert.Equal(1, r.Synonyms.Count(s => s == "health_factor"));
    }

    [Fact]
    public void Production_glossary_file_loads_with_at_least_200_entries()
    {
        var path = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !File.Exists(Path.Combine(path, "ACP_Metabot.Api", "Data", "DeFiGlossary.json")); i++)
            path = Path.GetDirectoryName(path) ?? path;
        var glossaryPath = Path.Combine(path, "ACP_Metabot.Api", "Data", "DeFiGlossary.json");
        Assert.True(File.Exists(glossaryPath), $"glossary file should exist at {glossaryPath}");

        var json = File.ReadAllText(glossaryPath);
        var entries = JsonSerializer.Deserialize<GlossaryEntry[]>(json);
        Assert.NotNull(entries);
        Assert.True(entries!.Length >= 200,
            $"glossary should have at least 200 entries, found {entries.Length}");
        Assert.All(entries, e => Assert.NotEmpty(e.Aliases));
    }
}
