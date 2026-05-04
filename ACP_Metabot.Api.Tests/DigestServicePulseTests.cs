using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// Unit tests for the v1.7 digest pulse fields: newAgents, churnRate,
/// cohortSurvival, windowStart, and the hourly cache.
/// Uses per-test temp SQLite DB and the real DigestService + OfferingRepository.
/// </summary>
public class DigestServicePulseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _repo;
    private readonly DigestService _svc;

    public DigestServicePulseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_digest_pulse_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();

        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new OfferingRepository(_db);

        var satCalc = new SaturationCalculator(threshold: 0.85);
        // ReputationService: only BuildSearchSummary is used by DigestService;
        // chain scanner / off-chain client are never called — pass null! safely.
        var repSvc = new ReputationService(
            new AgentReputationCacheRepository(_db),
            new AgentReputationHistoryRepository(_db),
            new LifetimeSnapshotRepository(_db),
            null!,  // ChainEventScanner — not called in digest path
            null!,  // AcpOffChainClient — not called in digest path
            new ScoreCalculator(),
            _repo,
            NullLogger<ReputationService>.Instance);

        _svc = new DigestService(_repo, repSvc, satCalc);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task SeedOfferingAsync(string agentAddr, string agentName,
        string offeringName, string firstSeenAt, bool isRemoved = false,
        string? removedAt = null)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings
                (agent_address, agent_name, offering_name, description,
                 price_usdc, price_type, chain, content_hash,
                 first_seen_at, last_seen_at, marketplace_version, is_removed, removed_at)
            VALUES ($a, $n, $o, 'test offering', 0.99, 'per_call', 'base', $h,
                    $fs, $fs, 'v2', $rm, $ra);";
        cmd.Parameters.AddWithValue("$a", agentAddr.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$n", agentName);
        cmd.Parameters.AddWithValue("$o", offeringName);
        cmd.Parameters.AddWithValue("$h", $"hash_{agentAddr}_{offeringName}");
        cmd.Parameters.AddWithValue("$fs", firstSeenAt);
        cmd.Parameters.AddWithValue("$rm", isRemoved ? 1 : 0);
        cmd.Parameters.AddWithValue("$ra", (object?)removedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── test 1: newAgents count ───────────────────────────────────────────────

    [Fact]
    public async Task Digest_NewAgents_CountsByFirstSeenInWindow()
    {
        var now = DateTime.UtcNow;
        // Agent A: well inside window
        await SeedOfferingAsync("0xaaa1000000000000000000000000000000000001", "Agent-A",
            "offer_a1", now.AddDays(-10).ToString("O"));
        await SeedOfferingAsync("0xaaa1000000000000000000000000000000000001", "Agent-A",
            "offer_a2", now.AddDays(-10).ToString("O"));
        // Agent B: inside window
        await SeedOfferingAsync("0xbbb2000000000000000000000000000000000002", "Agent-B",
            "offer_b1", now.AddDays(-5).ToString("O"));
        // Agent C: outside window (35 days ago, window = 30)
        await SeedOfferingAsync("0xccc3000000000000000000000000000000000003", "Agent-C",
            "offer_c1", now.AddDays(-35).ToString("O"));

        var result = await _svc.BuildAsync(30);

        Assert.Equal(2, result.NewAgents.Count);
        Assert.Equal(2, result.NewAgents.Agents.Count);
    }

    // ── test 2: churn rate ────────────────────────────────────────────────────

    [Fact]
    public async Task Digest_ChurnRate_Computes()
    {
        var now = DateTime.UtcNow;
        // Agent-D: first seen 40d ago, still active — in baseline, not churned
        await SeedOfferingAsync("0xddd4000000000000000000000000000000000004", "Agent-D",
            "offer_d1", now.AddDays(-40).ToString("O"), isRemoved: false);
        // Agent-E: first seen 40d ago, removed 5d ago (after window start) — in
        // baseline (removed_at > windowStart) and churned (no active offerings)
        await SeedOfferingAsync("0xeee5000000000000000000000000000000000005", "Agent-E",
            "offer_e1", now.AddDays(-40).ToString("O"),
            isRemoved: true, removedAt: now.AddDays(-5).ToString("O"));

        var result = await _svc.BuildAsync(30);

        // baseline=2, churned=1 → rate=0.5
        Assert.Equal(2, result.ChurnRate.BaselineCount);
        Assert.Equal(1, result.ChurnRate.ChurnedCount);
        Assert.Equal(0.5, result.ChurnRate.Rate, precision: 3);
    }

    // ── test 3: cohort survival null for short window ─────────────────────────

    [Fact]
    public async Task Digest_CohortSurvival_NullForShortWindow()
    {
        var result = await _svc.BuildAsync(7);

        Assert.Null(result.CohortSurvival);
    }

    // ── test 4: cohort survival populated for long window ────────────────────

    [Fact]
    public async Task Digest_CohortSurvival_PopulatedForLongWindow()
    {
        var now = DateTime.UtcNow;
        // Seed an agent whose first offering falls in the 60-day window
        await SeedOfferingAsync("0xfff6000000000000000000000000000000000006", "Agent-F",
            "offer_f1", now.AddDays(-50).ToString("O"), isRemoved: false);

        var result = await _svc.BuildAsync(60);

        Assert.NotNull(result.CohortSurvival);
        Assert.NotEmpty(result.CohortSurvival!);
        var row = result.CohortSurvival!.First();
        Assert.True(row.CohortSize >= 1);
        Assert.Contains("W", row.CohortWeek);
    }

    // ── test 5: windowStart ───────────────────────────────────────────────────

    [Fact]
    public async Task Digest_WindowStart_IsCorrect()
    {
        var before = DateTime.UtcNow.AddDays(-30).AddSeconds(-5);
        var result = await _svc.BuildAsync(30);
        var after = DateTime.UtcNow.AddDays(-30).AddSeconds(5);

        var parsed = DateTime.Parse(result.WindowStart,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.InRange(parsed, before, after);
    }

    // ── test 6: BuildAsync(60) doesn't throw (days cap is in handler) ─────────

    [Fact]
    public async Task Digest_DaysCap_BuildAsyncWorksFor60Days()
    {
        // Service itself has no internal cap; 60-day window should succeed.
        var ex = await Record.ExceptionAsync(() => _svc.BuildAsync(60));
        Assert.Null(ex);
    }

    // ── test 7: second call with same filters is a cache hit ──────────────────

    [Fact]
    public async Task Digest_Cache_SecondCallSameFiltersReturnsSameInstance()
    {
        var first  = await _svc.BuildAsync(1);
        var second = await _svc.BuildAsync(1);

        // Same object reference proves no recompute (within the same hour bucket)
        Assert.Same(first, second);
    }

}
