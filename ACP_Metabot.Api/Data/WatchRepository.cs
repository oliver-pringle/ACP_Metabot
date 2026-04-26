using System.Globalization;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Data;

public class WatchRepository
{
    private readonly Db _db;

    public WatchRepository(Db db) => _db = db;

    public async Task CreateAsync(Watch w)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO watches (
                id, job_id, buyer_address, query, webhook_url,
                duration_days, interval_hours, min_score, price_max_usdc,
                max_alerts, alerts_delivered, webhook_consecutive_failures,
                status, created_at, expires_at, last_polled_at)
            VALUES (
                $id, $jobId, $buyer, $query, $url,
                $dDays, $iHours, $minScore, $priceMax,
                $maxAlerts, $delivered, $failures,
                $status, $created, $expires, $polled);";
        cmd.Parameters.AddWithValue("$id", w.Id);
        cmd.Parameters.AddWithValue("$jobId", w.JobId);
        cmd.Parameters.AddWithValue("$buyer", w.BuyerAddress);
        cmd.Parameters.AddWithValue("$query", w.Query);
        cmd.Parameters.AddWithValue("$url", w.WebhookUrl);
        cmd.Parameters.AddWithValue("$dDays", w.DurationDays);
        cmd.Parameters.AddWithValue("$iHours", w.IntervalHours);
        cmd.Parameters.AddWithValue("$minScore", (object?)w.MinScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$priceMax", (object?)w.PriceMaxUsdc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxAlerts", w.MaxAlerts);
        cmd.Parameters.AddWithValue("$delivered", w.AlertsDelivered);
        cmd.Parameters.AddWithValue("$failures", w.WebhookConsecutiveFailures);
        cmd.Parameters.AddWithValue("$status", w.Status);
        cmd.Parameters.AddWithValue("$created", w.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$expires", w.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$polled",
            w.LastPolledAt.HasValue
                ? w.LastPolledAt.Value.ToString("O", CultureInfo.InvariantCulture)
                : (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Watch?> GetByIdAsync(string id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " FROM watches WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return Map(reader);
    }

    /// <summary>
    /// Returns watches whose status is 'active' or 'webhook_failing', whose
    /// expiry is in the future, and whose last_polled_at is older than
    /// interval_hours ago (or null). Caller is responsible for not double-polling.
    /// </summary>
    public async Task<List<Watch>> GetDueAsync(DateTime nowUtc)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + @"
            FROM watches
            WHERE status IN ('active', 'webhook_failing')
              AND datetime(expires_at) > datetime($now)
              AND (last_polled_at IS NULL
                   OR datetime(last_polled_at, '+' || interval_hours || ' hours') <= datetime($now));";
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Watch>();
        while (await reader.ReadAsync()) result.Add(Map(reader));
        return result;
    }

    public async Task<int> MarkExpiredAsync(DateTime nowUtc)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE watches SET status = 'expired'
            WHERE status IN ('active', 'webhook_failing')
              AND datetime(expires_at) <= datetime($now);";
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkPolledAsync(string id, DateTime nowUtc)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE watches SET last_polled_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> IncrementAlertsAsync(string id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE watches SET alerts_delivered = alerts_delivered + 1 WHERE id = $id;
            SELECT alerts_delivered FROM watches WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    public async Task ResetFailureCountAsync(string id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE watches SET webhook_consecutive_failures = 0 WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> IncrementFailureCountAsync(string id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE watches SET webhook_consecutive_failures = webhook_consecutive_failures + 1 WHERE id = $id;
            SELECT webhook_consecutive_failures FROM watches WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    public async Task MarkStatusAsync(string id, string status)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE watches SET status = $status WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<long>> GetSeenIdsAsync(string watchId)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT offering_id FROM watch_seen WHERE watch_id = $id;";
        cmd.Parameters.AddWithValue("$id", watchId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var set = new HashSet<long>();
        while (await reader.ReadAsync()) set.Add(reader.GetInt64(0));
        return set;
    }

    public async Task<int> ClearSeenAsync(string watchId)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM watch_seen WHERE watch_id = $id;";
        cmd.Parameters.AddWithValue("$id", watchId);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task RecordSeenAsync(string watchId, IEnumerable<long> offeringIds, DateTime nowUtc)
    {
        var ids = offeringIds.ToList();
        if (ids.Count == 0) return;
        await using var conn = _db.OpenConnection();
        await using var tx = conn.BeginTransaction();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO watch_seen (watch_id, offering_id, first_seen_at)
            VALUES ($wid, $oid, $ts);";
        var pWid = cmd.Parameters.Add("$wid", Microsoft.Data.Sqlite.SqliteType.Text);
        var pOid = cmd.Parameters.Add("$oid", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pTs = cmd.Parameters.Add("$ts", Microsoft.Data.Sqlite.SqliteType.Text);
        pWid.Value = watchId;
        pTs.Value = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        foreach (var id in ids)
        {
            pOid.Value = id;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private const string SelectColumns = @"
        SELECT id, job_id, buyer_address, query, webhook_url,
               duration_days, interval_hours, min_score, price_max_usdc,
               max_alerts, alerts_delivered, webhook_consecutive_failures,
               status, created_at, expires_at, last_polled_at";

    private static Watch Map(System.Data.Common.DbDataReader r)
    {
        return new Watch(
            Id: r.GetString(0),
            JobId: r.GetInt64(1),
            BuyerAddress: r.GetString(2),
            Query: r.GetString(3),
            WebhookUrl: r.GetString(4),
            DurationDays: r.GetInt32(5),
            IntervalHours: r.GetInt32(6),
            MinScore: r.IsDBNull(7) ? null : r.GetDouble(7),
            PriceMaxUsdc: r.IsDBNull(8) ? null : r.GetDouble(8),
            MaxAlerts: r.GetInt32(9),
            AlertsDelivered: r.GetInt32(10),
            WebhookConsecutiveFailures: r.GetInt32(11),
            Status: r.GetString(12),
            CreatedAt: DateTime.Parse(r.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ExpiresAt: DateTime.Parse(r.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastPolledAt: r.IsDBNull(15)
                ? null
                : DateTime.Parse(r.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
