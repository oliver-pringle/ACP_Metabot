using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Endpoints;
using ACP_Metabot.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// Tests for the PUBLIC, summary-only security-scan history read endpoint.
/// The headline guarantee: the raw findings[] JSON and the internal last_error
/// string are NEVER present in the projected output (P9/P10/P30/P63).
/// </summary>
public class SecurityScanHistoryEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityScanHistoryRepository _repo;

    public SecurityScanHistoryEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_secscanhist_ep_test_{Guid.NewGuid():N}.db");
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

    // Mirror the ASP.NET web defaults (camelCase) so the serialized shape inspected
    // here matches what the real HTTP pipeline emits from Results.Ok(...).
    private static readonly JsonSerializerOptions Camel =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- pure projection ----------------------------------------------------

    [Fact]
    public void Project_NeverLeaksRawFindingsOrLastError()
    {
        var row = new ScanHistoryRow(
            Id: 1, AgentAddress: "0xabc", ScannedAt: "2026-06-08T00:00:00.0000000Z",
            Status: SecurityStatus.Scanned, Score: 90, Grade: "A",
            ObservableCount: 11, FindingCount: 1, SeverityCountsJson: "{\"High\":1}",
            Verdict: "PASS", CorpusVersion: "2026-06-08",
            FindingsJson: "[{\"patternId\":\"P9\",\"evidence\":\"SECRET_EVIDENCE_LEAK\"}]",
            LastError: "INTERNAL_STACKTRACE_BOOM");

        var summary = SecurityScanHistoryEndpoint.Project(new[] { row });
        var json = JsonSerializer.Serialize(summary);

        // The raw finding evidence and the internal error string must be absent.
        Assert.DoesNotContain("SECRET_EVIDENCE_LEAK", json);
        Assert.DoesNotContain("INTERNAL_STACKTRACE_BOOM", json);
        Assert.DoesNotContain("findings", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastError", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("last_error", json, StringComparison.OrdinalIgnoreCase);

        // The summary fields ARE present.
        var s = Assert.Single(summary);
        Assert.Equal(90, s.Score);
        Assert.Equal("A", s.Grade);
        Assert.Equal("PASS", s.Verdict);
        Assert.Equal(1, s.FindingCount);
        Assert.Equal(JsonValueKind.Object, s.SeverityCounts.ValueKind);   // inline object, not a string
        Assert.Equal(1, s.SeverityCounts.GetProperty("High").GetInt32());
    }

    [Fact]
    public void Project_NullOrBadSeverityCounts_BecomesEmptyObject()
    {
        var mk = (string? sev) => new ScanHistoryRow(
            1, "0xa", "2026-06-08T00:00:00Z", SecurityStatus.Error,
            null, null, null, null, sev, null, null, null, null);

        foreach (var bad in new string?[] { null, "", "not json", "[1,2]" })
        {
            var s = Assert.Single(SecurityScanHistoryEndpoint.Project(new[] { mk(bad) }));
            Assert.Equal(JsonValueKind.Object, s.SeverityCounts.ValueKind);
            Assert.False(s.SeverityCounts.EnumerateObject().Any());  // {}
        }
    }

    // ---- HandleAsync validation --------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0xnothex")]
    [InlineData("0x123")]                                  // too short
    [InlineData("not-an-address")]
    public async Task HandleAsync_InvalidAddress_Returns400(string? agent)
    {
        var res = await SecurityScanHistoryEndpoint.HandleAsync(agent, null, _repo, default);
        var sc = Assert.IsAssignableFrom<IStatusCodeHttpResult>(res);
        Assert.Equal(400, sc.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_UnknownAgent_Returns200EmptyHistory()
    {
        var res = await SecurityScanHistoryEndpoint.HandleAsync(
            "0x1111111111111111111111111111111111111111", null, _repo, default);
        var ok = Assert.IsAssignableFrom<IValueHttpResult>(res);
        var json = JsonSerializer.Serialize(ok.Value, Camel);
        Assert.Contains("\"count\":0", json);
    }

    [Fact]
    public async Task HandleAsync_Valid_ReturnsNewestFirst_NoLeak_LowerCased()
    {
        const string mixed = "0xAbCdef0000000000000000000000000000000001";
        var lower = mixed.ToLowerInvariant();
        var iso1 = "2026-06-01T00:00:00.0000000Z";
        var iso2 = "2026-06-02T00:00:00.0000000Z";
        await _repo.AppendAsync(mixed, iso1, SecurityStatus.Scanned, 70, "C", 11, 2, "{\"Medium\":2}", "PASS", "2026-06-08",
            "[{\"evidence\":\"SECRET_EVIDENCE_LEAK\"}]", "INTERNAL_STACKTRACE_BOOM");
        await _repo.AppendAsync(mixed, iso2, SecurityStatus.Scanned, 95, "A", 11, 0, "{}", "PASS", "2026-06-08",
            "[]", null);

        var res = await SecurityScanHistoryEndpoint.HandleAsync(mixed, null, _repo, default);
        var ok = Assert.IsAssignableFrom<IValueHttpResult>(res);
        var json = JsonSerializer.Serialize(ok.Value, Camel);

        Assert.Contains("\"count\":2", json);
        Assert.Contains(lower, json);                              // address normalised lower-case
        Assert.DoesNotContain("SECRET_EVIDENCE_LEAK", json);       // P9/P10 — no raw findings end-to-end
        Assert.DoesNotContain("INTERNAL_STACKTRACE_BOOM", json);   // P30/P63 — no internal error
        // newest-first: the 95/A scan precedes the 70/C scan in the serialized order.
        Assert.True(json.IndexOf("\"score\":95", StringComparison.Ordinal)
                  < json.IndexOf("\"score\":70", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_ClampsLimit_BelowOne_To1()
    {
        const string a = "0x2222222222222222222222222222222222222222";
        for (int i = 0; i < 3; i++)
            await _repo.AppendAsync(a, $"2026-06-0{i + 1}T00:00:00.0000000Z",
                SecurityStatus.Scanned, 80 + i, "B", 11, 0, "{}", "PASS", null, "[]", null);

        var res = await SecurityScanHistoryEndpoint.HandleAsync(a, 0, _repo, default);   // limit 0 -> clamp to 1
        var ok = Assert.IsAssignableFrom<IValueHttpResult>(res);
        Assert.Contains("\"count\":1", JsonSerializer.Serialize(ok.Value, Camel));
    }
}
