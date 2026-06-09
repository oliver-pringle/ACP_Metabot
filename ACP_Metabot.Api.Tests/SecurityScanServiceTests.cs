using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class SecurityScanServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;
    private readonly SecurityScanHistoryRepository _histRepo;

    public SecurityScanServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_secsvc_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new SecurityVerdictRepository(_db);
        _histRepo = new SecurityScanHistoryRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    // Fake client: records each scanned address, returns a scanned verdict + raw
    // findings JSON + raw verdict discriminator (same ScanResult shape the worker uses).
    private sealed class FakeClient : ITheSecurityBotClient
    {
        public readonly List<string> Scanned = new();
        public Func<string, SecurityVerdict>? Map;
        public string? RawFindingsJson = "[{\"patternId\":\"P9\",\"severity\":\"High\"}]";
        public string? RawVerdict = "PASS";
        public Task<ScanResult> ScanAsync(string agentAddress, CancellationToken ct = default)
        {
            Scanned.Add(agentAddress);
            var v = Map?.Invoke(agentAddress)
                ?? new SecurityVerdict(agentAddress, SecurityStatus.Scanned, 90, "A", 11, 1, "{\"High\":1}",
                    DateTime.UtcNow.ToString("O"), null, null);
            return Task.FromResult(new ScanResult(v, RawFindingsJson, RawVerdict));
        }
    }

    [Fact]
    public async Task ScanAndPersist_UpsertsCache_AppendsOneHistoryRow_ReturnsResult()
    {
        var client = new FakeClient();
        var svc = new SecurityScanService(client);

        var result = await svc.ScanAndPersistAsync("0xabc", _repo, _histRepo, CancellationToken.None);

        // Returns the ScanResult the client produced.
        Assert.NotNull(result);
        Assert.Equal(SecurityStatus.Scanned, result.Verdict.Status);
        Assert.Equal("[{\"patternId\":\"P9\",\"severity\":\"High\"}]", result.RawFindingsJson);
        Assert.Equal("PASS", result.RawVerdict);
        Assert.Single(client.Scanned);

        // (a) latest-verdict cache upserted.
        var cached = await _repo.GetByAgentAsync("0xabc");
        Assert.NotNull(cached);
        Assert.Equal(90, cached!.Score);

        // (b) exactly one history row appended, retaining the full findings JSON.
        var hist = await _histRepo.ListByAgentAsync("0xabc");
        Assert.Single(hist);
        Assert.Equal("[{\"patternId\":\"P9\",\"severity\":\"High\"}]", hist[0].FindingsJson);
        Assert.Equal("PASS", hist[0].Verdict);
    }

    [Fact]
    public async Task ScanAndPersist_Rerun_AppendsSecondHistoryRow_OneCacheRow()
    {
        var client = new FakeClient();
        var svc = new SecurityScanService(client);

        await svc.ScanAndPersistAsync("0xa", _repo, _histRepo, CancellationToken.None);
        await svc.ScanAndPersistAsync("0xa", _repo, _histRepo, CancellationToken.None);

        // append: two history rows.
        Assert.Equal(2, (await _histRepo.ListByAgentAsync("0xa")).Count);
        // upsert: single cache row (GetByAgent returns the latest, not a duplicate).
        Assert.NotNull(await _repo.GetByAgentAsync("0xa"));
    }

    [Fact]
    public async Task ScanAndPersist_ErrorVerdict_StillPersistsCacheAndHistory()
    {
        var client = new FakeClient
        {
            Map = a => new SecurityVerdict(a, SecurityStatus.Error, null, null, null, null, null,
                DateTime.UtcNow.ToString("O"), null, "HTTP 500"),
            RawFindingsJson = null,
            RawVerdict = null,
        };
        var svc = new SecurityScanService(client);

        var result = await svc.ScanAndPersistAsync("0xerr", _repo, _histRepo, CancellationToken.None);

        Assert.Equal(SecurityStatus.Error, result.Verdict.Status);
        var cached = await _repo.GetByAgentAsync("0xerr");
        Assert.Equal(SecurityStatus.Error, cached!.Status);
        var hist = await _histRepo.ListByAgentAsync("0xerr");
        Assert.Single(hist);
        Assert.Equal(SecurityStatus.Error, hist[0].Status);
        Assert.Null(hist[0].FindingsJson);
        Assert.Equal("HTTP 500", hist[0].LastError); // stored server-side in history
    }
}
