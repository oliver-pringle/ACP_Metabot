// On-demand operator security scan — POST /admin/securityScan helper.
//
// Factored out of Program.cs so the validate -> scan-and-persist -> project
// behaviour can be unit-tested without standing up a WebApplicationFactory
// (none exists in this test project — see RiskAttestProEndpoint). Program.cs
// registers a thin `app.MapPost("/admin/securityScan", ...)` that resolves
// SecurityScanService + both repos and delegates straight to HandleAsync.
//
// GATING: /admin/* is NOT in the X-API-Key middleware bypass list, so this
// endpoint requires X-API-Key == INTERNAL_API_KEY (operator-only) — the same
// gate as /admin/pulse/tick-now. The handler is never reached unauthenticated.
//
// The scan-and-persist step is injected as a delegate so tests can write through
// real repos and return a synthetic ScanResult without a live SecurityBot.
//
// IResult shape: HandleAsync returns a CUSTOM `RawJsonResult : IResult` that
// serializes the body itself and writes the bytes straight to ctx.Response.Body
// — copied verbatim from RiskAttestProEndpoint. This is REQUIRED, not cosmetic:
// Results.Ok(...) / Results.BadRequest(...) call GetRequiredService on
// ctx.RequestServices, which throws ArgumentNullException (Parameter 'provider')
// when executed against a bare `new DefaultHttpContext()` (null RequestServices).
// The custom IResult never touches RequestServices, so the unit tests can
// round-trip it through ExecuteAsync(IResult) + DefaultHttpContext.

using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.AspNetCore.Http;

namespace ACP_Metabot.Api.Endpoints;

/// <summary>Request body for <c>POST /admin/securityScan</c>: the agent to scan.</summary>
public sealed record AdminSecurityScanRequest(string? AgentAddress);

/// <summary>
/// Static handler over <see cref="SecurityScanService"/>. The scan-and-persist
/// step is passed as a delegate (mirrors RiskAttestProEndpoint) so tests can
/// inject a counting/writing shim without the full cross-bot client.
/// </summary>
public static class SecurityScanEndpoint
{
    /// <summary>
    /// Validate the agent address (lower-case first, then <c>^0x[0-9a-fA-F]{40}$</c>;
    /// 400 otherwise), scan + persist through <paramref name="scanAndPersist"/>, and
    /// return the full operator projection: agentAddress, status, score, grade,
    /// observableCount, findingCount, severityCounts (object), verdict, scannedAt,
    /// findings[] (parsed from the persisted RawFindingsJson; null/blank -> []).
    /// <c>not_auditable</c>/<c>error</c> return 200 with that status (never a 500).
    /// <c>lastError</c> is NEVER surfaced (P30/P63).
    ///
    /// Returns a custom <see cref="RawJsonResult"/> (NOT Results.Ok/BadRequest) so
    /// the handler is unit-testable against a bare DefaultHttpContext — see the
    /// header note + RiskAttestProEndpoint.
    /// </summary>
    public static async Task<IResult> HandleAsync(
        AdminSecurityScanRequest req,
        SecurityVerdictRepository repo,
        SecurityScanHistoryRepository historyRepo,
        Func<string, SecurityVerdictRepository, SecurityScanHistoryRepository,
            CancellationToken, Task<ScanResult>> scanAndPersist,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.AgentAddress))
            return JsonError(400, "invalid_address");

        var addr = req.AgentAddress.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-fA-F]{40}$"))
            return JsonError(400, "invalid_address");

        // Always scans fresh (explicit operator override). Persists to the same
        // tables the worker writes via the shared SecurityScanService.
        var result = await scanAndPersist(addr, repo, historyRepo, ct);
        var v = result.Verdict;

        // severity_counts JSON -> object (null/blank -> empty object).
        JsonElement severityCounts = ParseObjectOrEmpty(v.SeverityCountsJson);
        // raw findings[] JSON -> array (null/blank/non-array -> empty array).
        JsonElement findings = ParseArrayOrEmpty(result.RawFindingsJson);

        // NOTE: v.LastError is deliberately OMITTED from the projection (P30/P63);
        // it is persisted server-side in security_scan_history only.
        //
        // The JsonElement values (severityCounts, findings) serialize INLINE as
        // real JSON via JsonSerializer — an object/array, not a quoted string.
        var json = JsonSerializer.Serialize(new
        {
            agentAddress    = v.AgentAddress,
            status          = v.Status,
            score           = v.Score,
            grade           = v.Grade,
            observableCount = v.ObservableCount,
            findingCount    = v.FindingCount,
            severityCounts,
            verdict         = result.RawVerdict,
            scannedAt       = v.ScannedAt,
            findings,
        }, JsonOpts);
        return new RawJsonResult(200, json);
    }

    /// <summary>
    /// Minimal <see cref="IResult"/> that writes a pre-serialized JSON body to the
    /// response without touching the DI container. Copied verbatim from
    /// RiskAttestProEndpoint: Results.Ok / Results.BadRequest / Results.Json all
    /// call GetRequiredService on ctx.RequestServices (null in a bare
    /// DefaultHttpContext), which makes the unit tests impossible. This shim writes
    /// the body byte-for-byte and lets the test just read ctx.Response.Body.
    /// </summary>
    private sealed class RawJsonResult : IResult
    {
        private readonly int _statusCode;
        private readonly string _json;
        public RawJsonResult(int statusCode, string json)
        {
            _statusCode = statusCode;
            _json = json;
        }
        public async Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = _statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var bytes = System.Text.Encoding.UTF8.GetBytes(_json);
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        }
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Test-friendly equivalent of <see cref="Results.Json"/> for non-200
    /// envelopes. Used for the 400 invalid_address reject. Body is camelCase
    /// <c>{"error":"..."}</c>.
    /// </summary>
    private static IResult JsonError(int status, string error)
        => new RawJsonResult(status,
            JsonSerializer.Serialize(new { error }, JsonOpts));

    private static JsonElement ParseObjectOrEmpty(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return doc.RootElement.Clone();
            }
            catch (JsonException) { /* fall through to empty */ }
        }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static JsonElement ParseArrayOrEmpty(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.Clone();
            }
            catch (JsonException) { /* fall through to empty */ }
        }
        using var empty = JsonDocument.Parse("[]");
        return empty.RootElement.Clone();
    }
}
