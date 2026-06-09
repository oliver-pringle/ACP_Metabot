using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class SecurityScanWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;
    private readonly SecurityScanHistoryRepository _histRepo;
    private readonly ServiceCollection _services;
    private ServiceProvider _sp;

    public SecurityScanWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_secworker_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"]      = $"Data Source={_dbPath}",
                ["SECURITY_SCAN_ENABLED"]         = "true",
                ["SECURITY_SCAN_BATCH"]           = "10",
                ["SECURITY_SCAN_DELAY_SECONDS"]   = "0", // no real delay in tests
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new SecurityVerdictRepository(_db);
        _histRepo = new SecurityScanHistoryRepository(_db);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(_db);
        services.AddSingleton(_repo);
        services.AddSingleton(_histRepo); // worker scope resolves it for the per-scan append
        // The worker resolves SecurityScanService from its per-tick scope. It is
        // constructed per-test from the FakeClient passed to MakeWorker (so the
        // service scans through the same fake the test inspects), then registered
        // as the scope-resolvable singleton.
        _services = services; // keep the collection so MakeWorker can register the per-test service
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private async Task InsertOfferingAsync(string addr)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var iso = DateTime.UtcNow.ToString("O");
        cmd.CommandText = @"
            INSERT INTO offerings
                (agent_address, agent_name, offering_name, description, price_usdc,
                 price_type, chain, content_hash, first_seen_at, last_seen_at, usage_count, is_removed)
            VALUES ($a, 'n', 'o-' || $a, 'd', 1.0, 'per_call', 'base', $a, $i, $i, 0, 0);";
        cmd.Parameters.AddWithValue("$a", addr);
        cmd.Parameters.AddWithValue("$i", iso);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class FakeClient : ITheSecurityBotClient
    {
        public readonly List<string> Scanned = new();
        public Func<string, SecurityVerdict>? Map;            // returns the cache verdict; wrapped in ScanResult
        public string? RawFindingsJson = "[{\"severity\":\"High\"}]";
        public Task<ScanResult> ScanAsync(string agentAddress, CancellationToken ct = default)
        {
            Scanned.Add(agentAddress);
            var v = Map?.Invoke(agentAddress)
                ?? new SecurityVerdict(agentAddress, SecurityStatus.Scanned, 90, "A", 11, 0, "{}",
                    DateTime.UtcNow.ToString("O"), null, null);
            return Task.FromResult(new ScanResult(v, RawFindingsJson, "PASS"));
        }
    }

    private SecurityScanWorker MakeWorker(ITheSecurityBotClient client)
    {
        // The worker no longer depends on ITheSecurityBotClient — its scope-resolved
        // SecurityScanService does. Register THIS test's FakeClient + the service so
        // the worker's per-tick scope resolves a service that scans through the same
        // fake the test inspects, then rebuild the provider.
        _sp.Dispose();
        _services.AddSingleton<ITheSecurityBotClient>(client);
        _services.AddSingleton<SecurityScanService>();
        _sp = _services.BuildServiceProvider();

        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var config = _sp.GetRequiredService<IConfiguration>();
        return new SecurityScanWorker(scopeFactory, config, NullLogger<SecurityScanWorker>.Instance);
    }

    [Fact]
    public async Task Tick_ScansStaleAgents_UpsertsVerdicts()
    {
        await InsertOfferingAsync("0xa");
        await InsertOfferingAsync("0xb");
        var client = new FakeClient();
        var worker = MakeWorker(client);

        var n = await worker.TickOnceAsync(CancellationToken.None);

        Assert.Equal(2, n);
        Assert.Equal(2, client.Scanned.Count);
        Assert.NotNull(await _repo.GetByAgentAsync("0xa"));
        Assert.NotNull(await _repo.GetByAgentAsync("0xb"));
    }

    [Fact]
    public async Task Tick_SkipsFreshlyScannedAgents()
    {
        await InsertOfferingAsync("0xfresh");
        await _repo.UpsertAsync(new SecurityVerdict("0xfresh", SecurityStatus.Scanned, 80, "B",
            11, 0, "{}", DateTime.UtcNow.ToString("O"), null, null));
        var client = new FakeClient();
        var worker = MakeWorker(client);

        var n = await worker.TickOnceAsync(CancellationToken.None);

        Assert.Equal(0, n);
        Assert.Empty(client.Scanned);
    }

    [Fact]
    public async Task Tick_RespectsBatchLimit()
    {
        for (int i = 0; i < 15; i++) await InsertOfferingAsync($"0x{i:x2}");
        var client = new FakeClient();
        var worker = MakeWorker(client);

        var n = await worker.TickOnceAsync(CancellationToken.None);

        Assert.Equal(10, n); // SECURITY_SCAN_BATCH = 10
        Assert.Equal(10, client.Scanned.Count);
    }

    [Fact]
    public async Task Tick_PersistsErrorVerdict_WhenClientReportsError()
    {
        await InsertOfferingAsync("0xerr");
        var client = new FakeClient
        {
            Map = a => new SecurityVerdict(a, SecurityStatus.Error, null, null, null, null, null,
                DateTime.UtcNow.ToString("O"), null, "HTTP 500")
        };
        var worker = MakeWorker(client);

        await worker.TickOnceAsync(CancellationToken.None);

        var row = await _repo.GetByAgentAsync("0xerr");
        Assert.Equal(SecurityStatus.Error, row!.Status);
    }

    [Fact]
    public async Task Tick_AppendsHistoryRow_PerScan()
    {
        await InsertOfferingAsync("0xa");
        var worker = MakeWorker(new FakeClient());

        await worker.TickOnceAsync(CancellationToken.None);

        var hist = await _histRepo.ListByAgentAsync("0xa");
        Assert.Single(hist);
        Assert.Equal("[{\"severity\":\"High\"}]", hist[0].FindingsJson); // full findings retained
        Assert.NotNull(await _repo.GetByAgentAsync("0xa"));              // latest cache still present
    }

    [Fact]
    public async Task Tick_RescanSameAgent_TwoHistoryRows_OneCacheRow()
    {
        await InsertOfferingAsync("0xa");
        var worker = MakeWorker(new FakeClient());

        // First scan (agent never-scanned -> stale).
        await worker.TickOnceAsync(CancellationToken.None);
        // Age the cache row past the 7-day scanned TTL so the agent is stale again,
        // then scan a second time.
        await _repo.UpsertAsync(new SecurityVerdict("0xa", SecurityStatus.Scanned, 90, "A", 11, 0, "{}",
            DateTime.UtcNow.AddDays(-10).ToString("O"), null, null));
        await worker.TickOnceAsync(CancellationToken.None);

        Assert.Equal(2, (await _histRepo.ListByAgentAsync("0xa")).Count); // append: two history rows
        Assert.NotNull(await _repo.GetByAgentAsync("0xa"));               // upsert: single cache row
    }
}
