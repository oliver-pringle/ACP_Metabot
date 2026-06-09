// Public read surface for the append-only security_scan_history table:
//   GET /v1/securityScanHistory?agent=0x..&limit=N
//
// Mirrors HandleReputationHistory / GET /v1/agentReputationHistory: public (the
// X-API-Key middleware bypasses /v1/* except /v1/internal), rate-limited under the
// "public-reputation" policy. Backs the acp-find plugin's acp_agent_security_history.
//
// SUMMARY ONLY. The raw findings[] JSON and last_error are NEVER returned here
// (P9 / P10 / P30 / P63) — they are stored server-side in the table and are simply
// not fields on ScanHistorySummary, so they cannot leak through this surface. An
// operator who needs the full per-finding detail re-scans via the GATED
// POST /admin/securityScan (which DOES return findings, behind X-API-Key).
//
// The projection is split out as a pure, allocation-light function so the P9/P10
// "no raw findings / no internal error string" guarantee is unit-tested directly
// (SecurityScanHistoryEndpointTests) without standing up a WebApplicationFactory.

using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.AspNetCore.Http;

namespace ACP_Metabot.Api.Endpoints;

/// <summary>
/// Static handler for the public, summary-only security-scan history read endpoint.
/// </summary>
public static class SecurityScanHistoryEndpoint
{
    /// <summary>
    /// One public-safe history row. Deliberately OMITS <c>FindingsJson</c> and
    /// <c>LastError</c> (P9/P10/P30/P63) — they are not representable on this type,
    /// so no projection mistake can leak them.
    /// </summary>
    public sealed record ScanHistorySummary(
        string ScannedAt,
        string Status,
        int? Score,
        string? Grade,
        string? Verdict,
        int? FindingCount,
        int? ObservableCount,
        string? CorpusVersion,
        JsonElement SeverityCounts);

    /// <summary>
    /// Pure projection: <see cref="ScanHistoryRow"/> → <see cref="ScanHistorySummary"/>.
    /// The <c>severity_counts</c> JSON string becomes an INLINE JSON object
    /// (null / blank / non-object → <c>{}</c>); <c>findings_json</c> and
    /// <c>last_error</c> are dropped entirely.
    /// </summary>
    public static IReadOnlyList<ScanHistorySummary> Project(IReadOnlyList<ScanHistoryRow> rows)
    {
        var list = new List<ScanHistorySummary>(rows.Count);
        foreach (var r in rows)
            list.Add(new ScanHistorySummary(
                ScannedAt:       r.ScannedAt,
                Status:          r.Status,
                Score:           r.Score,
                Grade:           r.Grade,
                Verdict:         r.Verdict,
                FindingCount:    r.FindingCount,
                ObservableCount: r.ObservableCount,
                CorpusVersion:   r.CorpusVersion,
                SeverityCounts:  ParseObjectOrEmpty(r.SeverityCountsJson)));
        return list;
    }

    /// <summary>
    /// Validate the agent address (lower-case first, then <c>^0x[0-9a-f]{40}$</c>;
    /// 400 <c>invalid_address</c> otherwise), clamp <paramref name="limit"/> to 1..100
    /// (default 20), list newest-first, project to summaries, and return
    /// <c>{ agentAddress, count, history[] }</c>. Empty history for an unscanned agent
    /// is a normal 200 (<c>count: 0</c>), not a 404.
    /// </summary>
    public static async Task<IResult> HandleAsync(
        string? agent, int? limit,
        SecurityScanHistoryRepository histRepo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agent))
            return Results.BadRequest(new { error = "invalid_address", message = "agent query param is required" });

        var addr = agent.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
            return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });

        var lim = limit is null ? 20 : Math.Clamp(limit.Value, 1, 100);
        var rows = await histRepo.ListByAgentAsync(addr, lim, ct);
        return Results.Ok(new { agentAddress = addr, count = rows.Count, history = Project(rows) });
    }

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
            catch (JsonException) { /* fall through to empty object */ }
        }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }
}
