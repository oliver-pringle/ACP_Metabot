using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class PurchaserServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly IConfiguration _config;

    public PurchaserServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_purch_svc_{Guid.NewGuid():N}.db");
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
            ["ACPPURCHASER_DAILY_CAP_USDC"] = "10.0",
        }).Build();
        _db = new Db(_config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private PurchaserBudgetService Budget() =>
        new(_db, _config, NullLogger<PurchaserBudgetService>.Instance);

    private PurchaserService Make(string tier) =>
        new(_db, Budget(), new StubRisk(tier), NullLogger<PurchaserService>.Instance);

    private sealed class StubRisk : IAgentRiskSource
    {
        private readonly string _tier;
        public StubRisk(string tier) => _tier = tier;
        public Task<string> RiskTierAsync(string agent, int chainId, CancellationToken ct) => Task.FromResult(_tier);
    }

    [Fact]
    public async Task Quote_fixed_low_risk_is_PROCEED()
    {
        var q = await Make("low").QuoteAsync("0xtarget", 0.05m, true, CancellationToken.None);
        Assert.Equal("PROCEED", q.Verdict);
        Assert.Equal(0.15m, q.TotalEscrowUsdc); // 0.10 + 0.05
    }

    [Fact]
    public async Task Quote_non_fixed_is_BLOCK()
    {
        var q = await Make("low").QuoteAsync("0xtarget", 0m, false, CancellationToken.None);
        Assert.Equal("BLOCK", q.Verdict);
        Assert.Contains("not_fixed_price", q.Reasons);
    }

    [Fact]
    public async Task Quote_critical_is_BLOCK()
    {
        var q = await Make("critical").QuoteAsync("0xtarget", 0.05m, true, CancellationToken.None);
        Assert.Equal("BLOCK", q.Verdict);
    }

    [Fact]
    public async Task Precheck_over_maxfunds_rejects_without_reserving()
    {
        var svc = Make("low");
        var r = await svc.PrecheckAsync("job1", "0xbuyer", "0xtarget", "spender_check", 0.20m, 0.05m, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("over_max_funds", r.Reason);
        Assert.Equal(0m, await Budget().GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
    }

    [Fact]
    public async Task Precheck_ok_reserves_and_audits()
    {
        var svc = Make("low");
        var r = await svc.PrecheckAsync("job1", "0xbuyer", "0xtarget", "spender_check", 0.05m, 0.50m, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(0.05m, await Budget().GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
    }

    [Fact]
    public async Task Settle_rejected_refunds_reservation()
    {
        var svc = Make("low");
        await svc.PrecheckAsync("job1", "0xbuyer", "0xtarget", "spender_check", 0.05m, 0.50m, CancellationToken.None);
        await svc.SettleAsync("job1", "0xbuyer", "REJECTED", null, "downstream_failed", 0.05m, CancellationToken.None);
        Assert.Equal(0m, await Budget().GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
    }
}
