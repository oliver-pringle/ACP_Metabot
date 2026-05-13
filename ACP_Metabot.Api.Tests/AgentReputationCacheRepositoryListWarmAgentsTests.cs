using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

// v1.7.3: ListWarmAgentsAsync filters to V2-active agents and orders by
// V2 hire count. Prior query (MAX(agent_job_count) DESC) returned V1-only
// agents with high V1 job counts but zero V2 chain activity, so every
// warmer compute came back as cold-start.
public class AgentReputationCacheRepositoryListWarmAgentsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly AgentReputationCacheRepository _repo;

    public AgentReputationCacheRepositoryListWarmAgentsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_listwarm_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new AgentReputationCacheRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task SeedOfferingAsync(
        string agentAddress, string agentName, string offeringName,
        string marketplaceVersion, long usageCount, long agentJobCount,
        bool isRemoved = false)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                price_usdc, price_type, chain, content_hash,
                first_seen_at, last_seen_at, usage_count, agent_job_count,
                marketplace_version, is_removed)
            VALUES ($a, $n, $o, 'desc', 0.10, 'per_call', 'base', $h,
                $f, $f, $u, $j, $m, $r)";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$n", agentName);
        cmd.Parameters.AddWithValue("$o", offeringName);
        cmd.Parameters.AddWithValue("$h", $"h_{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("$f", "2026-05-13T00:00:00Z");
        cmd.Parameters.AddWithValue("$u", usageCount);
        cmd.Parameters.AddWithValue("$j", agentJobCount);
        cmd.Parameters.AddWithValue("$m", marketplaceVersion);
        cmd.Parameters.AddWithValue("$r", isRemoved ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ExcludesV1OnlyAgents()
    {
        // V1-only agent with a big job count — should NOT appear.
        await SeedOfferingAsync("0xv1only", "V1Agent", "v1_off",
            marketplaceVersion: "v1", usageCount: 0, agentJobCount: 1_000_000);
        // V2 agent with one offering, zero hires — SHOULD appear.
        await SeedOfferingAsync("0xv2agent", "V2Agent", "v2_off",
            marketplaceVersion: "v2", usageCount: 0, agentJobCount: 0);

        var result = await _repo.ListWarmAgentsAsync(topN: 10);

        Assert.Single(result);
        Assert.Equal("0xv2agent", result[0].AgentAddress);
        Assert.Equal("V2Agent", result[0].AgentName);
    }

    [Fact]
    public async Task OrdersV2AgentsByV2HireCount()
    {
        await SeedOfferingAsync("0xv2low",  "V2Low",  "off_a", "v2", usageCount: 5,   agentJobCount: 0);
        await SeedOfferingAsync("0xv2high", "V2High", "off_b", "v2", usageCount: 100, agentJobCount: 0);
        await SeedOfferingAsync("0xv2mid",  "V2Mid",  "off_c", "v2", usageCount: 25,  agentJobCount: 0);

        var result = await _repo.ListWarmAgentsAsync(topN: 10);

        Assert.Equal(3, result.Count);
        Assert.Equal("0xv2high", result[0].AgentAddress);
        Assert.Equal("0xv2mid",  result[1].AgentAddress);
        Assert.Equal("0xv2low",  result[2].AgentAddress);
    }

    [Fact]
    public async Task SumsHiresAcrossMultipleV2Offerings()
    {
        // Agent with two V2 offerings — total 110 hires.
        await SeedOfferingAsync("0xcombined", "Combined", "off_1", "v2", usageCount: 60,  agentJobCount: 0);
        await SeedOfferingAsync("0xcombined", "Combined", "off_2", "v2", usageCount: 50,  agentJobCount: 0);
        // Single-offering V2 agent with 100 hires.
        await SeedOfferingAsync("0xsingle",   "Single",   "off_3", "v2", usageCount: 100, agentJobCount: 0);

        var result = await _repo.ListWarmAgentsAsync(topN: 10);

        Assert.Equal(2, result.Count);
        // Combined (110) beats Single (100) because we SUM not MAX.
        Assert.Equal("0xcombined", result[0].AgentAddress);
        Assert.Equal("0xsingle",   result[1].AgentAddress);
    }

    [Fact]
    public async Task TieBreaksByV1JobCount()
    {
        // Two V2 agents with the same V2 hires (0) — tie-break on V1 job count.
        await SeedOfferingAsync("0xtiebroken", "Tiebroken", "v1_a", "v1", usageCount: 0, agentJobCount: 500);
        await SeedOfferingAsync("0xtiebroken", "Tiebroken", "v2_a", "v2", usageCount: 0, agentJobCount: 0);
        await SeedOfferingAsync("0xother",     "Other",     "v2_b", "v2", usageCount: 0, agentJobCount: 0);

        var result = await _repo.ListWarmAgentsAsync(topN: 10);

        Assert.Equal(2, result.Count);
        // Tiebroken has V1 job count 500 vs Other's 0 — sorts first.
        Assert.Equal("0xtiebroken", result[0].AgentAddress);
        Assert.Equal("0xother",     result[1].AgentAddress);
    }

    [Fact]
    public async Task ExcludesTombstonedOfferings()
    {
        // Agent's only V2 offering is tombstoned — should NOT appear.
        await SeedOfferingAsync("0xremoved", "Removed", "off_x", "v2",
            usageCount: 99, agentJobCount: 0, isRemoved: true);
        await SeedOfferingAsync("0xlive",    "Live",    "off_y", "v2",
            usageCount: 1, agentJobCount: 0);

        var result = await _repo.ListWarmAgentsAsync(topN: 10);

        Assert.Single(result);
        Assert.Equal("0xlive", result[0].AgentAddress);
    }

    [Fact]
    public async Task RespectsTopNLimit()
    {
        for (int i = 0; i < 5; i++)
        {
            await SeedOfferingAsync($"0xa{i}", $"Agent{i}", $"off_{i}", "v2",
                usageCount: 100 - i, agentJobCount: 0);
        }

        var result = await _repo.ListWarmAgentsAsync(topN: 3);

        Assert.Equal(3, result.Count);
        Assert.Equal("0xa0", result[0].AgentAddress);
        Assert.Equal("0xa1", result[1].AgentAddress);
        Assert.Equal("0xa2", result[2].AgentAddress);
    }
}
