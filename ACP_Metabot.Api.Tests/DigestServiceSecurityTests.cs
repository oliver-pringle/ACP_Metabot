using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class DigestServiceSecurityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _offerings;
    private readonly SecurityVerdictRepository _secRepo;

    public DigestServiceSecurityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_digsec_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _offerings = new OfferingRepository(_db);
        _secRepo = new SecurityVerdictRepository(_db);
    }

    public void Dispose()
    {
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
                 price_type, chain, content_hash, first_seen_at, last_seen_at, usage_count,
                 marketplace_version, is_removed)
            VALUES ($a, 'AgentN', 'off-' || $a, 'desc', 1.0, 'per_call', 'base', $a, $i, $i, 0, 'v2', 0);";
        cmd.Parameters.AddWithValue("$a", addr);
        cmd.Parameters.AddWithValue("$i", iso);
        await cmd.ExecuteNonQueryAsync();
    }

    private DigestService MakeService()
    {
        var satCalc = new SaturationCalculator(threshold: 0.85);
        var repSvc = new ReputationService(
            new AgentReputationCacheRepository(_db),
            new AgentReputationHistoryRepository(_db),
            new LifetimeSnapshotRepository(_db),
            null!, null!, new ScoreCalculator(), _offerings,
            NullLogger<ReputationService>.Instance);
        return new DigestService(_offerings, repSvc, satCalc, resourcesRepo: null, securityRepo: _secRepo);
    }

    [Fact]
    public async Task Digest_AttachesSecurityVerdict_WhenPresent()
    {
        await InsertOfferingAsync("0xa");
        await _secRepo.UpsertAsync(new SecurityVerdict("0xa", SecurityStatus.Scanned, 91, "A",
            11, 0, "{}", DateTime.UtcNow.ToString("O"), null, null));

        var svc = MakeService();
        var result = await svc.BuildAsync(windowDays: 1, marketplaceFilter: null,
            chainFilter: null, priceMaxUsdc: null, includeSecurity: true);

        var off = result.NewOfferings.Single(o => o.AgentAddress.Equals("0xa", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(off.Security);
        Assert.Equal(SecurityStatus.Scanned, off.Security!.Status);
        Assert.Equal(91, off.Security.Score);
        Assert.Equal("A", off.Security.Grade);

        // P9/P10 leak-guard: the digest reads ONLY the security_verdicts cache —
        // raw findings/evidence/last_error from the history store must never appear.
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("findingsJson", json);
        Assert.DoesNotContain("evidence", json);
        Assert.DoesNotContain("lastError", json);
    }

    [Fact]
    public async Task Digest_Pending_WhenNoVerdict()
    {
        await InsertOfferingAsync("0xb");
        var svc = MakeService();
        var result = await svc.BuildAsync(1, null, null, null, includeSecurity: true);

        var off = result.NewOfferings.Single(o => o.AgentAddress.Equals("0xb", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SecurityStatus.Pending, off.Security!.Status);
    }

    [Fact]
    public async Task Digest_OmitsSecurity_WhenIncludeFalse()
    {
        await InsertOfferingAsync("0xc");
        var svc = MakeService();
        var result = await svc.BuildAsync(1, null, null, null, includeSecurity: false);

        var off = result.NewOfferings.Single(o => o.AgentAddress.Equals("0xc", StringComparison.OrdinalIgnoreCase));
        Assert.Null(off.Security);
    }
}
