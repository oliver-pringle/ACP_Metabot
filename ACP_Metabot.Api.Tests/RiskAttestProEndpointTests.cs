using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Endpoints;
using ACP_Metabot.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.0 riskAttestPro Task 8: POST /v1/risk/attest-pro endpoint behavior.
///
/// The endpoint is registered in Program.cs as a thin wrapper around the
/// static helper <see cref="RiskAttestProEndpoint.HandleAsync"/>. Tests
/// exercise the helper directly so the cache-read / service-call /
/// cache-write / 502-on-floor-breach behavior is asserted without a
/// WebApplicationFactory plumb (none exists in this test project — none
/// of the other endpoints are unit-tested via HTTP either, see the
/// RiskAttestProServiceTests pattern for the same kind of inline-fake
/// orchestration).
/// </summary>
public class RiskAttestProEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public RiskAttestProEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_endpt_attestpro_test_{Guid.NewGuid():N}.db");
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

    // ── Test seam: a service shim that lets us count invocations and
    //   either return a synthetic RiskAttestProResult OR throw
    //   InsufficientSignalsException. Implements the same call shape as
    //   the real RiskAttestProService.GenerateAsync so the endpoint's
    //   handler signature stays unchanged. We pass it as a delegate to
    //   HandleAsync (the endpoint factors GenerateAsync into a Func to
    //   keep this kind of unit test tractable).

    private sealed class CountingServiceShim
    {
        public int Calls;
        public Func<string, string, CancellationToken, Task<RiskAttestProResult>> Handler { get; set; } =
            (w, c, _) =>
            {
                throw new InvalidOperationException(
                    "CountingServiceShim.Handler not set");
            };

        public Task<RiskAttestProResult> InvokeAsync(string wallet, string chain, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Handler(wallet, chain, ct);
        }
    }

    private static RiskAttestProResult SyntheticOkResult(string wallet, string chain)
    {
        var componentsObj = JsonSerializer.SerializeToDocument(new
        {
            healthFactor = new { score = 70, source = "LiquidGuard", status = "fresh" },
            approvals = new { score = 75, source = "RevokeBot", status = "fresh" },
            mev = new { score = 80, source = "MEVProtect", status = "fresh" },
            reputation = new { score = 70, source = "TheMetaBot", status = "fresh" },
            arena = new { score = 60, source = "TheArenaBot", status = "fresh" },
            witness = new { score = 65, source = "TheWitnessBot", status = "fresh" },
            trajectory = new { score = 68, source = "history", status = "fresh" },
        });
        var recs = JsonSerializer.SerializeToDocument(Array.Empty<object>());
        var now = DateTimeOffset.UtcNow;
        return new RiskAttestProResult(
            Verdict: "OK",
            ScorePro: 70,
            Grade: "B",
            Components: componentsObj.RootElement.Clone(),
            ExecutiveSummary: "stubbed narration",
            Recommendations: recs.RootElement.Clone(),
            MarkdownReportBase64: Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("# riskAttestPro report\n")),
            Wallet: wallet,
            Chain: chain,
            GeneratedAt: now.UtcDateTime.ToString("O"),
            ExpiresAt: now.AddHours(24).UtcDateTime.ToString("O"),
            SourcesQueried: new[] { "LiquidGuard", "RevokeBot", "MEVProtect", "TheMetaBot", "TheArenaBot", "TheWitnessBot", "history" },
            SourcesUnavailable: Array.Empty<string>(),
            ComponentsHash: "abc123");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<int> CountCacheRowsAsync(string walletChain)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM risk_attest_pro_cache WHERE wallet_chain = $w;";
        cmd.Parameters.AddWithValue("$w", walletChain);
        var raw = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(raw ?? 0);
    }

    /// <summary>
    /// Pulls the response body off an IResult (JSON-encoded). The endpoint
    /// uses Results.Json / Results.Ok which produce typed result records
    /// carrying a Value/Object; we round-trip through ExecuteAsync on a
    /// fake HttpContext to capture the actual wire JSON.
    /// </summary>
    private static async Task<(int StatusCode, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var bodyStream = new MemoryStream();
        ctx.Response.Body = bodyStream;
        await result.ExecuteAsync(ctx);
        bodyStream.Position = 0;
        if (bodyStream.Length == 0)
        {
            return (ctx.Response.StatusCode == 0 ? 200 : ctx.Response.StatusCode,
                JsonDocument.Parse("{}").RootElement.Clone());
        }
        var doc = await JsonDocument.ParseAsync(bodyStream);
        return (ctx.Response.StatusCode == 0 ? 200 : ctx.Response.StatusCode,
            doc.RootElement.Clone());
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_returns_200_with_verdict_and_attestation_fields()
    {
        var shim = new CountingServiceShim
        {
            Handler = (w, c, _) => Task.FromResult(SyntheticOkResult(w, c)),
        };
        var req = new RiskAttestProRequest("0xABC0000000000000000000000000000000000abc", "base", null, false);

        var result = await RiskAttestProEndpoint.HandleAsync(
            req, _db, shim.InvokeAsync, CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status);
        Assert.Equal("OK", body.GetProperty("verdict").GetString());
        Assert.Equal(70, body.GetProperty("scorePro").GetInt32());
        Assert.Equal("B", body.GetProperty("grade").GetString());
        Assert.Equal("abc123", body.GetProperty("componentsHash").GetString());
        Assert.False(body.GetProperty("cacheHit").GetBoolean());

        // Cache row was written (wallet lowercased + ":" + chain).
        var rows = await CountCacheRowsAsync(
            "0xabc0000000000000000000000000000000000abc:base");
        Assert.Equal(1, rows);
        Assert.Equal(1, shim.Calls);
    }

    [Fact]
    public async Task SecondHire_within_1h_returns_cached_response()
    {
        var shim = new CountingServiceShim
        {
            Handler = (w, c, _) => Task.FromResult(SyntheticOkResult(w, c)),
        };
        var req = new RiskAttestProRequest("0xDEF0000000000000000000000000000000000def", "base", null, false);

        // Warm the cache.
        var r1 = await RiskAttestProEndpoint.HandleAsync(
            req, _db, shim.InvokeAsync, CancellationToken.None);
        var (status1, body1) = await ExecuteAsync(r1);
        Assert.Equal(200, status1);
        Assert.False(body1.GetProperty("cacheHit").GetBoolean());
        Assert.Equal(1, shim.Calls);

        // Second hire: service must NOT be invoked.
        var r2 = await RiskAttestProEndpoint.HandleAsync(
            req, _db, shim.InvokeAsync, CancellationToken.None);
        var (status2, body2) = await ExecuteAsync(r2);
        Assert.Equal(200, status2);
        Assert.True(body2.GetProperty("cacheHit").GetBoolean());
        Assert.Equal("OK", body2.GetProperty("verdict").GetString());
        Assert.Equal(1, shim.Calls); // still 1 — second call hit cache
    }

    [Fact]
    public async Task FreshQuery_param_bypasses_cache()
    {
        var shim = new CountingServiceShim
        {
            Handler = (w, c, _) => Task.FromResult(SyntheticOkResult(w, c)),
        };
        var addr = "0x1230000000000000000000000000000000000123";
        var req1 = new RiskAttestProRequest(addr, "base", null, false);
        var req2 = new RiskAttestProRequest(addr, "base", null, true);

        // Warm the cache.
        var r1 = await RiskAttestProEndpoint.HandleAsync(
            req1, _db, shim.InvokeAsync, CancellationToken.None);
        var (s1, _) = await ExecuteAsync(r1);
        Assert.Equal(200, s1);
        Assert.Equal(1, shim.Calls);

        // fresh=true must skip the cache.
        var r2 = await RiskAttestProEndpoint.HandleAsync(
            req2, _db, shim.InvokeAsync, CancellationToken.None);
        var (s2, body2) = await ExecuteAsync(r2);
        Assert.Equal(200, s2);
        Assert.False(body2.GetProperty("cacheHit").GetBoolean());
        Assert.Equal(2, shim.Calls); // service called again
    }

    [Fact]
    public async Task BelowFloor_returns_502_with_INSUFFICIENT_SIGNALS_envelope()
    {
        var shim = new CountingServiceShim
        {
            Handler = (_, _, _) =>
                Task.FromException<RiskAttestProResult>(
                    new InsufficientSignalsException(2)),
        };
        var req = new RiskAttestProRequest(
            "0x4560000000000000000000000000000000000456", "base", null, false);

        var result = await RiskAttestProEndpoint.HandleAsync(
            req, _db, shim.InvokeAsync, CancellationToken.None);
        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(502, status);
        Assert.Equal("INSUFFICIENT_SIGNALS", body.GetProperty("error").GetString());
        Assert.Contains("got 2", body.GetProperty("reason").GetString() ?? "");
        // Floor breach must NOT cache the failure.
        var rows = await CountCacheRowsAsync(
            "0x4560000000000000000000000000000000000456:base");
        Assert.Equal(0, rows);
    }
}
