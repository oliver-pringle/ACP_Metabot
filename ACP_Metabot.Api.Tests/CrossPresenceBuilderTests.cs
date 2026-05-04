using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class CrossPresenceBuilderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _offerings;
    private readonly CrossPresenceBuilder _builder;

    public CrossPresenceBuilderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_crosspresence_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _offerings = new OfferingRepository(_db);
        _builder = new CrossPresenceBuilder(_offerings);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task SeedAsync(params (string addr, string name, string marketplace, string firstSeen, bool removed)[] rows)
    {
        await using var conn = _db.OpenConnection();
        foreach (var r in rows)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                    price_usdc, price_type, chain, content_hash,
                    first_seen_at, last_seen_at, marketplace_version, is_removed)
                VALUES ($a, $n, $o, 'desc', 0.10, 'per_call', 'base', $h, $f, $f, $m, $r)";
            cmd.Parameters.AddWithValue("$a", r.addr.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$n", r.name);
            cmd.Parameters.AddWithValue("$o", $"off_{Guid.NewGuid():N}");
            cmd.Parameters.AddWithValue("$h", $"h_{Guid.NewGuid():N}");
            cmd.Parameters.AddWithValue("$f", r.firstSeen);
            cmd.Parameters.AddWithValue("$m", r.marketplace);
            cmd.Parameters.AddWithValue("$r", r.removed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BothMarketplaces_DominantByOfferingCount()
    {
        const string addr = "0xaaaa";
        await SeedAsync(
            (addr, "AgentA", "v1", "2026-01-01T00:00:00Z", false),
            (addr, "AgentA", "v1", "2026-01-02T00:00:00Z", false),
            (addr, "AgentA", "v1", "2026-01-03T00:00:00Z", false),
            (addr, "AgentA", "v2", "2026-01-04T00:00:00Z", false),
            (addr, "AgentA", "v2", "2026-01-05T00:00:00Z", false));

        var result = await _builder.BuildAsync(addr);

        Assert.NotNull(result.V1);
        Assert.NotNull(result.V2);
        Assert.Equal(3, result.V1!.OfferingCount);
        Assert.Equal(2, result.V2!.OfferingCount);
        Assert.True(result.InBoth);
        Assert.Equal("v1", result.Dominant);
    }

    [Fact]
    public async Task SingleMarketplace_OtherIsNull()
    {
        const string addr = "0xbbbb";
        await SeedAsync(
            (addr, "AgentB", "v2", "2026-02-01T00:00:00Z", false));

        var result = await _builder.BuildAsync(addr);

        Assert.Null(result.V1);
        Assert.NotNull(result.V2);
        Assert.Equal(1, result.V2!.OfferingCount);
        Assert.False(result.InBoth);
        Assert.Equal("v2", result.Dominant);
    }

    [Fact]
    public async Task TombstonedOfferings_Excluded()
    {
        const string addr = "0xcccc";
        await SeedAsync(
            (addr, "AgentC", "v1", "2026-03-01T00:00:00Z", true),   // tombstoned — must not count
            (addr, "AgentC", "v2", "2026-03-02T00:00:00Z", false));  // active

        var result = await _builder.BuildAsync(addr);

        Assert.Null(result.V1);
        Assert.NotNull(result.V2);
        Assert.Equal(1, result.V2!.OfferingCount);
        Assert.False(result.InBoth);
        Assert.Equal("v2", result.Dominant);
    }

    [Fact]
    public async Task TiedOfferingCount_DominantTied()
    {
        const string addr = "0xdddd";
        await SeedAsync(
            (addr, "AgentD", "v1", "2026-04-01T00:00:00Z", false),
            (addr, "AgentD", "v2", "2026-04-02T00:00:00Z", false));

        var result = await _builder.BuildAsync(addr);

        Assert.NotNull(result.V1);
        Assert.NotNull(result.V2);
        Assert.Equal(1, result.V1!.OfferingCount);
        Assert.Equal(1, result.V2!.OfferingCount);
        Assert.True(result.InBoth);
        Assert.Equal("tied", result.Dominant);
    }
}
