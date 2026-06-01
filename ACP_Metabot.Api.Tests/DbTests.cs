using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// Schema tests for tables added outside the DbMigrationTests scope.
/// </summary>
public class DbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public DbTests()
    {
        // Per-test temp file so xunit parallelism doesn't collide.
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_db_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task InitAsync_creates_risk_attest_pro_tables()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name FROM sqlite_master
            WHERE type='table'
              AND name IN (
                  'risk_snapshot_history',
                  'risk_attest_pro_spend',
                  'risk_attest_pro_cache',
                  'risk_attest_pro_bootstrap_state')
            ORDER BY name;";
        await using var reader = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        Assert.Equal(
            new[] {
                "risk_attest_pro_bootstrap_state",
                "risk_attest_pro_cache",
                "risk_attest_pro_spend",
                "risk_snapshot_history"
            },
            names);
    }

    [Fact]
    public async Task InitAsync_creates_acppurchaser_tables()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT name FROM sqlite_master WHERE type='table'
                            AND name IN ('acppurchaser_daily_spend','acppurchaser_audit');";
        var found = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) found.Add(rdr.GetString(0));
        Assert.Contains("acppurchaser_daily_spend", found);
        Assert.Contains("acppurchaser_audit", found);
    }
}
