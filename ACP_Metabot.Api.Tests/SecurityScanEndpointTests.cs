using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Endpoints;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// POST /admin/securityScan endpoint behaviour. The endpoint is registered in
/// Program.cs as a thin wrapper around the static SecurityScanEndpoint.HandleAsync;
/// tests exercise the helper directly (no WebApplicationFactory in this project,
/// see RiskAttestProEndpointTests). 401-without-key is enforced by the X-API-Key
/// middleware (/admin/* is gated) and is not reachable at this layer.
/// </summary>
public class SecurityScanEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;
    private readonly SecurityScanHistoryRepository _histRepo;

    public SecurityScanEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_endpt_secscan_{Guid.NewGuid():N}.db");
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

    // A scan delegate matching SecurityScanService.ScanAndPersistAsync's shape.
    // The shim writes through the real repos (so we can assert both tables) and
    // returns a synthetic ScanResult.
    private Func<string, SecurityVerdictRepository, SecurityScanHistoryRepository,
        CancellationToken, Task<ScanResult>> ScanDelegate(
            Func<string, ScanResult> produce)
        => async (addr, repo, hist, ct) =>
        {
            var result = produce(addr);
            var v = result.Verdict;
            await repo.UpsertAsync(v, ct);
            await hist.AppendAsync(v.AgentAddress, v.ScannedAt, v.Status, v.Score, v.Grade,
                v.ObservableCount, v.FindingCount, v.SeverityCountsJson, result.RawVerdict,
                v.CorpusVersion, result.RawFindingsJson, v.LastError, ct);
            return result;
        };

    private static ScanResult ScannedResult(string addr) => new(
        new SecurityVerdict(addr, SecurityStatus.Scanned, 88, "B", 11, 1, "{\"High\":1}",
            "2026-06-09T10:00:00.0000000Z", null, null),
        RawFindingsJson: "[{\"patternId\":\"P9\",\"title\":\"RPC url leak\",\"severity\":\"High\",\"verdict\":\"Present\",\"evidence\":\"alchemy key in log\",\"fixRef\":\"P9\"}]",
        RawVerdict: "PASS");

    private static async Task<(int StatusCode, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var bodyStream = new MemoryStream();
        ctx.Response.Body = bodyStream;
        await result.ExecuteAsync(ctx);
        bodyStream.Position = 0;
        if (bodyStream.Length == 0)
            return (ctx.Response.StatusCode == 0 ? 200 : ctx.Response.StatusCode,
                JsonDocument.Parse("{}").RootElement.Clone());
        var doc = await JsonDocument.ParseAsync(bodyStream);
        return (ctx.Response.StatusCode == 0 ? 200 : ctx.Response.StatusCode,
            doc.RootElement.Clone());
    }

    [Fact]
    public async Task BadAddress_returns_400()
    {
        var req = new AdminSecurityScanRequest("not-an-address");
        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(ScannedResult), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(400, status);
        Assert.Equal("invalid_address", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingAddress_returns_400()
    {
        var req = new AdminSecurityScanRequest(null);
        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(ScannedResult), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(400, status);
        Assert.Equal("invalid_address", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Scanned_returns_200_with_verdict_findings_and_writes_both_tables()
    {
        var addr = "0xABC0000000000000000000000000000000000ABC";
        var req = new AdminSecurityScanRequest(addr);

        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(ScannedResult), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status);
        Assert.Equal("0xabc0000000000000000000000000000000000abc", body.GetProperty("agentAddress").GetString());
        Assert.Equal(SecurityStatus.Scanned, body.GetProperty("status").GetString());
        Assert.Equal(88, body.GetProperty("score").GetInt32());
        Assert.Equal("B", body.GetProperty("grade").GetString());
        Assert.Equal(11, body.GetProperty("observableCount").GetInt32());
        Assert.Equal(1, body.GetProperty("findingCount").GetInt32());
        Assert.Equal("PASS", body.GetProperty("verdict").GetString());
        Assert.Equal("2026-06-09T10:00:00.0000000Z", body.GetProperty("scannedAt").GetString());

        // severityCounts is an OBJECT parsed from severity_counts JSON.
        var sev = body.GetProperty("severityCounts");
        Assert.Equal(JsonValueKind.Object, sev.ValueKind);
        Assert.Equal(1, sev.GetProperty("High").GetInt32());

        // findings is the parsed array from RawFindingsJson, full per-finding detail.
        var findings = body.GetProperty("findings");
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
        Assert.Equal(1, findings.GetArrayLength());
        var f0 = findings[0];
        Assert.Equal("P9", f0.GetProperty("patternId").GetString());
        Assert.Equal("alchemy key in log", f0.GetProperty("evidence").GetString());
        Assert.Equal("P9", f0.GetProperty("fixRef").GetString());

        // Both tables written (lower-cased).
        var cached = await _repo.GetByAgentAsync(addr);
        Assert.NotNull(cached);
        Assert.Equal(88, cached!.Score);
        var hist = await _histRepo.ListByAgentAsync(addr);
        Assert.Single(hist);

        // lastError must NEVER appear in the response body (P30/P63).
        var json = body.GetRawText();
        Assert.DoesNotContain("lastError", json);
    }

    [Fact]
    public async Task NullFindingsJson_returns_empty_findings_array()
    {
        var addr = "0xDEF0000000000000000000000000000000000DEF";
        var req = new AdminSecurityScanRequest(addr);
        ScanResult Produce(string a) => new(
            new SecurityVerdict(a, SecurityStatus.Scanned, 90, "A", 11, 0, "{}",
                "2026-06-09T11:00:00.0000000Z", null, null),
            RawFindingsJson: null, RawVerdict: "PASS");

        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(Produce), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status);
        var findings = body.GetProperty("findings");
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
        Assert.Equal(0, findings.GetArrayLength()); // null RawFindingsJson -> []
    }

    [Fact]
    public async Task NotAuditable_returns_200_with_status_and_empty_findings_no_lastError()
    {
        var addr = "0x1230000000000000000000000000000000000123";
        var req = new AdminSecurityScanRequest(addr);
        ScanResult Produce(string a) => new(
            new SecurityVerdict(a, SecurityStatus.NotAuditable, null, null, null, null, null,
                "2026-06-09T12:00:00.0000000Z", null, null),
            RawFindingsJson: null, RawVerdict: "NOT_AUDITABLE");

        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(Produce), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status); // honest outcome, not a 500
        Assert.Equal(SecurityStatus.NotAuditable, body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("findings").GetArrayLength());
        Assert.DoesNotContain("lastError", body.GetRawText());
    }

    [Fact]
    public async Task Error_returns_200_with_status_and_no_lastError_leak()
    {
        var addr = "0x4560000000000000000000000000000000000456";
        var req = new AdminSecurityScanRequest(addr);
        ScanResult Produce(string a) => new(
            new SecurityVerdict(a, SecurityStatus.Error, null, null, null, null, null,
                "2026-06-09T13:00:00.0000000Z", null, "HTTP 500 from securitybot"),
            RawFindingsJson: null, RawVerdict: null);

        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(Produce), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status); // never a 500
        Assert.Equal(SecurityStatus.Error, body.GetProperty("status").GetString());
        // last_error is stored in the history table (server-side) but NEVER in the response.
        var json = body.GetRawText();
        Assert.DoesNotContain("lastError", json);
        Assert.DoesNotContain("HTTP 500 from securitybot", json);
        var hist = await _histRepo.ListByAgentAsync(addr);
        Assert.Equal("HTTP 500 from securitybot", hist[0].LastError); // server-side only
    }
}
