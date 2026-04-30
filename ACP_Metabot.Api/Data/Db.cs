using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public class Db
{
    private readonly string _connectionString;

    public Db(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Sqlite")
            ?? throw new InvalidOperationException("ConnectionStrings:Sqlite not configured");
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task InitializeSchemaAsync()
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS offerings (
                id                       INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_address            TEXT    NOT NULL,
                agent_name               TEXT    NOT NULL,
                offering_name            TEXT    NOT NULL,
                description              TEXT    NOT NULL,
                requirement_schema_json  TEXT,
                price_usdc               REAL    NOT NULL,
                price_type               TEXT    NOT NULL,
                is_private               INTEGER NOT NULL DEFAULT 0,
                chain                    TEXT    NOT NULL,
                content_hash             TEXT    NOT NULL,
                first_seen_at            TEXT    NOT NULL,
                last_seen_at             TEXT    NOT NULL,
                usage_count              INTEGER NOT NULL DEFAULT 0,
                agent_job_count          INTEGER NOT NULL DEFAULT 0,
                UNIQUE(agent_address, offering_name)
            );

            CREATE INDEX IF NOT EXISTS ix_offerings_content_hash ON offerings(content_hash);
            CREATE INDEX IF NOT EXISTS ix_offerings_last_seen    ON offerings(last_seen_at);
            CREATE INDEX IF NOT EXISTS ix_offerings_agent        ON offerings(agent_address);

            CREATE TABLE IF NOT EXISTS agent_reputation_snapshots (
                snapshot_date    TEXT    NOT NULL,
                offering_id      INTEGER NOT NULL,
                usage_count      INTEGER NOT NULL,
                agent_job_count  INTEGER NOT NULL,
                PRIMARY KEY (snapshot_date, offering_id)
            );

            CREATE INDEX IF NOT EXISTS ix_repsnap_offering ON agent_reputation_snapshots(offering_id);

            CREATE TABLE IF NOT EXISTS offering_embeddings (
                offering_id     INTEGER PRIMARY KEY,
                model           TEXT    NOT NULL,
                dimension       INTEGER NOT NULL,
                embedding_blob  BLOB    NOT NULL,
                embedded_at     TEXT    NOT NULL,
                FOREIGN KEY (offering_id) REFERENCES offerings(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS watches (
                id                            TEXT    PRIMARY KEY,
                job_id                        INTEGER NOT NULL UNIQUE,
                buyer_address                 TEXT    NOT NULL,
                query                         TEXT    NOT NULL,
                webhook_url                   TEXT    NOT NULL,
                duration_days                 INTEGER NOT NULL,
                interval_hours                INTEGER NOT NULL,
                min_score                     REAL,
                price_max_usdc                REAL,
                max_alerts                    INTEGER NOT NULL,
                alerts_delivered              INTEGER NOT NULL DEFAULT 0,
                webhook_consecutive_failures  INTEGER NOT NULL DEFAULT 0,
                status                        TEXT    NOT NULL DEFAULT 'active',
                created_at                    TEXT    NOT NULL,
                expires_at                    TEXT    NOT NULL,
                last_polled_at                TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_watches_status_polled ON watches(status, last_polled_at);

            CREATE TABLE IF NOT EXISTS watch_seen (
                watch_id        TEXT    NOT NULL,
                offering_id     INTEGER NOT NULL,
                first_seen_at   TEXT    NOT NULL,
                PRIMARY KEY (watch_id, offering_id),
                FOREIGN KEY (watch_id) REFERENCES watches(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS agent_reputation_cache (
                agent_address       TEXT PRIMARY KEY,
                agent_name          TEXT    NOT NULL,
                agent_score         INTEGER NOT NULL,
                sub_scores_json     TEXT    NOT NULL,
                raw_counts_json     TEXT    NOT NULL,
                flags_json          TEXT    NOT NULL,
                computed_at         TEXT    NOT NULL,
                last_scanned_block  INTEGER NOT NULL,
                source              TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_reputation_cache_computed_at
                ON agent_reputation_cache(computed_at);

            CREATE TABLE IF NOT EXISTS agent_lifetime_snapshot (
                agent_address    TEXT    NOT NULL,
                snapshot_date    TEXT    NOT NULL,
                total_jobs       INTEGER NOT NULL,
                PRIMARY KEY (agent_address, snapshot_date)
            );

            CREATE INDEX IF NOT EXISTS idx_snapshot_date
                ON agent_lifetime_snapshot(snapshot_date);

            CREATE TABLE IF NOT EXISTS request_log (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ts              TEXT    NOT NULL,
                endpoint        TEXT    NOT NULL,
                method          TEXT    NOT NULL,
                status_code     INTEGER NOT NULL,
                duration_ms     INTEGER NOT NULL,
                source          TEXT    NOT NULL,
                user_agent      TEXT,
                caller_id       TEXT,
                remote_ip       TEXT,
                query_text      TEXT,
                agent_address   TEXT,
                provider_error  TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_request_log_ts          ON request_log(ts);
            CREATE INDEX IF NOT EXISTS ix_request_log_endpoint_ts ON request_log(endpoint, ts);
            CREATE INDEX IF NOT EXISTS ix_request_log_source_ts   ON request_log(source, ts);

            CREATE TABLE IF NOT EXISTS request_rollup_hourly (
                bucket_hour     TEXT    NOT NULL,
                endpoint        TEXT    NOT NULL,
                source          TEXT    NOT NULL,
                status_class    TEXT    NOT NULL,
                count           INTEGER NOT NULL,
                sum_duration_ms INTEGER NOT NULL,
                voyage_errors   INTEGER NOT NULL DEFAULT 0,
                claude_errors   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (bucket_hour, endpoint, source, status_class)
            );

            CREATE TABLE IF NOT EXISTS request_rollup_daily (
                bucket_date     TEXT    NOT NULL,
                endpoint        TEXT    NOT NULL,
                source          TEXT    NOT NULL,
                status_class    TEXT    NOT NULL,
                count           INTEGER NOT NULL,
                sum_duration_ms INTEGER NOT NULL,
                voyage_errors   INTEGER NOT NULL DEFAULT 0,
                claude_errors   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (bucket_date, endpoint, source, status_class)
            );

            CREATE INDEX IF NOT EXISTS ix_rollup_hourly_endpoint ON request_rollup_hourly(endpoint, bucket_hour);
            CREATE INDEX IF NOT EXISTS ix_rollup_daily_endpoint  ON request_rollup_daily(endpoint, bucket_date)
            ";
        await cmd.ExecuteNonQueryAsync();

        // Idempotent column additions for databases created before reputation
        // columns existed. SQLite has no `ADD COLUMN IF NOT EXISTS`, so each
        // ALTER is run in isolation and "duplicate column name" errors are
        // swallowed.
        foreach (var alter in new[]
        {
            "ALTER TABLE offerings ADD COLUMN usage_count INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE offerings ADD COLUMN agent_job_count INTEGER NOT NULL DEFAULT 0",
        })
        {
            try
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = alter;
                await alterCmd.ExecuteNonQueryAsync();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // Already added on a prior boot. No action.
            }
        }
    }
}
