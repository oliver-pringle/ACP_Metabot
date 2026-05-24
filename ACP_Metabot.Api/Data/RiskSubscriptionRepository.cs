using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

// v1.8 Portfolio Risk Bot — subscription row for daily_risk_watch.
//
// CADENCE NOTE: v1 hardcodes a 30-tick / 30-day daily cadence; v1.1 may add
// weekly / hourly via the existing fields. The worker (RiskWatchWorker) runs
// at 02:00 UTC every day, picks up rows where status='active' AND
// next_run_at <= now, computes a fresh risk_snapshot, diffs against
// last_snapshot_json, and pushes HMAC-signed.
public record RiskSubscription(
    string Id,
    long JobId,
    string BuyerAddress,
    string WalletAddress,
    string Chain,
    string WebhookUrl,
    string WebhookSecret,
    string Cadence,
    int TicksPurchased,
    int TicksDelivered,
    int ConsecutiveFailures,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime FirstTickAt,
    DateTime? LastRunAt,
    DateTime NextRunAt,
    string? LastSnapshotJson,
    int? LastScore
);

public class RiskSubscriptionRepository
{
    private readonly Db _db;
    private readonly ACP_Metabot.Api.Services.WebhookSecretCipher _cipher;
    public RiskSubscriptionRepository(Db db, ACP_Metabot.Api.Services.WebhookSecretCipher cipher)
    {
        _db = db;
        _cipher = cipher;
    }

    public async Task InsertAsync(RiskSubscription sub)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO risk_subscriptions
              (id, job_id, buyer_address, wallet_address, chain,
               webhook_url, webhook_secret, cadence,
               ticks_purchased, ticks_delivered, consecutive_failures,
               status, created_at, expires_at, first_tick_at,
               last_run_at, next_run_at, last_snapshot_json, last_score)
            VALUES
              ($id, $job, $buyer, $wallet, $chain,
               $url, $secret, $cadence,
               $tp, $td, $cf,
               $status, $created, $expires, $first,
               $last, $next, $snap, $score);";
        cmd.Parameters.AddWithValue("$id",     sub.Id);
        cmd.Parameters.AddWithValue("$job",    sub.JobId);
        cmd.Parameters.AddWithValue("$buyer",  sub.BuyerAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$wallet", sub.WalletAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$chain",  sub.Chain);
        cmd.Parameters.AddWithValue("$url",    sub.WebhookUrl);
        // 2026-05-24 hardening: AES-256-GCM at rest (opt-in via env). Lazy migration.
        cmd.Parameters.AddWithValue("$secret", _cipher.Protect(sub.WebhookSecret));
        cmd.Parameters.AddWithValue("$cadence", sub.Cadence);
        cmd.Parameters.AddWithValue("$tp",     sub.TicksPurchased);
        cmd.Parameters.AddWithValue("$td",     sub.TicksDelivered);
        cmd.Parameters.AddWithValue("$cf",     sub.ConsecutiveFailures);
        cmd.Parameters.AddWithValue("$status", sub.Status);
        cmd.Parameters.AddWithValue("$created", sub.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$expires", sub.ExpiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("$first",  sub.FirstTickAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last",   (object?)sub.LastRunAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$next",   sub.NextRunAt.ToString("O"));
        cmd.Parameters.AddWithValue("$snap",   (object?)sub.LastSnapshotJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$score",  (object?)sub.LastScore ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<RiskSubscription?> GetByIdAsync(string id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, job_id, buyer_address, wallet_address, chain,
                   webhook_url, webhook_secret, cadence,
                   ticks_purchased, ticks_delivered, consecutive_failures,
                   status, created_at, expires_at, first_tick_at,
                   last_run_at, next_run_at, last_snapshot_json, last_score
            FROM risk_subscriptions
            WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await ReadOneAsync(reader);
    }

    // Used by the worker: rows that are 'active' and whose next tick is due.
    public async Task<List<RiskSubscription>> GetDueAsync(DateTime nowUtc, int batchSize)
    {
        var rows = new List<RiskSubscription>();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, job_id, buyer_address, wallet_address, chain,
                   webhook_url, webhook_secret, cadence,
                   ticks_purchased, ticks_delivered, consecutive_failures,
                   status, created_at, expires_at, first_tick_at,
                   last_run_at, next_run_at, last_snapshot_json, last_score
            FROM risk_subscriptions
            WHERE status = 'active'
              AND next_run_at <= $now
            ORDER BY next_run_at ASC
            LIMIT $lim;";
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$lim", batchSize);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = await ReadOneSyncOrNullAsync(reader);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    // After a tick: update the cadence pointer and tick counters. Marks the
    // subscription completed when the buyer's purchased window is reached, and
    // suspended after 5 consecutive webhook failures (parity with watches
    // behaviour).
    public async Task RecordTickResultAsync(
        string id, bool ok, DateTime ranAtUtc, DateTime nextRunAtUtc,
        int newScore, string snapshotJson, bool windowComplete)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ok ? @"
            UPDATE risk_subscriptions
            SET ticks_delivered = ticks_delivered + 1,
                consecutive_failures = 0,
                last_run_at = $ran,
                next_run_at = $next,
                last_snapshot_json = $snap,
                last_score = $score,
                status = CASE WHEN $done = 1 THEN 'completed' ELSE status END
            WHERE id = $id;" : @"
            UPDATE risk_subscriptions
            SET consecutive_failures = consecutive_failures + 1,
                last_run_at = $ran,
                next_run_at = $next,
                last_snapshot_json = COALESCE($snap, last_snapshot_json),
                last_score = COALESCE($score, last_score),
                status = CASE WHEN consecutive_failures + 1 >= 5 THEN 'suspended' ELSE status END
            WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ran", ranAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$next", nextRunAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$snap", (object?)snapshotJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$score", (object)newScore);
        cmd.Parameters.AddWithValue("$done", windowComplete ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<RiskSubscription?> ReadOneAsync(SqliteDataReader reader)
    {
        if (!await reader.ReadAsync()) return null;
        return ReadFromReader(reader);
    }

    private Task<RiskSubscription?> ReadOneSyncOrNullAsync(SqliteDataReader reader)
        => Task.FromResult<RiskSubscription?>(ReadFromReader(reader));

    private RiskSubscription ReadFromReader(SqliteDataReader reader)
    {
        DateTime ParseUtc(int i) => DateTime.Parse(reader.GetString(i),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        DateTime? ParseNullableUtc(int i) => reader.IsDBNull(i) ? null
            : DateTime.Parse(reader.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new RiskSubscription(
            Id:                  reader.GetString(0),
            JobId:               reader.GetInt64(1),
            BuyerAddress:        reader.GetString(2),
            WalletAddress:       reader.GetString(3),
            Chain:               reader.GetString(4),
            WebhookUrl:          reader.GetString(5),
            // 2026-05-24 hardening: decrypt at-rest secret on read.
            WebhookSecret:       _cipher.Unprotect(reader.GetString(6)),
            Cadence:             reader.GetString(7),
            TicksPurchased:      reader.GetInt32(8),
            TicksDelivered:      reader.GetInt32(9),
            ConsecutiveFailures: reader.GetInt32(10),
            Status:              reader.GetString(11),
            CreatedAt:           ParseUtc(12),
            ExpiresAt:           ParseUtc(13),
            FirstTickAt:         ParseUtc(14),
            LastRunAt:           ParseNullableUtc(15),
            NextRunAt:           ParseUtc(16),
            LastSnapshotJson:    reader.IsDBNull(17) ? null : reader.GetString(17),
            LastScore:           reader.IsDBNull(18) ? null : reader.GetInt32(18)
        );
    }
}
