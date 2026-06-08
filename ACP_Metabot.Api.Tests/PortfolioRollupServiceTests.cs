using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// Verifies the /v1/resources/portfolioRollup envelope shape, cross-bot edge
/// rendering, and 5-min cache stickiness. Uses an in-process Db fixture so
/// no ASP.NET host or sibling bots need to be reachable.
/// </summary>
public class PortfolioRollupServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _offerings;
    private readonly AgentReputationCacheRepository _repCache;
    private readonly AgentResourcesRepository _resources;
    private readonly IConfiguration _config;

    public PortfolioRollupServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_portfolio_rollup_test_{Guid.NewGuid():N}.db");
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(_config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _offerings = new OfferingRepository(_db);
        _repCache = new AgentReputationCacheRepository(_db);
        _resources = new AgentResourcesRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private PortfolioRollupService BuildService(IConfiguration? overrides = null)
    {
        return new PortfolioRollupService(
            _repCache,
            _offerings,
            _resources,
            overrides ?? _config,
            NullLogger<PortfolioRollupService>.Instance);
    }

    private static JsonElement Roundtrip(object envelope)
    {
        var json = JsonSerializer.Serialize(envelope);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Envelope_HasExpectedTopLevelFields()
    {
        var svc = BuildService();
        var rollup = await svc.GetRollupAsync();

        var root = Roundtrip(rollup);
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("asOfUtc", out _));
        Assert.True(root.TryGetProperty("cacheTtlSeconds", out _));
        Assert.True(root.TryGetProperty("portfolio", out var portfolio));
        Assert.True(root.TryGetProperty("bots", out var bots));
        Assert.True(root.TryGetProperty("crossBotEdges", out var edges));

        // portfolio summary block
        Assert.True(portfolio.TryGetProperty("operator", out _));
        Assert.True(portfolio.TryGetProperty("website", out _));
        Assert.True(portfolio.TryGetProperty("totalBots", out var totalBots));
        Assert.Equal(15, totalBots.GetInt32());
        Assert.True(portfolio.TryGetProperty("totalPaidOfferings", out _));
        Assert.True(portfolio.TryGetProperty("totalFreeResources", out _));
        Assert.True(portfolio.TryGetProperty("totalSubscriptions", out _));

        Assert.Equal(JsonValueKind.Array, bots.ValueKind);
        Assert.Equal(15, bots.GetArrayLength());

        Assert.Equal(JsonValueKind.Array, edges.ValueKind);
        Assert.True(edges.GetArrayLength() > 0);
    }

    [Fact]
    public async Task EveryBot_HasRequiredFields()
    {
        var svc = BuildService();
        var rollup = await svc.GetRollupAsync();
        var root = Roundtrip(rollup);

        var bots = root.GetProperty("bots");
        Assert.Equal(15, bots.GetArrayLength());

        foreach (var bot in bots.EnumerateArray())
        {
            Assert.True(bot.TryGetProperty("slug", out var slug));
            Assert.False(string.IsNullOrWhiteSpace(slug.GetString()));

            Assert.True(bot.TryGetProperty("displayName", out _));

            Assert.True(bot.TryGetProperty("agentAddress", out var addr));
            var addrStr = addr.GetString() ?? "";
            Assert.StartsWith("0x", addrStr);
            Assert.Equal(42, addrStr.Length);

            Assert.True(bot.TryGetProperty("agentId", out _));
            Assert.True(bot.TryGetProperty("chains", out var chains));
            Assert.Equal(JsonValueKind.Array, chains.ValueKind);
            Assert.True(chains.GetArrayLength() >= 1);

            Assert.True(bot.TryGetProperty("category", out _));
            Assert.True(bot.TryGetProperty("marketplaceUrl", out var mkt));
            Assert.StartsWith("https://app.virtuals.io/acp/agents/", mkt.GetString()!);

            Assert.True(bot.TryGetProperty("website", out var website));
            Assert.StartsWith("https://acp-metabot.dev/portfolio/", website.GetString()!);

            Assert.True(bot.TryGetProperty("resourcesBaseUrl", out var rb));
            Assert.StartsWith("https://api.acp-metabot.dev/", rb.GetString()!);
            Assert.EndsWith("/v1/resources", rb.GetString()!);

            Assert.True(bot.TryGetProperty("witnessedCatalogueUrl", out var wc));
            Assert.EndsWith("/v1/resources/witnessedCatalogue", wc.GetString()!);

            Assert.True(bot.TryGetProperty("offeringCount", out var oc));
            Assert.True(oc.GetInt32() >= 0);

            Assert.True(bot.TryGetProperty("resourceCount", out var rc));
            Assert.True(rc.GetInt32() >= 0);

            Assert.True(bot.TryGetProperty("subscriptionTierCount", out _));
            Assert.True(bot.TryGetProperty("synthetic", out _));
            Assert.True(bot.TryGetProperty("lastSeenAt", out _));
            Assert.True(bot.TryGetProperty("v1Note", out _));
        }
    }

    [Fact]
    public async Task PortfolioTotals_SumPerBotCounts()
    {
        var svc = BuildService();
        var rollup = await svc.GetRollupAsync();
        var root = Roundtrip(rollup);

        var bots = root.GetProperty("bots");
        int sumOff = 0;
        int sumRes = 0;
        int sumSub = 0;
        foreach (var bot in bots.EnumerateArray())
        {
            sumOff += bot.GetProperty("offeringCount").GetInt32();
            sumRes += bot.GetProperty("resourceCount").GetInt32();
            sumSub += bot.GetProperty("subscriptionTierCount").GetInt32();
        }

        var portfolio = root.GetProperty("portfolio");
        Assert.Equal(sumOff, portfolio.GetProperty("totalPaidOfferings").GetInt32());
        Assert.Equal(sumRes, portfolio.GetProperty("totalFreeResources").GetInt32());
        Assert.Equal(sumSub, portfolio.GetProperty("totalSubscriptions").GetInt32());
    }

    [Fact]
    public async Task CrossBotEdges_AreWellFormed()
    {
        var svc = BuildService();
        var rollup = await svc.GetRollupAsync();
        var root = Roundtrip(rollup);

        var slugs = root.GetProperty("bots").EnumerateArray()
            .Select(b => b.GetProperty("slug").GetString()!)
            .ToHashSet();

        var edges = root.GetProperty("crossBotEdges");
        Assert.True(edges.GetArrayLength() > 0);
        foreach (var edge in edges.EnumerateArray())
        {
            var producer = edge.GetProperty("producer").GetString()!;
            var consumer = edge.GetProperty("consumer").GetString()!;
            Assert.Contains(producer, slugs);
            Assert.Contains(consumer, slugs);
            Assert.True(edge.TryGetProperty("via", out _));
            Assert.True(edge.TryGetProperty("verified", out var verified));
            Assert.True(verified.ValueKind == JsonValueKind.True
                     || verified.ValueKind == JsonValueKind.False);
        }
    }

    [Fact]
    public async Task Cache_ReturnsSameInstance_OnSubsequentCalls()
    {
        var svc = BuildService();
        var first = await svc.GetRollupAsync();
        var second = await svc.GetRollupAsync();
        // Identity check — second call should hit cache and return same ref.
        Assert.Same(first, second);
    }

    [Fact]
    public async Task SlugsAreUnique()
    {
        var svc = BuildService();
        var rollup = await svc.GetRollupAsync();
        var root = Roundtrip(rollup);

        var slugs = root.GetProperty("bots").EnumerateArray()
            .Select(b => b.GetProperty("slug").GetString()!)
            .ToList();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    [Fact]
    public async Task LiveOfferingCount_OverridesHardcoded_WhenIndexHasRows()
    {
        // Seed 2 active offerings against TheMetaBot's known address so the
        // service swaps its hardcoded count (19) for the live count (2).
        const string addr = "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c";
        await using (var conn = _db.OpenConnection())
        {
            for (int i = 0; i < 2; i++)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                        price_usdc, price_type, chain, content_hash,
                        first_seen_at, last_seen_at, marketplace_version, is_removed)
                    VALUES ($a, 'TheMetaBot', $o, 'desc', 0.05, 'fixed', 'base', $h,
                            '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z', 'v2', 0);";
                cmd.Parameters.AddWithValue("$a", addr);
                cmd.Parameters.AddWithValue("$o", $"smoke_{i}");
                cmd.Parameters.AddWithValue("$h", $"h_{Guid.NewGuid():N}");
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var svc = BuildService();
        var rollup = await svc.GetRollupAsync();
        var root = Roundtrip(rollup);

        var meta = root.GetProperty("bots").EnumerateArray()
            .First(b => b.GetProperty("slug").GetString() == "metabot");
        Assert.Equal(2, meta.GetProperty("offeringCount").GetInt32());
    }
}
