using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class SecurityScanHistoryRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityScanHistoryRepository _repo;

    public SecurityScanHistoryRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_secscanhist_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new SecurityScanHistoryRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task Append_ThenList_RoundtripsAndLowercases()
    {
        var iso = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        var findings = "[{\"patternId\":\"P9\",\"severity\":\"High\",\"evidence\":\"x\"}]";
        await _repo.AppendAsync("0xABC", iso, SecurityStatus.Scanned, 90, "A", 11, 1, "{\"High\":1}", "PASS", "2026-06-08", findings, null);

        var rows = await _repo.ListByAgentAsync("0xabc");
        Assert.Single(rows);
        Assert.Equal("0xabc", rows[0].AgentAddress);          // lower-cased on write
        Assert.Equal(SecurityStatus.Scanned, rows[0].Status);
        Assert.Equal(90, rows[0].Score);
        Assert.Equal(findings, rows[0].FindingsJson);          // full findings JSON round-trips
        Assert.Equal("PASS", rows[0].Verdict);
    }

    [Fact]
    public async Task Append_Twice_SameAgent_ProducesTwoRows_NotOverwrite()
    {
        var iso1 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        var iso2 = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        await _repo.AppendAsync("0xa", iso1, SecurityStatus.Scanned, 70, "C", 11, 2, "{}", "PASS", null, "[]", null);
        await _repo.AppendAsync("0xa", iso2, SecurityStatus.Scanned, 95, "A", 11, 0, "{}", "PASS", null, "[]", null);

        var rows = await _repo.ListByAgentAsync("0xa");
        Assert.Equal(2, rows.Count);                           // append, not upsert
        Assert.Equal(iso2, rows[0].ScannedAt);                 // newest first
        Assert.Equal(95, rows[0].Score);
        Assert.Equal(70, rows[1].Score);
    }

    [Fact]
    public async Task Append_ErrorScan_StoresNullFindings_AndReason()
    {
        var iso = DateTime.UtcNow.ToString("O");
        await _repo.AppendAsync("0xe", iso, SecurityStatus.Error, null, null, null, null, null, null, null, null, "HTTP 500");
        var rows = await _repo.ListByAgentAsync("0xe");
        Assert.Single(rows);
        Assert.Equal(SecurityStatus.Error, rows[0].Status);
        Assert.Null(rows[0].FindingsJson);
        Assert.Equal("HTTP 500", rows[0].LastError);
    }

    [Fact]
    public async Task List_RespectsLimit_NewestFirst()
    {
        for (int i = 0; i < 5; i++)
            await _repo.AppendAsync("0xa", new DateTime(2026, 6, 1 + i, 0, 0, 0, DateTimeKind.Utc).ToString("O"),
                SecurityStatus.Scanned, 80 + i, "B", 11, 0, "{}", "PASS", null, "[]", null);
        var rows = await _repo.ListByAgentAsync("0xa", limit: 2);
        Assert.Equal(2, rows.Count);
        Assert.Equal(84, rows[0].Score);                       // most recent
    }

    [Fact]
    public async Task List_UnknownAgent_ReturnsEmpty()
        => Assert.Empty(await _repo.ListByAgentAsync("0xnope"));
}
