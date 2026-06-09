using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// The single write-path for a SecurityBot verdict. Extracted from
/// SecurityScanWorker.TickOnceAsync so the background worker (timing / batching /
/// stale-selection) and the on-demand operator endpoint (POST /admin/securityScan)
/// both persist a scan IDENTICALLY — there is exactly one way a verdict is written.
///
/// Depends on ITheSecurityBotClient only; the repos are passed IN by the caller,
/// which already resolves them from its own scope (the worker from its per-tick
/// scope, the endpoint from the request scope). This keeps the service free of
/// scope concerns and matches the existing repo-as-singleton wiring. Registered as
/// a DI singleton.
/// </summary>
public sealed class SecurityScanService
{
    private readonly ITheSecurityBotClient _client;

    public SecurityScanService(ITheSecurityBotClient client) => _client = client;

    /// <summary>
    /// Scan the target agent over acp-shared (free internal path), upsert the
    /// latest-verdict cache, append one immutable history row retaining the full
    /// result, and return the ScanResult so the caller can surface full detail.
    ///
    /// Cache FIRST (keeps the digest correct), then append; a crash strictly
    /// between the two re-captures next scan. ScanAsync NEVER throws — a non-2xx /
    /// transport / parse failure arrives as a status=error verdict, which is still
    /// persisted (honest outcome) and returned.
    /// </summary>
    public async Task<ScanResult> ScanAndPersistAsync(
        string agentAddress,
        SecurityVerdictRepository repo,
        SecurityScanHistoryRepository historyRepo,
        CancellationToken ct)
    {
        var result = await _client.ScanAsync(agentAddress, ct);
        var verdict = result.Verdict;

        // (a) latest-verdict cache (drives the digest join) — upsert/overwrite.
        await repo.UpsertAsync(verdict, ct);

        // (b) append-only history — one immutable row per scan, retaining the FULL
        // result incl. the raw findings JSON. Appended for scanned/not_auditable/
        // error alike so the timeline is complete (same arg order the worker uses).
        await historyRepo.AppendAsync(
            verdict.AgentAddress, verdict.ScannedAt, verdict.Status,
            verdict.Score, verdict.Grade, verdict.ObservableCount,
            verdict.FindingCount, verdict.SeverityCountsJson,
            result.RawVerdict, verdict.CorpusVersion,
            result.RawFindingsJson, verdict.LastError, ct);

        return result;
    }
}
