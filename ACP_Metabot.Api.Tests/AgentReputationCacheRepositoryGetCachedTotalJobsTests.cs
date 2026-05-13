using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

// v1.7.5: GetCachedTotalJobsAsync surfaces the warmer-cached on-chain total
// (raw_counts_json.totalJobs) so AcpV2MarketplaceSource can stamp it onto
// every V2 offering's agent_job_count column during the indexer pass.
public class AgentReputationCacheRepositoryGetCachedTotalJobsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly AgentReputationCacheRepository _repo;

    public AgentReputationCacheRepositoryGetCachedTotalJobsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_cached_jobs_test_{Guid.NewGuid():N}.db");
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

    private async Task UpsertCacheRowAsync(
        string agentAddress, long totalJobs, DateTime? computedAt = null)
    {
        var rawCounts = new RawCounts(
            TotalJobs:        totalJobs,
            Completed:        Math.Max(0, totalJobs - 1),
            Rejected:         0,
            Expired:          0,
            CompletedLast30d: Math.Max(0, totalJobs - 1),
            LastActiveAt:     null);
        await _repo.UpsertAsync(new CachedReputationRow(
            AgentAddress:     agentAddress.ToLowerInvariant(),
            AgentName:        "Agent " + agentAddress[..8],
            AgentScore:       50,
            SubScoresJson:    "{}",
            RawCountsJson:    System.Text.Json.JsonSerializer.Serialize(rawCounts),
            FlagsJson:        "{}",
            ComputedAt:       computedAt ?? DateTime.UtcNow,
            LastScannedBlock: 1_000_000,
            Source:           "chain"));
    }

    [Fact]
    public async Task ReturnsTotalJobsFromCachedRow()
    {
        await UpsertCacheRowAsync("0xaaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1", totalJobs: 42);

        var result = await _repo.GetCachedTotalJobsAsync(
            "0xaaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1");

        Assert.Equal(42L, result);
    }

    [Fact]
    public async Task ReturnsNullWhenAgentNotCached()
    {
        var result = await _repo.GetCachedTotalJobsAsync(
            "0xbbb2bbb2bbb2bbb2bbb2bbb2bbb2bbb2bbb2bbb2");

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsZeroWhenAgentCachedWithNoJobs()
    {
        // Cold-start agent that's been scanned but has no on-chain jobs.
        // We want to distinguish "not cached" (null) from "cached as 0" (0).
        await UpsertCacheRowAsync("0xccc3ccc3ccc3ccc3ccc3ccc3ccc3ccc3ccc3ccc3", totalJobs: 0);

        var result = await _repo.GetCachedTotalJobsAsync(
            "0xccc3ccc3ccc3ccc3ccc3ccc3ccc3ccc3ccc3ccc3");

        Assert.Equal(0L, result);
    }

    [Fact]
    public async Task IgnoresTtl_ReturnsStaleCount()
    {
        // Lookup is intentionally TTL-bypassing — a 7-day-stale count of 50
        // is strictly better than 0 for marketplace ranking, and the warmer
        // re-runs nightly so the row will refresh on its own.
        await UpsertCacheRowAsync(
            "0xddd4ddd4ddd4ddd4ddd4ddd4ddd4ddd4ddd4ddd4",
            totalJobs: 17,
            computedAt: DateTime.UtcNow.AddDays(-7));

        var result = await _repo.GetCachedTotalJobsAsync(
            "0xddd4ddd4ddd4ddd4ddd4ddd4ddd4ddd4ddd4ddd4");

        Assert.Equal(17L, result);
    }

    [Fact]
    public async Task ReturnsNullWhenJsonMalformed()
    {
        // Direct insert of a malformed raw_counts_json — deserialisation
        // should swallow the JsonException and return null, NOT throw.
        await using (var conn = _db.OpenConnection())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO agent_reputation_cache
                    (agent_address, agent_name, agent_score, sub_scores_json,
                     raw_counts_json, flags_json, computed_at, last_scanned_block, source)
                VALUES ('0xeee5eee5eee5eee5eee5eee5eee5eee5eee5eee5', 'BadJsonAgent', 50, '{}',
                        '{not valid json', '{}', $c, 1000, 'chain');";
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await _repo.GetCachedTotalJobsAsync(
            "0xeee5eee5eee5eee5eee5eee5eee5eee5eee5eee5");

        Assert.Null(result);
    }
}
