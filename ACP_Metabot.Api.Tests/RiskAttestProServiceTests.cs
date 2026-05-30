using System.Text;
using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.0 riskAttestPro Task 6: 7-lane orchestrator. Tests use the real
/// RiskTrajectoryStore + RiskAttestProLlm (with injector stub so no LLM
/// network) + real RiskAttestProMarkdown, mocking only the 4 peer clients
/// + Arena + Witness + the reputation cache repo (each gets a tiny inline
/// fake exposed below).
/// </summary>
public class RiskAttestProServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly RiskTrajectoryStore _traj;
    private readonly RiskAttestProLlm _llm;

    public RiskAttestProServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_attestpro_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _traj = new RiskTrajectoryStore(_db, NullLogger<RiskTrajectoryStore>.Instance);
        // Stub LLM injector — deterministic narration so happy-path tests
        // assert on a stable string. Keeps the LLM budget code path exercised
        // (cache write, spend increment) without touching Anthropic.
        Func<string, Task<(string, decimal)>> injector = async _ =>
        {
            await Task.Yield();
            return ("stubbed narration", 0.001m);
        };
        _llm = new RiskAttestProLlm(_db, NullLogger<RiskAttestProLlm>.Instance, injector);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ── Fake peer-client implementations ────────────────────────────────────

    private sealed class FakePeers : IRiskPeerClients
    {
        public Func<string, string, JsonDocument?> Hf { get; set; } = (_, _) => null;
        public Func<string, string, JsonDocument?> Quote { get; set; } = (_, _) => null;
        public Func<string, JsonDocument?> Mev { get; set; } = _ => null;
        public Func<string, string, string, string, JsonDocument?> Calldata { get; set; } = (_, _, _, _) => null;
        public Func<object, JsonDocument?> Publish { get; set; } = _ => null;

        public Task<JsonDocument?> GetHealthFactorAsync(string wallet, string chain, CancellationToken ct)
            => Task.FromResult(Hf(wallet, chain));
        public Task<JsonDocument?> GetApprovalsQuoteAsync(string wallet, string chain, CancellationToken ct)
            => Task.FromResult(Quote(wallet, chain));
        public Task<JsonDocument?> GetMevScoreAsync(string wallet, CancellationToken ct)
            => Task.FromResult(Mev(wallet));
        public Task<JsonDocument?> GetRevokeCalldataAsync(string wallet, string chain, string spender, string token, CancellationToken ct)
            => Task.FromResult(Calldata(wallet, chain, spender, token));
        public Task<JsonDocument?> PublishAttestationAsync(object payload, CancellationToken ct)
            => Task.FromResult(Publish(payload));
    }

    private sealed class FakeWitness : IWitnessBotClient
    {
        public WitnessManifest Result { get; set; } = new WitnessManifest(
            IsAcpAgent: false, CatalogueHash: null, SignerEoa: null, SignedAt: null,
            ManifestUid: null, Status: "fresh", Details: "no manifest");
        public Task<WitnessManifest> ManifestByAgentAsync(string agentAddress, CancellationToken ct = default)
            => Task.FromResult(Result);
    }

    /// <summary>
    /// Test-only async reputation lookup. The orchestrator depends on a tiny
    /// delegate-shaped interface so tests don't have to wire the full
    /// ReputationService graph (which needs an AcpOffChainClient + Scanner +
    /// many other things).
    /// </summary>
    private sealed class FakeRep : IRiskReputationLookup
    {
        public CachedReputationRow? Row { get; set; }
        public Task<CachedReputationRow?> GetAsync(string wallet, CancellationToken ct)
            => Task.FromResult(Row);
    }

    private sealed class FakeArena : IRiskArenaLookup
    {
        public JsonDocument? Result { get; set; }
        public Task<JsonDocument?> GetParticipantAsync(string wallet, CancellationToken ct)
            => Task.FromResult(Result);
    }

    // ── JsonDocument helpers ────────────────────────────────────────────────

    private static JsonDocument Json(string text) => JsonDocument.Parse(text);

    private static JsonDocument FreshHfPayload(double hf = 1.87) => Json($$"""
        {
          "wallet": "0xabc",
          "chain": "base",
          "healthFactors": [{ "protocol": "Aave V3", "hf": {{hf.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}}, "status": "healthy" }]
        }
    """);

    private static JsonDocument FreshApprovalsPayload(int highRisk = 0, int total = 5) => Json($$"""
        {
          "wallet": "0xabc",
          "chain": "base",
          "totalApprovals": {{total}},
          "highRiskCount": {{highRisk}},
          "approvals": []
        }
    """);

    private static JsonDocument FreshApprovalsWithSpenders() => Json("""
        {
          "wallet": "0xabc",
          "chain": "base",
          "totalApprovals": 3,
          "highRiskCount": 2,
          "approvals": [
            { "spender": "0xrisky1", "token": "0xtok1", "riskTier": "high" },
            { "spender": "0xrisky2", "token": "0xtok2", "riskTier": "high" }
          ]
        }
    """);

    private static JsonDocument FreshMevPayload(int mevScore = 80) => Json($$"""
        {
          "wallet": "0xabc",
          "mevScore": {{mevScore}},
          "sandwichesObserved": 0,
          "windowDays": 30
        }
    """);

    private static JsonDocument FreshArenaPayload(bool participant) => Json($$"""
        {
          "wallet": "0xabc",
          "isParticipant": {{(participant ? "true" : "false")}}
        }
    """);

    private static JsonDocument FreshCalldataPayload(string spender, string token) => Json($$"""
        {
          "to": "{{token}}",
          "data": "0xa9059cbb000000000000000000000000{{spender.Substring(2)}}",
          "value": "0"
        }
    """);

    private static CachedReputationRow FreshRepRow(int score = 70) => new(
        AgentAddress: "0xabc",
        AgentName: "TestAgent",
        AgentScore: score,
        SubScoresJson: "{}",
        RawCountsJson: "{}",
        FlagsJson: "{}",
        ComputedAt: DateTime.UtcNow,
        LastScannedBlock: 0,
        Source: "lazy");

    private RiskAttestProService MakeSvc(
        FakePeers peers, FakeWitness witness, FakeRep rep, FakeArena arena)
        => new(peers, witness, rep, arena, _traj, _llm, NullLogger<RiskAttestProService>.Instance);

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_returns_verdict_OK_with_seven_components()
    {
        var peers = new FakePeers
        {
            Hf = (_, _) => FreshHfPayload(),
            Quote = (_, _) => FreshApprovalsPayload(highRisk: 0, total: 5),
            Mev = _ => FreshMevPayload(mevScore: 85),
        };
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(70) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        await SeedTrajectory("0xabc", "base", 70, 65, 60);

        var svc = MakeSvc(peers, witness, rep, arena);
        var result = await svc.GenerateAsync("0xABC", "base");

        Assert.Equal(7, result.Components.EnumerateObject().Count());
        Assert.Empty(result.SourcesUnavailable);
        Assert.Equal("0xabc", result.Wallet);
        Assert.Equal("base", result.Chain);
        Assert.Contains(new[] { "STRONG_BUY", "OK", "CAUTION" }, v => v == result.Verdict);
        Assert.Equal(7, result.SourcesQueried.Length);
    }

    [Fact]
    public async Task OneLaneUnavailable_proceeds_with_sourcesUnavailable_populated()
    {
        var peers = new FakePeers
        {
            Hf = (_, _) => FreshHfPayload(),
            Quote = (_, _) => FreshApprovalsPayload(0, 5),
            Mev = _ => FreshMevPayload(80),
        };
        // Witness: explicit "unavailable" — distinguished from "fresh + IsAcpAgent=false".
        var witness = new FakeWitness
        {
            Result = new WitnessManifest(false, null, null, null, null, "unavailable", "HTTP 503"),
        };
        var rep = new FakeRep { Row = FreshRepRow(70) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        await SeedTrajectory("0xabc", "base", 70, 65, 60);

        var svc = MakeSvc(peers, witness, rep, arena);
        var result = await svc.GenerateAsync("0xabc", "base");

        Assert.Contains("TheWitnessBot", result.SourcesUnavailable);
        Assert.Single(result.SourcesUnavailable);
        Assert.NotEqual("INSUFFICIENT_DATA", result.Verdict);
    }

    [Fact]
    public async Task FourLanesSucceed_just_meets_floor()
    {
        // 3 unavailable: HF (null), Approvals (null), MEV (null). Reputation,
        // Witness (fresh), Arena (fresh), Trajectory (fresh) = 4 fresh.
        var peers = new FakePeers();
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(65) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        await SeedTrajectory("0xabc", "base", 65, 60, 55);

        var svc = MakeSvc(peers, witness, rep, arena);
        var result = await svc.GenerateAsync("0xabc", "base");

        Assert.Equal(3, result.SourcesUnavailable.Length);
        Assert.NotNull(result.Verdict);
    }

    [Fact]
    public async Task ThreeLanesSucceed_throws_InsufficientSignalsException()
    {
        // Only 3 of 7 fresh: Reputation, Arena, Witness. HF/Approvals/MEV null
        // and Trajectory empty (insufficient_data → counts as unavailable).
        var peers = new FakePeers();
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(65) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        // No trajectory seeded.

        var svc = MakeSvc(peers, witness, rep, arena);
        var ex = await Assert.ThrowsAsync<InsufficientSignalsException>(
            () => svc.GenerateAsync("0xabc", "base"));
        Assert.Contains("got 3", ex.Message);
    }

    [Fact]
    public async Task ComponentsHash_stable_across_identical_payloads()
    {
        var peers = new FakePeers
        {
            Hf = (_, _) => FreshHfPayload(),
            Quote = (_, _) => FreshApprovalsPayload(0, 5),
            Mev = _ => FreshMevPayload(80),
        };
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(70) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        await SeedTrajectory("0xabc", "base", 70, 65, 60);

        var svc = MakeSvc(peers, witness, rep, arena);
        var a = await svc.GenerateAsync("0xabc", "base");
        var b = await svc.GenerateAsync("0xabc", "base");
        Assert.Equal(a.ComponentsHash, b.ComponentsHash);
    }

    [Fact]
    public async Task ComponentsHash_changes_when_subscore_changes_by_one()
    {
        var peers = new FakePeers
        {
            Hf = (_, _) => FreshHfPayload(),
            Quote = (_, _) => FreshApprovalsPayload(0, 5),
            Mev = _ => FreshMevPayload(80),
        };
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(70) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        await SeedTrajectory("0xabc", "base", 70, 65, 60);

        var svc = MakeSvc(peers, witness, rep, arena);
        var a = await svc.GenerateAsync("0xabc", "base");

        // Change reputation score by 1 — hash should diverge.
        rep.Row = FreshRepRow(71);
        var b = await svc.GenerateAsync("0xabc", "base");
        Assert.NotEqual(a.ComponentsHash, b.ComponentsHash);
    }

    [Fact]
    public async Task Recommendations_include_revoke_when_high_risk_approvals_exist()
    {
        var peers = new FakePeers
        {
            Hf = (_, _) => FreshHfPayload(),
            Quote = (_, _) => FreshApprovalsWithSpenders(),
            Mev = _ => FreshMevPayload(80),
            Calldata = (_, _, spender, token) => FreshCalldataPayload(spender, token),
        };
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(70) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        await SeedTrajectory("0xabc", "base", 70, 65, 60);

        var svc = MakeSvc(peers, witness, rep, arena);
        var result = await svc.GenerateAsync("0xabc", "base");

        // Recommendations should contain at least one {action:"revoke"} entry.
        var actions = result.Recommendations.EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToArray();
        Assert.Contains("revoke", actions);
    }

    [Fact]
    public async Task Trajectory_direction_improving_when_scores_ascend()
    {
        var peers = new FakePeers
        {
            Hf = (_, _) => FreshHfPayload(),
            Quote = (_, _) => FreshApprovalsPayload(0, 5),
            Mev = _ => FreshMevPayload(80),
        };
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(70) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        // older→newer: 60 (21d ago) → 65 (14d ago) → 70 (7d ago) = improving.
        await SeedTrajectory("0xabc", "base", scoreAt7: 70, scoreAt14: 65, scoreAt21: 60);

        var svc = MakeSvc(peers, witness, rep, arena);
        var result = await svc.GenerateAsync("0xabc", "base");

        var traj = result.Components.GetProperty("trajectory");
        Assert.Equal("improving", traj.GetProperty("direction").GetString());
    }

    [Fact]
    public async Task Trajectory_direction_insufficient_data_when_no_history()
    {
        // Empty trajectory store; 6 fresh peer signals → 4-of-7 floor still met
        // BUT trajectory counts as unavailable (per spec: insufficient_data ≡
        // counts against the floor). Pass 6 fresh upstream signals so the
        // request itself proceeds (6 fresh ≥ 4 floor) and we can still
        // inspect the trajectory component's direction.
        var peers = new FakePeers
        {
            Hf = (_, _) => FreshHfPayload(),
            Quote = (_, _) => FreshApprovalsPayload(0, 5),
            Mev = _ => FreshMevPayload(80),
        };
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(70) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        // No SeedTrajectory call.

        var svc = MakeSvc(peers, witness, rep, arena);
        var result = await svc.GenerateAsync("0xabc", "base");

        var traj = result.Components.GetProperty("trajectory");
        Assert.Equal("insufficient_data", traj.GetProperty("direction").GetString());
        Assert.Contains("history", result.SourcesUnavailable);
    }

    [Fact]
    public async Task MarkdownReport_is_base64_valid_markdown()
    {
        var peers = new FakePeers
        {
            Hf = (_, _) => FreshHfPayload(),
            Quote = (_, _) => FreshApprovalsPayload(0, 5),
            Mev = _ => FreshMevPayload(80),
        };
        var witness = new FakeWitness();
        var rep = new FakeRep { Row = FreshRepRow(70) };
        var arena = new FakeArena { Result = FreshArenaPayload(false) };
        await SeedTrajectory("0xabc", "base", 70, 65, 60);

        var svc = MakeSvc(peers, witness, rep, arena);
        var result = await svc.GenerateAsync("0xabc", "base");

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(result.MarkdownReportBase64));
        Assert.StartsWith("# riskAttestPro report", decoded);
    }

    // ── Helper to seed trajectory rows at -7/-14/-21 day strides ────────────

    private async Task SeedTrajectory(string wallet, string chain,
        int scoreAt7, int scoreAt14, int scoreAt21)
    {
        var now = DateTimeOffset.UtcNow;
        await _traj.WriteAsync(wallet, chain, now.AddDays(-7), scoreAt7, "{}");
        await _traj.WriteAsync(wallet, chain, now.AddDays(-14), scoreAt14, "{}");
        await _traj.WriteAsync(wallet, chain, now.AddDays(-21), scoreAt21, "{}");
    }
}
