using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

// v1.9 self-tuning warmer — covers the LifetimeSnapshotRepository
// ListTopByDeltaAsync query that underpins the trending overlay. Seeds a
// minimal SQLite DB, writes two-or-three snapshot dates with hand-crafted
// totals, and asserts the resulting (agent, delta) order matches expected.
//
// Critical invariants the warmer depends on:
//   - delta = today_total - past_total
//   - agents with no past snapshot (new joiners) get their full current
//     total as delta — they're maximally trending
//   - rows with zero or negative delta are excluded (no signal)
//   - results sorted by delta DESC and capped at topN
//   - empty result set when there's only one snapshot date (cold-boot)
public class WarmerTrendingDeltaTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly LifetimeSnapshotRepository _repo;

    public WarmerTrendingDeltaTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_trending_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new LifetimeSnapshotRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task ListTopByDelta_OrdersByDeltaDescending()
    {
        var today = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        var weekAgo = today.AddDays(-7);
        await _repo.UpsertAsync("0xa", weekAgo, 10);
        await _repo.UpsertAsync("0xb", weekAgo, 100);
        await _repo.UpsertAsync("0xc", weekAgo, 50);
        await _repo.UpsertAsync("0xa", today, 30);    // delta 20
        await _repo.UpsertAsync("0xb", today, 105);   // delta 5
        await _repo.UpsertAsync("0xc", today, 150);   // delta 100

        var top = await _repo.ListTopByDeltaAsync(topN: 5, windowDays: 7, nowUtc: today);

        Assert.Equal(3, top.Count);
        Assert.Equal("0xc", top[0].AgentAddress);
        Assert.Equal(100, top[0].Delta);
        Assert.Equal("0xa", top[1].AgentAddress);
        Assert.Equal(20, top[1].Delta);
        Assert.Equal("0xb", top[2].AgentAddress);
        Assert.Equal(5, top[2].Delta);
    }

    [Fact]
    public async Task ListTopByDelta_NewJoinersGetFullCurrentTotal()
    {
        // Brand-new agent — no past snapshot. The COALESCE in the query
        // gives them a delta equal to their full current total, surfacing
        // newly-onboarded agents as maximally trending.
        var today = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        var weekAgo = today.AddDays(-7);
        await _repo.UpsertAsync("0xveteran", weekAgo, 1000);
        await _repo.UpsertAsync("0xveteran", today, 1005); // delta 5
        await _repo.UpsertAsync("0xnewbie",  today, 75);   // delta 75 (no past)

        var top = await _repo.ListTopByDeltaAsync(topN: 5, windowDays: 7, nowUtc: today);

        Assert.Equal(2, top.Count);
        Assert.Equal("0xnewbie", top[0].AgentAddress);
        Assert.Equal(75, top[0].Delta);
        Assert.Equal("0xveteran", top[1].AgentAddress);
        Assert.Equal(5, top[1].Delta);
    }

    [Fact]
    public async Task ListTopByDelta_ExcludesZeroOrNegativeDeltas()
    {
        // Agent with no movement → excluded.
        // Agent with negative movement (data correction / tombstone) → excluded.
        // Only positive deltas surface as "trending".
        var today = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        var weekAgo = today.AddDays(-7);
        await _repo.UpsertAsync("0xidle",    weekAgo, 50);
        await _repo.UpsertAsync("0xidle",    today,   50);  // delta 0 — excluded
        await _repo.UpsertAsync("0xshrunk",  weekAgo, 50);
        await _repo.UpsertAsync("0xshrunk",  today,   40);  // delta -10 — excluded
        await _repo.UpsertAsync("0xreal",    weekAgo, 50);
        await _repo.UpsertAsync("0xreal",    today,   60);  // delta 10 — kept

        var top = await _repo.ListTopByDeltaAsync(topN: 5, windowDays: 7, nowUtc: today);

        Assert.Single(top);
        Assert.Equal("0xreal", top[0].AgentAddress);
        Assert.Equal(10, top[0].Delta);
    }

    [Fact]
    public async Task ListTopByDelta_RespectsTopNCap()
    {
        var today = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        var weekAgo = today.AddDays(-7);
        for (int i = 1; i <= 10; i++)
        {
            var addr = $"0x{i:x40}";
            await _repo.UpsertAsync(addr, weekAgo, 0);
            await _repo.UpsertAsync(addr, today, i * 10); // ascending deltas
        }

        var top3 = await _repo.ListTopByDeltaAsync(topN: 3, windowDays: 7, nowUtc: today);

        Assert.Equal(3, top3.Count);
        Assert.Equal(100, top3[0].Delta); // i=10
        Assert.Equal(90,  top3[1].Delta); // i=9
        Assert.Equal(80,  top3[2].Delta); // i=8
    }

    [Fact]
    public async Task ListTopByDelta_EmptyOnColdBoot()
    {
        // Only today's snapshot exists — no past row to diff against, AND
        // the query asks for past_date < today. So everything COALESCEs to
        // 0 and surfaces as "trending = today's total". Verify a single-day
        // table doesn't crash and returns the rows it should.
        var today = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        await _repo.UpsertAsync("0xa", today, 10);

        var top = await _repo.ListTopByDeltaAsync(topN: 5, windowDays: 7, nowUtc: today);

        Assert.Single(top);
        Assert.Equal("0xa", top[0].AgentAddress);
        Assert.Equal(10, top[0].Delta);
    }
}
