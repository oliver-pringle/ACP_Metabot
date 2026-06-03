using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ACP_Metabot.Api.Tests;

public class StackPurchaserServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly IConfiguration _config;

    public StackPurchaserServiceTests()
    {
        _dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"acp_stacksvc_{System.Guid.NewGuid():N}.db");
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
            ["ACPPURCHASER_DAILY_CAP_USDC"] = "50.0",
        }).Build();
        _db = new Db(_config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (System.IO.File.Exists(_dbPath)) System.IO.File.Delete(_dbPath);
    }

    // --- Fakes (mirror PurchaserServiceTests seams) ---
    private sealed class FakeRisk : IAgentRiskSource
    {
        public Dictionary<string, string> Tiers = new(StringComparer.OrdinalIgnoreCase);
        public Task<string> RiskTierAsync(string a, int c, CancellationToken ct)
            => Task.FromResult(Tiers.TryGetValue(a, out var t) ? t : "low");
    }

    private sealed class FakeComposer : IStackComposerSource
    {
        public List<ACP_Metabot.Api.Models.StackEntry> Entries = new();
        public Task<IReadOnlyList<ACP_Metabot.Api.Models.StackEntry>> CurateAsync(string intent, decimal budget, int max, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ACP_Metabot.Api.Models.StackEntry>>(Entries);
    }

    private sealed class FakeOfferings : IStackOfferingSource
    {
        // key: (agentAddress lowercased, offeringName) -> (priceType, requirementSchemaJson)
        public Dictionary<(string, string), (string PriceType, string? Schema)> Map = new();
        public Task<(string PriceType, string? RequirementSchemaJson)?> ResolveAsync(string agent, string offering, CancellationToken ct)
            => Task.FromResult(Map.TryGetValue((agent.ToLowerInvariant(), offering), out var v) ? ((string, string?)?)v : null);
    }

    private (StackPurchaserService svc, FakeComposer comp, FakeRisk risk, FakeOfferings offs) Build()
    {
        var budget = new PurchaserBudgetService(_db, _config, NullLogger<PurchaserBudgetService>.Instance);
        var comp = new FakeComposer();
        var risk = new FakeRisk();
        var offs = new FakeOfferings();
        int n = 0;
        var svc = new StackPurchaserService(_db, new StackQuoteStore(_db), budget, risk, comp, offs,
            idFactory: () => $"stk_{++n}", NullLogger<StackPurchaserService>.Instance);
        return (svc, comp, risk, offs);
    }

    private const string A1 = "0x1111111111111111111111111111111111111111";
    private const string A2 = "0x2222222222222222222222222222222222222222";
    private const string SUBJECT = "0x9999999999999999999999999999999999999999";
    private const string ADDR_SCHEMA = "{\"type\":\"object\",\"properties\":{\"agentAddress\":{\"type\":\"string\"}}}";

    // ── Task 3: QuoteAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Quote_keeps_fixed_price_low_risk_mappable_step()
    {
        var (svc, comp, _, offs) = Build();
        comp.Entries.Add(new("risk_snapshot", "MetaBot", A1, 0.05, "risk"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);

        var q = await svc.QuoteAsync(SUBJECT, "screen this wallet", 1.0m, 5, default);

        Assert.Equal("PROCEED", q.Verdict);
        Assert.Single(q.Steps);
        Assert.Equal(0.05m, q.TotalDownstreamUsdc);
        Assert.Equal(0.25m, q.ExecuteFeeUsdc);
        Assert.Equal(0.30m, q.TotalEscrowUsdc);
        Assert.StartsWith("stk_", q.QuoteId);
    }

    [Fact]
    public async Task Quote_drops_non_fixed_price_step()
    {
        var (svc, comp, _, offs) = Build();
        comp.Entries.Add(new("oracle_watch", "OracleBot", A1, 0.50, "watch"));
        offs.Map[(A1, "oracle_watch")] = ("subscription", ADDR_SCHEMA);

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        Assert.Equal("BLOCK", q.Verdict); // zero kept
        Assert.Contains(q.DroppedCandidates, d => d.Reason == "not_fixed_price");
    }

    [Fact]
    public async Task Quote_drops_critical_risk_step()
    {
        var (svc, comp, risk, offs) = Build();
        comp.Entries.Add(new("scan", "Bad", A1, 0.05, "x"));
        offs.Map[(A1, "scan")] = ("fixed", ADDR_SCHEMA);
        risk.Tiers[A1] = "critical";

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        Assert.Contains(q.DroppedCandidates, d => d.Reason == "risk_critical");
    }

    [Fact]
    public async Task Quote_drops_subject_unmappable_step()
    {
        var (svc, comp, _, offs) = Build();
        comp.Entries.Add(new("digest", "X", A1, 0.05, "x"));
        offs.Map[(A1, "digest")] = ("fixed", "{\"type\":\"object\",\"properties\":{\"days\":{\"type\":\"number\"}}}");

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        Assert.Contains(q.DroppedCandidates, d => d.Reason == "subject_unmappable");
    }

    [Fact]
    public async Task Quote_blocks_when_total_exceeds_maxFunds()
    {
        var (svc, comp, _, offs) = Build();
        comp.Entries.Add(new("a", "X", A1, 0.50, "r"));
        comp.Entries.Add(new("b", "Y", A2, 0.60, "r"));
        offs.Map[(A1, "a")] = ("fixed", ADDR_SCHEMA);
        offs.Map[(A2, "b")] = ("fixed", ADDR_SCHEMA);

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        Assert.Equal("BLOCK", q.Verdict);
        Assert.Contains("over_max_funds", q.Reasons);
    }

    [Fact]
    public async Task Quote_persists_a_loadable_plan()
    {
        var (svc, comp, _, offs) = Build();
        comp.Entries.Add(new("risk_snapshot", "MetaBot", A1, 0.05, "risk"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);
        var stored = await new StackQuoteStore(_db).LoadAsync(q.QuoteId, default);

        Assert.NotNull(stored);
        // buyer is bound at precheck — stored with empty key at quote time (plan note b)
        Assert.Equal("", stored!.BuyerKey);
        Assert.Single(stored.Steps);
        Assert.Equal("agentAddress", System.Linq.Enumerable.First(stored.Steps[0].InnerRequirement.Keys));
        Assert.Equal(SUBJECT.ToLowerInvariant(), stored.Steps[0].InnerRequirement["agentAddress"]?.ToString());
    }

    // ── Task 4: PrecheckAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task Precheck_rejects_expired_quote()
    {
        var (svc, _, _, _) = Build();
        var r = await svc.PrecheckAsync("outer1", "0xbuyer", "missing", SUBJECT, default);
        Assert.False(r.Ok);
        Assert.Equal("quote_expired_or_not_found", r.Reason);
    }

    [Fact]
    public async Task Precheck_rejects_subject_mismatch()
    {
        var (svc, comp, _, offs) = Build();
        comp.Entries.Add(new("risk_snapshot", "M", A1, 0.05, "r"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);
        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        var r = await svc.PrecheckAsync("outer1", "0xbuyer", q.QuoteId, "0xdifferent", default);
        Assert.False(r.Ok);
        Assert.Equal("subject_mismatch", r.Reason);
    }

    [Fact]
    public async Task Precheck_binds_buyer_and_reserves_then_blocks_second_buyer()
    {
        var (svc, comp, _, offs) = Build();
        comp.Entries.Add(new("risk_snapshot", "M", A1, 0.05, "r"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);
        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        var first = await svc.PrecheckAsync("outer1", "0xbuyerA", q.QuoteId, SUBJECT, default);
        Assert.True(first.Ok);
        Assert.Single(first.Steps);

        var second = await svc.PrecheckAsync("outer2", "0xbuyerB", q.QuoteId, SUBJECT, default);
        Assert.False(second.Ok);
        Assert.Equal("buyer_mismatch", second.Reason);
    }

    // ── Task 5: SettleAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task Settle_rejected_refunds_full_reservation()
    {
        var (svc, comp, _, offs) = Build();
        comp.Entries.Add(new("risk_snapshot", "M", A1, 0.30, "r"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);
        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);
        var pre = await svc.PrecheckAsync("outerX", "0xbuyer", q.QuoteId, SUBJECT, default);
        Assert.True(pre.Ok);

        var budget = new PurchaserBudgetService(_db, _config, NullLogger<PurchaserBudgetService>.Instance);
        Assert.Equal(0.30m, await budget.GetTodaysSpendAsync("0xbuyer", default));

        await svc.SettleAsync("outerX", "0xbuyer", "REJECTED", null, "downstream_failed", 0.30m, default);
        Assert.Equal(0m, await budget.GetTodaysSpendAsync("0xbuyer", default));
    }
}
