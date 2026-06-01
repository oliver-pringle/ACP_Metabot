using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class PurchaserBudgetServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly PurchaserBudgetService _svc;

    public PurchaserBudgetServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_purch_budget_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
                ["ACPPURCHASER_DAILY_CAP_USDC"] = "10.0",
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _svc = new PurchaserBudgetService(_db, config, NullLogger<PurchaserBudgetService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Reserve_under_cap_succeeds_and_accumulates()
    {
        var r1 = await _svc.TryReserveAsync("0xbuyer", 4m, CancellationToken.None);
        Assert.True(r1.Reserved);
        var r2 = await _svc.TryReserveAsync("0xbuyer", 4m, CancellationToken.None);
        Assert.True(r2.Reserved);
        Assert.Equal(8m, r2.DayTotalUsd);
    }

    [Fact]
    public async Task Reserve_over_cap_is_rejected_and_not_charged()
    {
        Assert.True((await _svc.TryReserveAsync("0xbuyer", 9m, CancellationToken.None)).Reserved);
        var over = await _svc.TryReserveAsync("0xbuyer", 5m, CancellationToken.None);
        Assert.False(over.Reserved);
        Assert.Equal(9m, await _svc.GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
    }

    [Fact]
    public async Task Refund_restores_headroom()
    {
        await _svc.TryReserveAsync("0xbuyer", 9m, CancellationToken.None);
        await _svc.RecordActualSpendAsync("0xbuyer", -9m, CancellationToken.None);
        Assert.Equal(0m, await _svc.GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
        Assert.True((await _svc.TryReserveAsync("0xbuyer", 9m, CancellationToken.None)).Reserved);
    }

    [Fact]
    public async Task Caps_are_isolated_per_buyer()
    {
        Assert.True((await _svc.TryReserveAsync("0xA", 9m, CancellationToken.None)).Reserved);
        var b = await _svc.TryReserveAsync("0xB", 9m, CancellationToken.None);
        Assert.True(b.Reserved); // 0xB has its own bucket
    }
}
