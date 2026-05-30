using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.0 riskAttestPro Task 7: IHostedService idempotently registers the
/// AgentRisk EAS schema via the injector seam. Real EASIssuer wiring lands
/// in v1.0.1; these tests exercise the persistence + idempotency contract.
/// Mirrors the temp-DB scaffolding used by RiskTrajectoryStoreTests so the
/// xunit parallel matrix doesn't collide on a shared file.
/// </summary>
public class RiskAttestProSchemaBootstrapWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public RiskAttestProSchemaBootstrapWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_schemaboot_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task FirstBoot_inserts_schema_uid_into_bootstrap_state()
    {
        const string FakeUid = "0xfakeschemauid123";
        var worker = new RiskAttestProSchemaBootstrapWorker(
            _db,
            NullLogger<RiskAttestProSchemaBootstrapWorker>.Instance,
            registerInjector: _ => Task.FromResult(FakeUid));

        await worker.StartAsync(CancellationToken.None);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT schema_uid FROM risk_attest_pro_bootstrap_state LIMIT 1;";
        var stored = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal(FakeUid, stored);
    }

    [Fact]
    public async Task SecondBoot_reuses_existing_schema_uid()
    {
        var callCount = 0;
        Func<string, Task<string>> injector = _ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult("0xfakeschemauid456");
        };

        var firstWorker = new RiskAttestProSchemaBootstrapWorker(
            _db, NullLogger<RiskAttestProSchemaBootstrapWorker>.Instance, injector);
        await firstWorker.StartAsync(CancellationToken.None);
        Assert.Equal(1, callCount);

        // Fresh instance over the same DB simulates a second boot.
        var secondWorker = new RiskAttestProSchemaBootstrapWorker(
            _db, NullLogger<RiskAttestProSchemaBootstrapWorker>.Instance, injector);
        await secondWorker.StartAsync(CancellationToken.None);

        Assert.Equal(1, callCount); // Existing UID short-circuits the injector.
    }
}
