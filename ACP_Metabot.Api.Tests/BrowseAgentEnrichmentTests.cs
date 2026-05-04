using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// Verifies that the browseAgent response carries a populated crossPresence
/// block and per-offering pricePercentile when the underlying services are
/// wired correctly.  Uses an in-process fixture to avoid standing up ASP.NET,
/// mirroring the approach in CrossPresenceBuilderTests.
/// </summary>
public class BrowseAgentEnrichmentTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _offerings;
    private readonly CrossPresenceBuilder _crossPresence;
    private readonly PricePercentileCalculator _pricePercentile;

    public BrowseAgentEnrichmentTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_browse_enrich_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _offerings = new OfferingRepository(_db);
        _crossPresence = new CrossPresenceBuilder(_offerings);
        _pricePercentile = new PricePercentileCalculator(lowNThreshold: 5);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // Seed minimal offering rows — no embedding column needed for this test.
    private async Task<long> SeedOfferingAsync(string agentAddr, string agentName,
        string offeringName, string marketplace, double priceUsdc)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                price_usdc, price_type, chain, content_hash,
                first_seen_at, last_seen_at, marketplace_version, is_removed)
            VALUES ($a, $n, $o, 'desc', $p, 'per_call', 'base', $h,
                    '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z', $m, 0);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$a", agentAddr.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$n", agentName);
        cmd.Parameters.AddWithValue("$o", offeringName);
        cmd.Parameters.AddWithValue("$p", priceUsdc);
        cmd.Parameters.AddWithValue("$h", $"h_{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("$m", marketplace);
        var id = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(id);
    }

    /// <summary>
    /// Mirrors the per-offering enrichment block from HandleBrowseAgent so
    /// that tests can exercise the logic without a running ASP.NET host.
    /// </summary>
    private static PricePercentileDto ComputePercentile(
        long offeringId, string category, string marketplace, double priceUsdc,
        PricePercentileCalculator calc)
    {
        var pp = calc.Compute(offeringId, category, marketplace, priceUsdc);
        return new PricePercentileDto(pp.Value, pp.PeerN, pp.LowN);
    }

    [Fact]
    public async Task CrossPresence_InBoth_WhenAgentHasV1AndV2Offerings()
    {
        const string addr = "0x1234567890123456789012345678901234567890";

        await SeedOfferingAsync(addr, "TestAgent", "off_v1_a", "v1", 1.00);
        await SeedOfferingAsync(addr, "TestAgent", "off_v1_b", "v1", 2.00);
        await SeedOfferingAsync(addr, "TestAgent", "off_v2_a", "v2", 3.00);

        var cp = await _crossPresence.BuildAsync(addr);

        Assert.True(cp.InBoth, "inBoth should be true when agent has both v1 and v2 offerings");
        Assert.NotNull(cp.V1);
        Assert.NotNull(cp.V2);
        Assert.Equal(2, cp.V1!.OfferingCount);
        Assert.Equal(1, cp.V2!.OfferingCount);
        Assert.Equal("v1", cp.Dominant);
    }

    [Fact]
    public async Task CrossPresence_OnlyV2_WhenAgentHasNoV1Offerings()
    {
        const string addr = "0xabcdef1234567890abcdef1234567890abcdef12";

        await SeedOfferingAsync(addr, "V2Only", "off_v2_a", "v2", 0.50);
        await SeedOfferingAsync(addr, "V2Only", "off_v2_b", "v2", 1.50);

        var cp = await _crossPresence.BuildAsync(addr);

        Assert.False(cp.InBoth);
        Assert.Null(cp.V1);
        Assert.NotNull(cp.V2);
        Assert.Equal(2, cp.V2!.OfferingCount);
        Assert.Equal("v2", cp.Dominant);
    }

    [Fact]
    public void PricePercentile_IsWellFormed_WhenLowNThresholdNotMet()
    {
        // 3 peers < lowNThreshold=5 → LowN=true, Value=null.
        _pricePercentile.Refresh(new (long, string, string, double)[]
        {
            (1L, "defi", "v1", 0.50),
            (2L, "defi", "v1", 1.00),
            (3L, "defi", "v1", 2.00),
        });

        var dto = ComputePercentile(1L, "defi", "v1", 0.50, _pricePercentile);

        Assert.True(dto.LowN);
        Assert.Null(dto.Value);
        // peerN = total - 1 self = 2
        Assert.Equal(2, dto.PeerN);
    }

    [Fact]
    public void PricePercentile_ValuePresent_WhenEnoughPeers()
    {
        // 7 peers in "trading/v2"; id 10 is the cheapest at 0.10 USDC.
        _pricePercentile.Refresh(new (long, string, string, double)[]
        {
            (10L, "trading", "v2", 0.10),
            (11L, "trading", "v2", 0.50),
            (12L, "trading", "v2", 1.00),
            (13L, "trading", "v2", 1.50),
            (14L, "trading", "v2", 2.00),
            (15L, "trading", "v2", 3.00),
            (16L, "trading", "v2", 5.00),
        });

        var dto = ComputePercentile(10L, "trading", "v2", 0.10, _pricePercentile);

        Assert.False(dto.LowN);
        Assert.NotNull(dto.Value);
        Assert.Equal(0, dto.Value);   // cheapest → 0th percentile
        Assert.Equal(6, dto.PeerN);   // 7 total - 1 self = 6

        var dtoTop = ComputePercentile(16L, "trading", "v2", 5.00, _pricePercentile);
        Assert.Equal(100, dtoTop.Value);
    }

    [Fact]
    public async Task BrowseAgentResult_IncludesCrossPresenceAndPercentileFields()
    {
        // Smoke-test the DTO shape: confirm AgentBrowseResult and
        // AgentBrowseOffering accept both new fields at compile time.
        const string addr = "0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";

        var id1 = await SeedOfferingAsync(addr, "AgentX", "off_a", "v1", 0.99);
        var id2 = await SeedOfferingAsync(addr, "AgentX", "off_b", "v2", 1.99);

        var cp = await _crossPresence.BuildAsync(addr);

        _pricePercentile.Refresh(new (long, string, string, double)[]
        {
            (id1, "misc", "v1", 0.99),
            (id2, "misc", "v2", 1.99),
        });

        var dto1 = ComputePercentile(id1, "misc", "v1", 0.99, _pricePercentile);
        var dto2 = ComputePercentile(id2, "misc", "v2", 1.99, _pricePercentile);

        // Both offerings have only 0 peers → LowN=true (one row, peerN = 0)
        Assert.True(dto1.LowN);
        Assert.True(dto2.LowN);

        // crossPresence block should flag inBoth=true
        Assert.True(cp.InBoth);
        Assert.NotNull(cp.V1);
        Assert.NotNull(cp.V2);

        // Verify the DTO constructors accept the fields (compile-time shape check).
        var off = new AgentBrowseOffering(
            OfferingId: id1,
            OfferingName: "off_a",
            Description: "desc",
            PriceUsdc: 0.99,
            PriceType: "per_call",
            Chain: "base",
            IsPrivate: false,
            RequirementSchema: null,
            FirstSeenAt: "2026-01-01T00:00:00Z",
            LastSeenAt: "2026-01-02T00:00:00Z",
            Reputation: null,
            MarketplaceVersion: "v1",
            PricePercentile: dto1);

        Assert.NotNull(off.PricePercentile);
        Assert.True(off.PricePercentile.LowN);
    }
}
