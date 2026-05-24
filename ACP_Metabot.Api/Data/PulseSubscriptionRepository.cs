using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

// v1.9 marketplacePulseSub — one row per active subscription.
// MarketplacePulseWorker (default OFF) iterates rows where status='active'
// and next_run_at <= now, computes a fresh digest, signs HMAC, and POSTs to
// webhook_url. Schema mirrors risk_subscriptions exactly so the
// BasicSubscriptionBot pattern stays uniform across the portfolio.
public record PulseSubscription(
    string Id,
    long JobId,
    string BuyerAddress,
    string WebhookUrl,
    string WebhookSecret,
    string Cadence,
    string Marketplace,
    int TicksPurchased,
    int TicksDelivered,
    int ConsecutiveFailures,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime FirstTickAt,
    DateTime? LastRunAt,
    DateTime NextRunAt,
    string? LastPayloadHash
);

public class PulseSubscriptionRepository
{
    private readonly Db _db;
    private readonly ACP_Metabot.Api.Services.WebhookSecretCipher _cipher;
    public PulseSubscriptionRepository(Db db, ACP_Metabot.Api.Services.WebhookSecretCipher cipher)
    {
        _db = db;
        _cipher = cipher;
    }

    public async Task InsertAsync(PulseSubscription sub)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO pulse_subscriptions
              (id, job_id, buyer_address, webhook_url, webhook_secret, cadence,
               marketplace, ticks_purchased, ticks_delivered, consecutive_failures,
               status, created_at, expires_at, first_tick_at,
               last_run_at, next_run_at, last_payload_hash)
            VALUES
              ($id, $job, $buyer, $url, $secret, $cadence,
               $market, $tp, $td, $cf,
               $status, $created, $expires, $first,
               $last, $next, $hash);";
        cmd.Parameters.AddWithValue("$id",      sub.Id);
        cmd.Parameters.AddWithValue("$job",     sub.JobId);
        cmd.Parameters.AddWithValue("$buyer",   sub.BuyerAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$url",     sub.WebhookUrl);
        // 2026-05-24 hardening: webhook_secret optionally AES-256-GCM at rest
        // via WEBHOOK_SECRET_ENCRYPTION_KEY. No-op (plaintext passthrough)
        // when unset; lazy migration via "v1:" prefix sentinel.
        cmd.Parameters.AddWithValue("$secret",  _cipher.Protect(sub.WebhookSecret));
        cmd.Parameters.AddWithValue("$cadence", sub.Cadence);
        cmd.Parameters.AddWithValue("$market",  sub.Marketplace);
        cmd.Parameters.AddWithValue("$tp",      sub.TicksPurchased);
        cmd.Parameters.AddWithValue("$td",      sub.TicksDelivered);
        cmd.Parameters.AddWithValue("$cf",      sub.ConsecutiveFailures);
        cmd.Parameters.AddWithValue("$status",  sub.Status);
        cmd.Parameters.AddWithValue("$created", sub.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$expires", sub.ExpiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("$first",   sub.FirstTickAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last",    (object?)sub.LastRunAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$next",    sub.NextRunAt.ToString("O"));
        cmd.Parameters.AddWithValue("$hash",    (object?)sub.LastPayloadHash ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<PulseSubscription?> GetByIdAsync(string id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " FROM pulse_subscriptions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadFromReader(reader) : null;
    }

    public async Task<List<PulseSubscription>> GetDueAsync(DateTime nowUtc, int batchSize)
    {
        var rows = new List<PulseSubscription>();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + @" FROM pulse_subscriptions
                            WHERE status = 'active' AND next_run_at <= $now
                            ORDER BY next_run_at ASC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$lim", batchSize);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(ReadFromReader(reader));
        return rows;
    }

    // Mirror of RiskSubscriptionRepository.RecordTickResultAsync: success path
    // bumps ticks_delivered + clears failures; failure path bumps
    // consecutive_failures and suspends after 5. windowComplete = ticks_delivered
    // reached ticks_purchased.
    public async Task RecordTickResultAsync(
        string id, bool ok, DateTime ranAtUtc, DateTime nextRunAtUtc,
        string? payloadHash, bool windowComplete)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ok ? @"
            UPDATE pulse_subscriptions
            SET ticks_delivered = ticks_delivered + 1,
                consecutive_failures = 0,
                last_run_at = $ran,
                next_run_at = $next,
                last_payload_hash = COALESCE($hash, last_payload_hash),
                status = CASE WHEN $done = 1 THEN 'completed' ELSE status END
            WHERE id = $id;" : @"
            UPDATE pulse_subscriptions
            SET consecutive_failures = consecutive_failures + 1,
                last_run_at = $ran,
                next_run_at = $next,
                status = CASE WHEN consecutive_failures + 1 >= 5 THEN 'suspended' ELSE status END
            WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id",   id);
        cmd.Parameters.AddWithValue("$ran",  ranAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$next", nextRunAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", (object?)payloadHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$done", windowComplete ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string SelectColumns =
        @"SELECT id, job_id, buyer_address, webhook_url, webhook_secret, cadence,
                 marketplace, ticks_purchased, ticks_delivered, consecutive_failures,
                 status, created_at, expires_at, first_tick_at,
                 last_run_at, next_run_at, last_payload_hash";

    private PulseSubscription ReadFromReader(SqliteDataReader r)
    {
        DateTime ParseUtc(int i) => DateTime.Parse(r.GetString(i),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        DateTime? ParseNullableUtc(int i) => r.IsDBNull(i) ? null
            : DateTime.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new PulseSubscription(
            Id:                  r.GetString(0),
            JobId:               r.GetInt64(1),
            BuyerAddress:        r.GetString(2),
            WebhookUrl:          r.GetString(3),
            // 2026-05-24 hardening: decrypt at-rest secret on read. Legacy
            // plaintext rows pass through unchanged via the "v1:" sentinel.
            WebhookSecret:       _cipher.Unprotect(r.GetString(4)),
            Cadence:             r.GetString(5),
            Marketplace:         r.GetString(6),
            TicksPurchased:      r.GetInt32(7),
            TicksDelivered:      r.GetInt32(8),
            ConsecutiveFailures: r.GetInt32(9),
            Status:              r.GetString(10),
            CreatedAt:           ParseUtc(11),
            ExpiresAt:           ParseUtc(12),
            FirstTickAt:         ParseUtc(13),
            LastRunAt:           ParseNullableUtc(14),
            NextRunAt:           ParseUtc(15),
            LastPayloadHash:     r.IsDBNull(16) ? null : r.GetString(16)
        );
    }
}
