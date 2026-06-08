using System.Globalization;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class SecurityVerdictRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;

    public SecurityVerdictRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_secverdict_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new SecurityVerdictRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static SecurityVerdict Scanned(string addr, string scannedAtIso, int score = 90) =>
        new(addr, SecurityStatus.Scanned, score, "A", 11, 0, "{}", scannedAtIso, "2026-06-08", null);

    // Insert a minimal active offering so GetStaleAgents' candidate join finds the agent.
    private async Task InsertOfferingAsync(string agentAddress, string lastSeenIso, long usageCount = 0)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings
                (agent_address, agent_name, offering_name, description, price_usdc,
                 price_type, chain, content_hash, first_seen_at, last_seen_at, usage_count, is_removed)
            VALUES ($a, 'n', 'o-' || $a, 'd', 1.0, 'per_call', 'base', $a || $ls, $ls, $ls, $u, 0);";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        cmd.Parameters.AddWithValue("$ls", lastSeenIso);
        cmd.Parameters.AddWithValue("$u", usageCount);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Upsert_ThenGetByAgent_Roundtrips_AndLowercases()
    {
        var iso = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        await _repo.UpsertAsync(Scanned("0xABCDEF", iso));

        var row = await _repo.GetByAgentAsync("0xabcdef");
        Assert.NotNull(row);
        Assert.Equal("0xabcdef", row!.AgentAddress); // lower-cased on write
        Assert.Equal(SecurityStatus.Scanned, row.Status);
        Assert.Equal(90, row.Score);
        Assert.Equal("A", row.Grade);
    }

    [Fact]
    public async Task Upsert_Twice_OverwritesNotDuplicates()
    {
        var iso1 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        var iso2 = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        await _repo.UpsertAsync(Scanned("0xa", iso1, score: 70));
        await _repo.UpsertAsync(Scanned("0xa", iso2, score: 95));

        var row = await _repo.GetByAgentAsync("0xa");
        Assert.Equal(95, row!.Score);
        Assert.Equal(iso2, row.ScannedAt);
    }

    [Fact]
    public async Task GetMany_ReturnsOnlyRequested_KeyedLowercase()
    {
        var iso = DateTime.UtcNow.ToString("O");
        await _repo.UpsertAsync(Scanned("0xa", iso));
        await _repo.UpsertAsync(Scanned("0xb", iso));

        var map = await _repo.GetManyAsync(new[] { "0xA", "0xC" });
        Assert.True(map.ContainsKey("0xa"));
        Assert.False(map.ContainsKey("0xb"));
        Assert.False(map.ContainsKey("0xc"));
    }

    [Fact]
    public async Task GetMany_EmptyInput_ReturnsEmpty()
    {
        var map = await _repo.GetManyAsync(Array.Empty<string>());
        Assert.Empty(map);
    }

    [Fact]
    public async Task GetStaleAgents_NeverScanned_Selected_HighestHiresFirst()
    {
        var now = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
        var seen = now.AddDays(-1).ToString("O");
        await InsertOfferingAsync("0xlow", seen, usageCount: 1);
        await InsertOfferingAsync("0xhigh", seen, usageCount: 100);

        var stale = await _repo.GetStaleAgentsAsync(
            now, activeWindowDays: 30,
            scannedTtl: TimeSpan.FromDays(7),
            notAuditableTtl: TimeSpan.FromDays(30),
            errorTtl: TimeSpan.FromHours(6),
            limit: 10);

        Assert.Equal(new[] { "0xhigh", "0xlow" }, stale); // never-scanned, higher traction first
    }

    [Fact]
    public async Task GetStaleAgents_FreshScanned_Excluded_StaleScanned_Included()
    {
        var now = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
        var seen = now.AddDays(-1).ToString("O");
        await InsertOfferingAsync("0xfresh", seen);
        await InsertOfferingAsync("0xstale", seen);
        await _repo.UpsertAsync(Scanned("0xfresh", now.AddDays(-1).ToString("O")));   // 1 day old < 7d TTL
        await _repo.UpsertAsync(Scanned("0xstale", now.AddDays(-10).ToString("O")));  // 10 days old > 7d TTL

        var stale = await _repo.GetStaleAgentsAsync(
            now, 30, TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromHours(6), 10);

        Assert.Contains("0xstale", stale);
        Assert.DoesNotContain("0xfresh", stale);
    }

    [Fact]
    public async Task GetStaleAgents_ErrorRow_ShortTtl_RetriesSooner()
    {
        var now = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
        var seen = now.AddDays(-1).ToString("O");
        await InsertOfferingAsync("0xerr", seen);
        await _repo.UpsertAsync(new SecurityVerdict(
            "0xerr", SecurityStatus.Error, null, null, null, null, null,
            now.AddHours(-7).ToString("O"), null, "HTTP 500")); // 7h old > 6h error TTL

        var stale = await _repo.GetStaleAgentsAsync(
            now, 30, TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromHours(6), 10);

        Assert.Contains("0xerr", stale);
    }

    [Fact]
    public async Task GetStaleAgents_RespectsLimit()
    {
        var now = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
        var seen = now.AddDays(-1).ToString("O");
        for (int i = 0; i < 5; i++) await InsertOfferingAsync($"0x{i}", seen);

        var stale = await _repo.GetStaleAgentsAsync(
            now, 30, TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromHours(6), limit: 2);

        Assert.Equal(2, stale.Count);
    }
}
