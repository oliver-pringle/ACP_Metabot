using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.0 riskAttestPro Task 2: read-through store over risk_snapshot_history.
/// Mirrors DbTests temp-DB scaffolding so the xunit parallel matrix doesn't
/// collide on a shared file.
/// </summary>
public class RiskTrajectoryStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly RiskTrajectoryStore _store;

    public RiskTrajectoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_traj_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _store = new RiskTrajectoryStore(_db, NullLogger<RiskTrajectoryStore>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Lookup_returns_null_when_history_empty()
    {
        var now = DateTimeOffset.UtcNow;
        var row = await _store.LookupStrideAsync("0xabc", "base", now, daysAgo: 7);
        Assert.Null(row);
    }

    [Fact]
    public async Task Write_then_lookup_at_zero_stride_returns_row()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync("0xabc", "base", now, score: 72, componentsJson: "{\"hf\":80}");

        var row = await _store.LookupStrideAsync("0xabc", "base", now, daysAgo: 0);
        Assert.NotNull(row);
        Assert.Equal(72, row!.Score);
        Assert.Equal("{\"hf\":80}", row.ComponentsJson);
    }

    [Fact]
    public async Task Lookup_stride_finds_nearest_row_within_24h_window()
    {
        var now = DateTimeOffset.UtcNow;
        // Row was captured at (now - 7 days + 1 hour). Lookup at daysAgo=7
        // centres the ±12h window at (now - 7 days), so the row at +1h sits
        // comfortably inside.
        var captured = now.AddDays(-7).AddHours(1);
        await _store.WriteAsync("0xabc", "base", captured, score: 55, componentsJson: "{}");

        var row = await _store.LookupStrideAsync("0xabc", "base", now, daysAgo: 7);
        Assert.NotNull(row);
        Assert.Equal(55, row!.Score);
    }

    [Fact]
    public async Task Lookup_stride_returns_null_when_no_row_in_24h_window()
    {
        var now = DateTimeOffset.UtcNow;
        // 5-day-old row sits 2 days outside the ±12h window centred at (now - 7d).
        var captured = now.AddDays(-5);
        await _store.WriteAsync("0xabc", "base", captured, score: 40, componentsJson: "{}");

        var row = await _store.LookupStrideAsync("0xabc", "base", now, daysAgo: 7);
        Assert.Null(row);
    }

    [Fact]
    public async Task Lookup_is_case_insensitive_on_wallet()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync("0xABC", "base", now, score: 33, componentsJson: "{}");

        var row = await _store.LookupStrideAsync("0xabc", "base", now, daysAgo: 0);
        Assert.NotNull(row);
        Assert.Equal(33, row!.Score);
    }
}
