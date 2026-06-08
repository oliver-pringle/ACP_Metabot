using System.Globalization;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Data;

/// <summary>
/// Persistence for cached SecurityBot verdicts (the security_verdicts table).
/// Written by SecurityScanWorker, read by DigestService for the /v1/digest
/// security join. Addresses are stored + queried lower-cased.
/// </summary>
public sealed class SecurityVerdictRepository
{
    private readonly Db _db;

    public SecurityVerdictRepository(Db db) => _db = db;

    public async Task UpsertAsync(SecurityVerdict v, CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO security_verdicts
                (agent_address, status, score, grade, observable_count, finding_count,
                 severity_counts, scanned_at, corpus_version, last_error)
            VALUES ($a, $st, $sc, $gr, $oc, $fc, $sevc, $at, $cv, $err)
            ON CONFLICT(agent_address) DO UPDATE SET
                status           = excluded.status,
                score            = excluded.score,
                grade            = excluded.grade,
                observable_count = excluded.observable_count,
                finding_count    = excluded.finding_count,
                severity_counts  = excluded.severity_counts,
                scanned_at       = excluded.scanned_at,
                corpus_version   = excluded.corpus_version,
                last_error       = excluded.last_error;";
        cmd.Parameters.AddWithValue("$a",    v.AgentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$st",   v.Status);
        cmd.Parameters.AddWithValue("$sc",   (object?)v.Score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gr",   (object?)v.Grade ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$oc",   (object?)v.ObservableCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fc",   (object?)v.FindingCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sevc", (object?)v.SeverityCountsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at",   v.ScannedAt);
        cmd.Parameters.AddWithValue("$cv",   (object?)v.CorpusVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err",  (object?)v.LastError ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SecurityVerdict?> GetByAgentAsync(string agentAddress, CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, status, score, grade, observable_count, finding_count,
                   severity_counts, scanned_at, corpus_version, last_error
            FROM security_verdicts WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return Map(reader);
    }

    /// <summary>Verdicts for the given addresses, keyed by lower-cased address.</summary>
    public async Task<IReadOnlyDictionary<string, SecurityVerdict>> GetManyAsync(
        IReadOnlyCollection<string> agentAddresses, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SecurityVerdict>(StringComparer.Ordinal);
        if (agentAddresses.Count == 0) return result;

        var lowered = agentAddresses.Select(a => a.ToLowerInvariant()).Distinct().ToList();
        var paramNames = lowered.Select((_, i) => $"$a{i}").ToArray();

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT agent_address, status, score, grade, observable_count, finding_count,
                   severity_counts, scanned_at, corpus_version, last_error
            FROM security_verdicts
            WHERE agent_address IN ({string.Join(",", paramNames)});";
        for (int i = 0; i < lowered.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], lowered[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var v = Map(reader);
            result[v.AgentAddress] = v;
        }
        return result;
    }

    /// <summary>
    /// Active marketplace agents (≥1 non-removed offering seen within
    /// <paramref name="activeWindowDays"/>) whose verdict is missing or past its
    /// status-specific TTL. Never-scanned agents rank first, then by descending
    /// hire count, then oldest verdict first. Capped at <paramref name="limit"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetStaleAgentsAsync(
        DateTime nowUtc, int activeWindowDays,
        TimeSpan scannedTtl, TimeSpan notAuditableTtl, TimeSpan errorTtl,
        int limit, CancellationToken ct = default)
    {
        string Iso(DateTime d) => d.ToString("O", CultureInfo.InvariantCulture);
        var activeCutoff       = Iso(nowUtc.AddDays(-activeWindowDays));
        var scannedCutoff      = Iso(nowUtc.Subtract(scannedTtl));
        var notAuditableCutoff = Iso(nowUtc.Subtract(notAuditableTtl));
        var errorCutoff        = Iso(nowUtc.Subtract(errorTtl));

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT o.addr
            FROM (
                SELECT LOWER(agent_address) AS addr,
                       SUM(usage_count)     AS hires
                FROM offerings
                WHERE is_removed = 0 AND last_seen_at >= $activeCutoff
                GROUP BY LOWER(agent_address)
            ) o
            LEFT JOIN security_verdicts v ON v.agent_address = o.addr
            WHERE v.agent_address IS NULL
               OR (v.status = 'scanned'        AND v.scanned_at < $scannedCutoff)
               OR (v.status = 'not_auditable'  AND v.scanned_at < $notAuditableCutoff)
               OR (v.status = 'error'          AND v.scanned_at < $errorCutoff)
            ORDER BY (v.scanned_at IS NULL) DESC, o.hires DESC, v.scanned_at ASC
            LIMIT $limit;";
        cmd.Parameters.AddWithValue("$activeCutoff",       activeCutoff);
        cmd.Parameters.AddWithValue("$scannedCutoff",      scannedCutoff);
        cmd.Parameters.AddWithValue("$notAuditableCutoff", notAuditableCutoff);
        cmd.Parameters.AddWithValue("$errorCutoff",        errorCutoff);
        cmd.Parameters.AddWithValue("$limit",              limit);

        var list = new List<string>(capacity: limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    private static SecurityVerdict Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        AgentAddress:       r.GetString(0),
        Status:             r.GetString(1),
        Score:              r.IsDBNull(2) ? null : r.GetInt32(2),
        Grade:              r.IsDBNull(3) ? null : r.GetString(3),
        ObservableCount:    r.IsDBNull(4) ? null : r.GetInt32(4),
        FindingCount:       r.IsDBNull(5) ? null : r.GetInt32(5),
        SeverityCountsJson: r.IsDBNull(6) ? null : r.GetString(6),
        ScannedAt:          r.GetString(7),
        CorpusVersion:      r.IsDBNull(8) ? null : r.GetString(8),
        LastError:          r.IsDBNull(9) ? null : r.GetString(9));
}
