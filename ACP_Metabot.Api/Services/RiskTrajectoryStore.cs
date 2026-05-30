using System.Globalization;
using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// v1.0 riskAttestPro Task 2: a single row pulled from the
/// <c>risk_snapshot_history</c> 30-day trajectory store. Returned by
/// <see cref="RiskTrajectoryStore.LookupStrideAsync"/> when a row exists
/// within the ±12h window of the requested stride.
/// </summary>
public sealed record TrajectoryRow(DateTimeOffset CapturedAt, int Score, string ComponentsJson);

/// <summary>
/// v1.0 riskAttestPro Task 2: thin read-through store over
/// <c>risk_snapshot_history</c>. Two operations:
///
///   WriteAsync         — append a snapshot row for (wallet, chain).
///   LookupStrideAsync  — find the row closest to (now - daysAgo) within
///                        ±12h, or null if no row exists in that window.
///
/// Wallet is lowercased before write + lookup so the store is
/// case-insensitive on EVM addresses (callers can pass either casing).
/// Captured-at is stored as ISO-8601 round-trip ("o") so SQLite's
/// <c>julianday()</c> ordering matches lexical ordering exactly.
///
/// The ±12h window is the spec-mandated stride tolerance: re-fetches at
/// -7 / -14 / -21 day strides return cached rows when available and fall
/// through to a live re-snap otherwise.
/// </summary>
public sealed class RiskTrajectoryStore
{
    private readonly Db _db;
    private readonly ILogger<RiskTrajectoryStore> _log;

    public RiskTrajectoryStore(Db db, ILogger<RiskTrajectoryStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task WriteAsync(
        string wallet,
        string chain,
        DateTimeOffset capturedAt,
        int score,
        string componentsJson,
        CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO risk_snapshot_history (wallet, chain, captured_at, score, components_json)
            VALUES ($w, $c, $t, $s, $cj);";
        cmd.Parameters.AddWithValue("$w", wallet.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$c", chain);
        cmd.Parameters.AddWithValue("$t", capturedAt.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$s", score);
        cmd.Parameters.AddWithValue("$cj", componentsJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<TrajectoryRow?> LookupStrideAsync(
        string wallet,
        string chain,
        DateTimeOffset now,
        int daysAgo,
        CancellationToken ct = default)
    {
        var target = now.AddDays(-daysAgo);
        var windowStart = target.AddHours(-12);
        var windowEnd = target.AddHours(12);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT captured_at, score, components_json
            FROM risk_snapshot_history
            WHERE wallet = $w AND chain = $c
              AND captured_at >= $ws AND captured_at <= $we
            ORDER BY ABS(julianday(captured_at) - julianday($t)) ASC
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$w", wallet.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$c", chain);
        cmd.Parameters.AddWithValue("$t", target.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$ws", windowStart.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$we", windowEnd.ToString("o", CultureInfo.InvariantCulture));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new TrajectoryRow(
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            reader.GetInt32(1),
            reader.GetString(2));
    }
}
