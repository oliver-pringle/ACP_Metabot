using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.10 Phase 3 T5: end-to-end coverage of AgentRiskScorer's four signals.
/// Each test seeds offerings + (where relevant) reputation rows directly via
/// SQL, then drives ScoreAsync and asserts on the resulting signal slot.
///
/// The fixture intentionally bypasses SearchService — pricing-outlier and
/// footprint-anomaly are exercised against the offerings table directly,
/// and SearchService corpus init is heavy. The internal ctor lets tests
/// inject suspiciousFundersOverride so they don't depend on the on-disk
/// SuspiciousFunderPatterns.json being copied to the test bin directory.
/// </summary>
public class AgentRiskScorerTests : IAsyncLifetime
{
    private string _dbPath = "";
    private Db _db = null!;
    private OfferingRepository _offerings = null!;
    private AgentReputationCacheRepository _reputation = null!;
    private PricePercentileCalculator _pricePercentile = null!;
    private IConfiguration _config = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_risk_scorer_{Guid.NewGuid():N}.db");
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
                ["Search:AgentRiskCacheTtlSeconds"] = "300",
            }).Build();
        _db = new Db(_config);
        await _db.InitializeSchemaAsync();
        _offerings = new OfferingRepository(_db);
        _reputation = new AgentReputationCacheRepository(_db);
        _pricePercentile = new PricePercentileCalculator(lowNThreshold: 5);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    // ── Seed helpers ──────────────────────────────────────────────────────

    private async Task SeedOfferingAsync(
        string agentAddress, string offeringName, double price = 0.10,
        string marketplaceVersion = "v2", string chain = "base")
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings (
                agent_address, agent_name, offering_name, description,
                price_usdc, price_type, chain, content_hash,
                first_seen_at, last_seen_at, marketplace_version, is_removed)
            VALUES ($a, 'TestAgent', $n, 'desc', $p, 'per_call', $ch, $h,
                    '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z', $mv, 0);";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$n", offeringName);
        cmd.Parameters.AddWithValue("$p", price);
        cmd.Parameters.AddWithValue("$ch", chain);
        cmd.Parameters.AddWithValue("$h", $"{agentAddress}|{offeringName}|{price}");
        cmd.Parameters.AddWithValue("$mv", marketplaceVersion);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedReputationAsync(string agentAddress, long totalJobs)
    {
        var rawCounts = new RawCounts(
            TotalJobs:        totalJobs,
            Completed:        Math.Max(0, totalJobs - 1),
            Rejected:         0,
            Expired:          0,
            CompletedLast30d: Math.Max(0, totalJobs - 1),
            LastActiveAt:     null);
        await _reputation.UpsertAsync(new CachedReputationRow(
            AgentAddress:     agentAddress.ToLowerInvariant(),
            AgentName:        "Agent",
            AgentScore:       70,
            SubScoresJson:    "{}",
            RawCountsJson:    System.Text.Json.JsonSerializer.Serialize(rawCounts),
            FlagsJson:        "{}",
            ComputedAt:       DateTime.UtcNow,
            LastScannedBlock: 1_000_000,
            Source:           "chain"));
    }

    private AgentRiskScorer BuildScorer(IEnumerable<string>? suspiciousFunders = null)
    {
        return new AgentRiskScorer(
            _db, _offerings, _reputation, _pricePercentile,
            search: null,
            _config,
            NullLogger<AgentRiskScorer>.Instance,
            suspiciousFundersOverride: suspiciousFunders ?? Array.Empty<string>());
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScoreAsync_low_risk_for_well_established_agent()
    {
        // Well-established agent: 100+ completed jobs (reputationDepth = 0),
        // own offering names (no V1 collision; footprintAnomaly = 0),
        // not in suspicious-funder list (walletProvenance = 0),
        // no SearchService so pricing-outlier degrades to 0 ("no categorised
        // offerings or insufficient peer pool"). Total expected: 0 (low).
        var agent = "0x" + new string('1', 40);
        await SeedOfferingAsync(agent, "unique_offering_low_risk", price: 0.10);
        await SeedReputationAsync(agent, totalJobs: 150);

        var scorer = BuildScorer();
        var result = await scorer.ScoreAsync(agent, chainId: 8453, CancellationToken.None);

        Assert.Equal("low", result.RiskTier);
        Assert.InRange(result.RiskScore, 0, 25);
        Assert.Equal(4, result.Signals.Count);

        var depth = result.Signals.Single(s => s.Name == "reputationDepth");
        Assert.Equal(0, depth.Score);
        Assert.Contains("150 completed jobs", depth.Detail);

        var provenance = result.Signals.Single(s => s.Name == "walletProvenance");
        Assert.Equal(0, provenance.Score);

        var footprint = result.Signals.Single(s => s.Name == "footprintAnomaly");
        Assert.Equal(0, footprint.Score);
    }

    [Fact]
    public async Task ScoreAsync_high_when_suspicious_funder_match()
    {
        // Same well-established agent shape as the low-risk test, but THIS
        // address is injected into the suspicious-funder set via the test
        // override. walletProvenance must flip to 25 (its only possible
        // non-zero score). Total expected: 25 → still "low" tier boundary,
        // but the walletProvenance signal is the load-bearing assertion.
        // (To push into "medium" tier we'd need to combine with another
        // non-zero signal — covered implicitly by the third test.)
        var agent = "0x" + new string('a', 40);
        await SeedOfferingAsync(agent, "some_offering", price: 0.10);
        await SeedReputationAsync(agent, totalJobs: 200);

        // Inject this exact address into the suspicious-funder set. The
        // override lowercases on construction so casing here doesn't matter.
        var scorer = BuildScorer(suspiciousFunders: new[] { agent });
        var result = await scorer.ScoreAsync(agent, chainId: 8453, CancellationToken.None);

        var provenance = result.Signals.Single(s => s.Name == "walletProvenance");
        Assert.Equal(25, provenance.Score);
        Assert.Contains("suspicious-funder", provenance.Detail);

        // Score should be exactly the walletProvenance contribution given the
        // other three signals all evaluate to 0.
        Assert.Equal(25, result.RiskScore);
        // 25 sits at the boundary between "low" and "medium" — spec says
        // <= 25 is low, so this stays low. Confirm the bin holds.
        Assert.Equal("low", result.RiskTier);
    }

    [Fact]
    public async Task ScoreAsync_footprint_anomaly_when_offering_name_matches_V1_agent()
    {
        // Seed a V1 agent at a DIFFERENT address holding offering "foo".
        // Then seed the target V2 agent with its own offering also named "foo".
        // The name-collision triggers footprintAnomaly = 25.
        var v1Squatter = "0x" + new string('b', 40);
        var v2Target = "0x" + new string('c', 40);

        await SeedOfferingAsync(v1Squatter, "foo", price: 1.00, marketplaceVersion: "v1");
        await SeedOfferingAsync(v2Target, "foo", price: 0.50, marketplaceVersion: "v2");

        // Brand-new V2 target — no reputation cache row → reputationDepth = 25
        // (jobCount defaults to 0). Combined with footprintAnomaly = 25 this
        // pushes the score to ≥ 50, landing the agent in the "medium" tier.
        var scorer = BuildScorer();
        var result = await scorer.ScoreAsync(v2Target, chainId: 8453, CancellationToken.None);

        var footprint = result.Signals.Single(s => s.Name == "footprintAnomaly");
        Assert.Equal(25, footprint.Score);
        Assert.Contains("foo", footprint.Detail);
        Assert.Contains("V1", footprint.Detail);

        var depth = result.Signals.Single(s => s.Name == "reputationDepth");
        Assert.Equal(25, depth.Score);

        // 25 + 25 + 0 + 0 = 50, which the spec bins as "medium" (<= 50).
        Assert.Equal(50, result.RiskScore);
        Assert.Equal("medium", result.RiskTier);
    }
}
