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

    // Trigger DDL is factored out so the v1.3 migration can recreate them
    // after the offerings table is rebuilt, without having to re-execute the
    // whole base schema block.
    private const string OfferingsFtsTriggersSql = @"
        CREATE TRIGGER IF NOT EXISTS offerings_ai AFTER INSERT ON offerings BEGIN
            INSERT INTO offerings_fts(rowid, offering_name, agent_name, description)
            VALUES (new.id, new.offering_name, new.agent_name, new.description);
        END;

        CREATE TRIGGER IF NOT EXISTS offerings_ad AFTER DELETE ON offerings BEGIN
            INSERT INTO offerings_fts(offerings_fts, rowid, offering_name, agent_name, description)
            VALUES ('delete', old.id, old.offering_name, old.agent_name, old.description);
        END;

        -- Scoped to the FTS-mirrored columns. The indexer hot path is the
        -- touch UPDATE in OfferingRepository.UpsertManyAsync which only
        -- changes last_seen_at + usage_count + agent_job_count, none of
        -- which are mirrored, so this trigger MUST NOT fire for them. An
        -- AFTER UPDATE without OF fires on every UPDATE regardless of
        -- which columns appear in the SET, and that caused v1.2 prod
        -- corruption (each touch fired delete-then-insert on the FTS
        -- index across 34K rows for 12 indexer cycles, eventually
        -- corrupting the FTS5 shadow pages).
        CREATE TRIGGER IF NOT EXISTS offerings_au
        AFTER UPDATE OF offering_name, agent_name, description ON offerings BEGIN
            INSERT INTO offerings_fts(offerings_fts, rowid, offering_name, agent_name, description)
            VALUES ('delete', old.id, old.offering_name, old.agent_name, old.description);
            INSERT INTO offerings_fts(rowid, offering_name, agent_name, description)
            VALUES (new.id, new.offering_name, new.agent_name, new.description);
        END;
    ";

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
                marketplace_version      TEXT    NOT NULL DEFAULT 'v1',
                UNIQUE(marketplace_version, agent_address, offering_name)
            );

            CREATE INDEX IF NOT EXISTS ix_offerings_content_hash ON offerings(content_hash);
            CREATE INDEX IF NOT EXISTS ix_offerings_last_seen    ON offerings(last_seen_at);
            CREATE INDEX IF NOT EXISTS ix_offerings_agent        ON offerings(agent_address);
            -- ix_offerings_mv is created AFTER the v1.3 ALTER TABLE step
            -- below, since legacy DBs don't have marketplace_version yet
            -- when this block runs.

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
                last_polled_at                TEXT,
                marketplace                   TEXT
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
            CREATE INDEX IF NOT EXISTS ix_rollup_daily_endpoint  ON request_rollup_daily(endpoint, bucket_date);

            -- Phase 1: hybrid BM25+dense search. External-content FTS5 mirrors
            -- offerings(name, agent, description) for the lexical leg of the fusion.
            -- Triggers keep the inverted index in lockstep with the source table
            -- so the indexer's hot path stays UpsertManyAsync-only — no manual
            -- rebuild calls anywhere.
            --
            -- Note: marketplace_version is intentionally NOT mirrored here. v1/v2
            -- filtering happens post-FTS at the offerings join level (see
            -- OfferingRepository.SearchBm25Async), which keeps this table stable
            -- across the v1.3 migration and avoids a forced FTS rebuild.
            CREATE VIRTUAL TABLE IF NOT EXISTS offerings_fts USING fts5(
                offering_name, agent_name, description,
                content='offerings', content_rowid='id',
                tokenize='unicode61 remove_diacritics 2'
            );
        " + OfferingsFtsTriggersSql + @"

            -- Phase 3: reputation time-series. One row per (agent, UTC date);
            -- 90-day retention pruned by the warmer post-pass.
            CREATE TABLE IF NOT EXISTS agent_reputation_history (
                agent_address      TEXT    NOT NULL,
                snapshot_date      TEXT    NOT NULL,
                agent_score        INTEGER NOT NULL,
                sub_scores_json    TEXT    NOT NULL,
                raw_counts_json    TEXT    NOT NULL,
                PRIMARY KEY (agent_address, snapshot_date)
            );

            CREATE INDEX IF NOT EXISTS ix_rep_history_agent_date
                ON agent_reputation_history(agent_address, snapshot_date DESC);

            -- v1.3: V2 marketplace enumeration cache. Holds distinct seller
            -- wallets observed from JobCreated events on the V2 ACP contract.
            -- Populated incrementally by ChainEventScanner; consumed by
            -- AcpV2MarketplaceSource as enumeration Source A.
            CREATE TABLE IF NOT EXISTS v2_known_sellers (
                agent_address      TEXT    PRIMARY KEY,
                first_seen_block   INTEGER NOT NULL,
                first_seen_at      TEXT    NOT NULL,
                last_seen_block    INTEGER NOT NULL,
                last_seen_at       TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_v2_sellers_last_seen ON v2_known_sellers(last_seen_at);

            -- v1.3: Block checkpoint for the V2 seller enumerator. Single-row
            -- table — the latest scanned block height we've covered for the
            -- 'all sellers' pass on the V2 contract.
            CREATE TABLE IF NOT EXISTS v2_seller_scan_checkpoint (
                key                TEXT    PRIMARY KEY,
                last_scanned_block INTEGER NOT NULL,
                updated_at         TEXT    NOT NULL
            );
            ";
        await cmd.ExecuteNonQueryAsync();

        // Idempotent column additions for databases created before later
        // columns existed. SQLite has no `ADD COLUMN IF NOT EXISTS`, so each
        // ALTER is run in isolation and "duplicate column name" errors are
        // swallowed. Order matters only for v1.3's marketplace_version, which
        // the constraint-rebuild step below depends on having present.
        foreach (var alter in new[]
        {
            "ALTER TABLE offerings ADD COLUMN usage_count INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE offerings ADD COLUMN agent_job_count INTEGER NOT NULL DEFAULT 0",
            // v1.3: V2 marketplace source. Default 'v1' so every existing row
            // is correctly tagged for the legacy V1 source.
            "ALTER TABLE offerings ADD COLUMN marketplace_version TEXT NOT NULL DEFAULT 'v1'",
            // v1.3: per-watch marketplace filter. Null means cross-version.
            "ALTER TABLE watches ADD COLUMN marketplace TEXT",
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

        // v1.3: index over marketplace_version. Runs after the ALTER above so
        // the column is guaranteed to exist on legacy DBs.
        await using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_offerings_mv ON offerings(marketplace_version);";
            await idxCmd.ExecuteNonQueryAsync();
        }

        // v1.3: rebuild offerings table to swap UNIQUE(agent_address,
        // offering_name) for UNIQUE(marketplace_version, agent_address,
        // offering_name). Required because SQLite cannot drop an existing
        // UNIQUE constraint in place. Detect by reading sqlite_master for the
        // current DDL — if it does not already mention the composite key, we
        // need to rebuild. Idempotent: a second run is a no-op.
        await using (var ddlProbe = conn.CreateCommand())
        {
            ddlProbe.CommandText =
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='offerings';";
            var ddl = (string?)(await ddlProbe.ExecuteScalarAsync()) ?? "";
            var hasCompositeUnique =
                ddl.Contains("UNIQUE(marketplace_version", StringComparison.OrdinalIgnoreCase) ||
                ddl.Contains("UNIQUE (marketplace_version", StringComparison.OrdinalIgnoreCase);
            if (!hasCompositeUnique)
            {
                await RebuildOfferingsTableForCompositeUniqueAsync(conn);
            }
        }

        // v1.3.1: repair the dangling offering_embeddings FK left behind by
        // the v1.3 offerings rebuild. SQLite (legacy_alter_table=OFF, default
        // since 3.25) rewrites FK references when a parent table is renamed,
        // so RENAME offerings -> offerings_old_for_mv_migration also rewrote
        // offering_embeddings's FK to point at the renamed table. The
        // subsequent DROP of offerings_old_for_mv_migration left the FK
        // pointing at a table that no longer exists. With foreign_keys=ON,
        // every INSERT into offering_embeddings then fails with
        // "no such table: main.offerings_old_for_mv_migration", which on prod
        // silently broke embedding for both new V2 and new V1 rows since the
        // v1.3 deploy. Detect by scanning offering_embeddings.sql for the
        // obsolete table name, and rebuild to refresh the FK target.
        await using (var fkProbe = conn.CreateCommand())
        {
            fkProbe.CommandText =
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='offering_embeddings';";
            var ddl = (string?)(await fkProbe.ExecuteScalarAsync()) ?? "";
            if (ddl.Contains("offerings_old_for_mv_migration", StringComparison.OrdinalIgnoreCase))
            {
                await RebuildOfferingEmbeddingsToRefreshFkAsync(conn);
            }
        }

        // FTS5 sync. Use SQLite's native 'rebuild' command (the documented way
        // to populate an external-content FTS5 from existing data) rather than
        // a manual INSERT...SELECT. 'rebuild' is atomic and guarantees the
        // index matches the parent table — manual inserts can leave the index
        // and content out of sync, which then causes corruption when the
        // AFTER UPDATE trigger's 'delete' command can't find a matching row.
        // Only triggered when the row counts disagree, so steady-state boots
        // are a no-op.
        await using (var rebuild = conn.CreateCommand())
        {
            rebuild.CommandText = @"
                SELECT
                    (SELECT COUNT(*) FROM offerings) -
                    (SELECT COUNT(*) FROM offerings_fts);";
            long diff = 0;
            try { diff = (long)(await rebuild.ExecuteScalarAsync() ?? 0L); }
            catch (SqliteException ex)
            {
                Console.Error.WriteLine($"[db] WARNING: FTS5 row-count probe failed ({ex.Message}); skipping rebuild.");
            }

            if (diff != 0)
            {
                Console.Error.WriteLine($"[db] FTS5 out of sync (diff={diff}); rebuilding offerings_fts.");
                await using var rb = conn.CreateCommand();
                rb.CommandText = "INSERT INTO offerings_fts(offerings_fts) VALUES('rebuild');";
                try
                {
                    await rb.ExecuteNonQueryAsync();
                }
                catch (SqliteException ex)
                {
                    Console.Error.WriteLine($"[db] WARNING: FTS5 rebuild failed ({ex.Message}); hybrid search will fall back to dense-only.");
                }
            }
        }

        // FTS5 availability probe — Phase 1 hybrid search needs it. Microsoft.Data.Sqlite 9.0.*
        // bundles SQLite with FTS5 enabled; this probe surfaces the failure early on the rare
        // build where it isn't, before the offerings_fts CREATE blows up.
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pragma_compile_options() WHERE compile_options = 'ENABLE_FTS5' LIMIT 1";
            var hasFts5 = await probe.ExecuteScalarAsync();
            if (hasFts5 is null)
            {
                Console.Error.WriteLine("[db] WARNING: SQLite ENABLE_FTS5 not detected — hybrid search will fall back to dense-only.");
            }
        }
    }

    /// <summary>
    /// Atomic rebuild of the offerings table to swap the legacy
    /// UNIQUE(agent_address, offering_name) constraint for the v1.3
    /// composite UNIQUE(marketplace_version, agent_address, offering_name).
    /// SQLite cannot drop an existing UNIQUE in place, so we use the
    /// documented "rename + recreate + copy + drop" idiom inside a
    /// transaction. FTS triggers must be dropped before the rename
    /// (they reference offerings) and recreated after.
    /// </summary>
    private static async Task RebuildOfferingsTableForCompositeUniqueAsync(SqliteConnection conn)
    {
        Console.Error.WriteLine("[db] v1.3 migration: rebuilding offerings to swap UNIQUE constraint.");

        // PRAGMA foreign_keys cannot be toggled inside a transaction in
        // SQLite — set it before BEGIN.
        await ExecAsync(conn, "PRAGMA foreign_keys = OFF;");
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            // 1. Drop FTS triggers — they reference offerings and will
            //    block the RENAME.
            await ExecAsync(conn, tx, "DROP TRIGGER IF EXISTS offerings_ai;");
            await ExecAsync(conn, tx, "DROP TRIGGER IF EXISTS offerings_au;");
            await ExecAsync(conn, tx, "DROP TRIGGER IF EXISTS offerings_ad;");

            // 2. Rename the old table out of the way.
            await ExecAsync(conn, tx,
                "ALTER TABLE offerings RENAME TO offerings_old_for_mv_migration;");

            // 3. Create the new table with the composite UNIQUE.
            await ExecAsync(conn, tx, @"
                CREATE TABLE offerings (
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
                    marketplace_version      TEXT    NOT NULL DEFAULT 'v1',
                    UNIQUE(marketplace_version, agent_address, offering_name)
                );");

            // 4. Copy every row, preserving the existing autoincrement id so
            //    foreign-key references (offering_embeddings,
            //    agent_reputation_snapshots, watch_seen) stay valid. Default
            //    marketplace_version to 'v1' for any pre-migration row that
            //    somehow has NULL.
            await ExecAsync(conn, tx, @"
                INSERT INTO offerings (
                    id, agent_address, agent_name, offering_name, description,
                    requirement_schema_json, price_usdc, price_type, is_private,
                    chain, content_hash, first_seen_at, last_seen_at,
                    usage_count, agent_job_count, marketplace_version
                )
                SELECT
                    id, agent_address, agent_name, offering_name, description,
                    requirement_schema_json, price_usdc, price_type, is_private,
                    chain, content_hash, first_seen_at, last_seen_at,
                    usage_count, agent_job_count,
                    COALESCE(marketplace_version, 'v1')
                FROM offerings_old_for_mv_migration;");

            // 5. Drop the old table.
            await ExecAsync(conn, tx, "DROP TABLE offerings_old_for_mv_migration;");

            // 6. Recreate the explicit indexes (the autoincrement primary key
            //    index is recreated by the CREATE TABLE itself).
            await ExecAsync(conn, tx,
                "CREATE INDEX IF NOT EXISTS ix_offerings_content_hash ON offerings(content_hash);");
            await ExecAsync(conn, tx,
                "CREATE INDEX IF NOT EXISTS ix_offerings_last_seen    ON offerings(last_seen_at);");
            await ExecAsync(conn, tx,
                "CREATE INDEX IF NOT EXISTS ix_offerings_agent        ON offerings(agent_address);");
            await ExecAsync(conn, tx,
                "CREATE INDEX IF NOT EXISTS ix_offerings_mv           ON offerings(marketplace_version);");

            // 7. Recreate FTS triggers.
            await ExecAsync(conn, tx, OfferingsFtsTriggersSql);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        finally
        {
            await ExecAsync(conn, "PRAGMA foreign_keys = ON;");
        }

        Console.Error.WriteLine("[db] v1.3 migration: offerings rebuild complete.");
    }

    /// <summary>
    /// Rebuilds offering_embeddings to refresh its FOREIGN KEY target. The
    /// v1.3 offerings rebuild renames offerings out of the way and drops the
    /// renamed copy. Modern SQLite rewrites FK references during the parent
    /// rename, so offering_embeddings ends up referencing the now-dropped
    /// offerings_old_for_mv_migration. Without this repair, every INSERT
    /// into offering_embeddings raises "no such table" with foreign_keys=ON.
    /// </summary>
    private static async Task RebuildOfferingEmbeddingsToRefreshFkAsync(SqliteConnection conn)
    {
        Console.Error.WriteLine("[db] v1.3.1 repair: rebuilding offering_embeddings to refresh dangling FK.");

        await ExecAsync(conn, "PRAGMA foreign_keys = OFF;");
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            await ExecAsync(conn, tx,
                "ALTER TABLE offering_embeddings RENAME TO offering_embeddings_fkfix_old;");
            await ExecAsync(conn, tx, @"
                CREATE TABLE offering_embeddings (
                    offering_id     INTEGER PRIMARY KEY,
                    model           TEXT    NOT NULL,
                    dimension       INTEGER NOT NULL,
                    embedding_blob  BLOB    NOT NULL,
                    embedded_at     TEXT    NOT NULL,
                    FOREIGN KEY (offering_id) REFERENCES offerings(id) ON DELETE CASCADE
                );");
            await ExecAsync(conn, tx, @"
                INSERT INTO offering_embeddings
                  (offering_id, model, dimension, embedding_blob, embedded_at)
                SELECT offering_id, model, dimension, embedding_blob, embedded_at
                FROM offering_embeddings_fkfix_old;");
            await ExecAsync(conn, tx, "DROP TABLE offering_embeddings_fkfix_old;");
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        finally
        {
            await ExecAsync(conn, "PRAGMA foreign_keys = ON;");
        }

        Console.Error.WriteLine("[db] v1.3.1 repair: offering_embeddings rebuild complete.");
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecAsync(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
