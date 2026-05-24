using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class SearchNarratorTests : IAsyncLifetime
{
    private string _dbPath = "";
    private Db _db = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"metabot-nar-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        await _db.InitializeSchemaAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    private SearchNarrator Build(IClaudeClient claude)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Search:NarrativeCacheTtlSeconds"] = "3600"
            }).Build();
        return new SearchNarrator(claude, _db, config, NullLogger<SearchNarrator>.Instance);
    }

    private sealed class FakeClaudeClient : IClaudeClient
    {
        public string? FixedResponse { get; init; }
        public Exception? ThrowsOnCall { get; init; }
        public int CallCount { get; private set; }
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
        {
            CallCount++;
            if (ThrowsOnCall is not null) throw ThrowsOnCall;
            return Task.FromResult(FixedResponse ?? "{\"summary\":\"x\",\"perResultReason\":[]}");
        }
    }

    // Mirrors the OfferingMatch positional ctor used across the test suite
    // (see SearchServiceFilterTests.Hit). Only fields exercised by the
    // narrator are parameterised.
    private static OfferingMatch Hit(string name, string addr, string category = "defi", double price = 0.10) =>
        new OfferingMatch(
            OfferingId: 1,
            AgentName: "TestAgent",
            AgentAddress: addr,
            OfferingName: name,
            Description: "test description for narrator",
            PriceUsdc: price,
            PriceType: "one_time",
            Chain: "base",
            Score: 0.85,
            Reputation: null,
            Category: category,
            MarketplaceVersion: "v2");

    [Fact]
    public async Task NarrateAsync_returns_ok_status_and_parsed_payload_on_success()
    {
        var fakeJson = "{\"summary\":\"Three options for HF monitoring.\",\"perResultReason\":[{\"offering\":\"hf_check@0x1836\",\"reason\":\"cheapest single-shot\"}]}";
        var claude = new FakeClaudeClient { FixedResponse = fakeJson };
        var narrator = Build(claude);
        var hits = new[] { Hit("hf_check", "0x" + new string('1', 40)) };

        var r = await narrator.NarrateAsync("watch HF on aave", hits, null, CancellationToken.None);

        Assert.Equal("ok", r.Status);
        Assert.Contains("HF monitoring", r.Summary);
        Assert.NotEmpty(r.PerResultReason);
        Assert.Equal("hf_check@0x1836", r.PerResultReason[0].Offering);
        Assert.Equal("cheapest single-shot", r.PerResultReason[0].Reason);
        Assert.NotEmpty(r.CitedOfferings);
        Assert.False(r.CacheHit);
    }

    [Fact]
    public async Task NarrateAsync_serves_from_cache_on_second_identical_call()
    {
        var claude = new FakeClaudeClient
        {
            FixedResponse = "{\"summary\":\"first call narration\",\"perResultReason\":[{\"offering\":\"hf_check@0x1836\",\"reason\":\"r\"}]}"
        };
        var narrator = Build(claude);
        var hits = new[] { Hit("hf_check", "0x" + new string('1', 40)) };

        var first = await narrator.NarrateAsync("watch HF on aave", hits, null, CancellationToken.None);
        Assert.False(first.CacheHit);
        Assert.Equal(1, claude.CallCount);
        Assert.Equal("ok", first.Status);

        var second = await narrator.NarrateAsync("watch HF on aave", hits, null, CancellationToken.None);
        Assert.True(second.CacheHit, "second identical call should serve from cache");
        Assert.Equal(1, claude.CallCount); // Claude not called again
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.PerResultReason.Count, second.PerResultReason.Count);
    }

    [Fact]
    public async Task NarrateAsync_returns_degraded_envelope_on_LLM_failure()
    {
        var claude = new FakeClaudeClient { ThrowsOnCall = new InvalidOperationException("boom") };
        var narrator = Build(claude);
        var hits = new[] { Hit("hf_check", "0x" + new string('1', 40)) };

        var r = await narrator.NarrateAsync("watch HF", hits, null, CancellationToken.None);

        Assert.StartsWith("degraded_", r.Status);
        Assert.Equal("degraded_llm_unavailable", r.Status);
        Assert.NotEmpty(r.CitedOfferings); // still returns cited offerings even on failure
        Assert.Empty(r.PerResultReason);
    }
}
