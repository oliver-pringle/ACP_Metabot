using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

/// <summary>
/// Append-only persistence for EVERY SecurityBot scan (the security_scan_history
/// table). One immutable row per scan, retaining the full result incl. the raw
/// findings JSON — the durable answer to "save the results of each scan on a bot".
/// security_verdicts (SecurityVerdictRepository) remains the latest-only cache;
/// THIS repo never updates or deletes, it only inserts and lists. Addresses are
/// stored + queried lower-cased (matching SecurityVerdictRepository). The
/// surrogate-id PK + (agent_address, scanned_at DESC) index structurally mirror
/// risk_snapshot_history; AgentReputationHistoryRepository is the structural
/// reference only (it does NOT lower-case addresses — do not copy that).
/// </summary>
public sealed class SecurityScanHistoryRepository
{
    private readonly Db _db;

    public SecurityScanHistoryRepository(Db db) => _db = db;

    /// <summary>
    /// Append one immutable history row for a completed scan. Never overwrites —
    /// each call inserts a new autoincrement row, so two scans of the same agent
    /// yield two rows. <paramref name="findingsJson"/> is the full raw findings[]
    /// array (may be null for not_auditable / error scans). <paramref name="rawVerdict"/>
    /// is SecurityBot's raw verdict discriminator (PASS / NOT_AUDITABLE / ...).
    /// </summary>
    public async Task AppendAsync(
        string agentAddress, string scannedAt, string status,
        int? score, string? grade, int? observableCount, int? findingCount,
        string? severityCountsJson, string? rawVerdict, string? corpusVersion,
        string? findingsJson, string? lastError, CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO security_scan_history
                (agent_address, scanned_at, status, score, grade, observable_count,
                 finding_count, severity_counts, verdict, corpus_version,
                 findings_json, last_error)
            VALUES ($a, $at, $st, $sc, $gr, $oc, $fc, $sevc, $vd, $cv, $fj, $err);";
        cmd.Parameters.AddWithValue("$a",   agentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$at",  scannedAt);
        cmd.Parameters.AddWithValue("$st",  status);
        cmd.Parameters.AddWithValue("$sc",  (object?)score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gr",  (object?)grade ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$oc",  (object?)observableCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fc",  (object?)findingCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sevc",(object?)severityCountsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$vd",  (object?)rawVerdict ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cv",  (object?)corpusVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fj",  (object?)findingsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)lastError ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Most recent <paramref name="limit"/> scans for an agent, newest first.
    /// Empty when the agent has never been scanned. limit clamped 1–100.
    /// (Read surface is deferred; this method backs tests + a future endpoint.)
    /// </summary>
    public async Task<IReadOnlyList<ScanHistoryRow>> ListByAgentAsync(
        string agentAddress, int limit = 20, CancellationToken ct = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 100) limit = 100;

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, agent_address, scanned_at, status, score, grade,
                   observable_count, finding_count, severity_counts, verdict,
                   corpus_version, findings_json, last_error
            FROM security_scan_history
            WHERE agent_address = $a
            ORDER BY scanned_at DESC, id DESC
            LIMIT $limit;";
        cmd.Parameters.AddWithValue("$a",     agentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<ScanHistoryRow>(capacity: limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Map(reader));
        return list;
    }

    private static ScanHistoryRow Map(SqliteDataReader r) => new(
        Id:                 r.GetInt64(0),
        AgentAddress:       r.GetString(1),
        ScannedAt:          r.GetString(2),
        Status:             r.GetString(3),
        Score:              r.IsDBNull(4)  ? null : r.GetInt32(4),
        Grade:              r.IsDBNull(5)  ? null : r.GetString(5),
        ObservableCount:    r.IsDBNull(6)  ? null : r.GetInt32(6),
        FindingCount:       r.IsDBNull(7)  ? null : r.GetInt32(7),
        SeverityCountsJson: r.IsDBNull(8)  ? null : r.GetString(8),
        Verdict:            r.IsDBNull(9)  ? null : r.GetString(9),
        CorpusVersion:      r.IsDBNull(10) ? null : r.GetString(10),
        FindingsJson:       r.IsDBNull(11) ? null : r.GetString(11),
        LastError:          r.IsDBNull(12) ? null : r.GetString(12));
}
