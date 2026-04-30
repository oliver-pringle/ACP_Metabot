using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// Regression tests for Db.InitializeSchemaAsync. Catches the v1.2 prod
/// failure where the AFTER UPDATE trigger fired on every offerings touch
/// and corrupted the offerings_fts shadow pages.
/// </summary>
public class DbMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public DbMigrationTests()
    {
        // Per-test temp file so xunit parallelism doesn't collide.
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_migration_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Migration_OnFreshDb_PassesIntegrityCheck()
    {
        await _db.InitializeSchemaAsync();
        Assert.Equal("ok", await IntegrityCheck());
    }

    [Fact]
    public async Task Migration_RunTwice_IsIdempotent()
    {
        await _db.InitializeSchemaAsync();
        await _db.InitializeSchemaAsync();
        Assert.Equal("ok", await IntegrityCheck());
    }

    [Fact]
    public async Task IndexerTouchPattern_DoesNotCorruptFtsIndex()
    {
        // Reproduces the v1.2 prod failure: bulk UPDATE of last_seen_at +
        // counters (none of which are FTS-mirrored) inside one transaction
        // for ~thousands of rows. Pre-fix, this corrupted offerings_fts; the
        // fix (AFTER UPDATE OF offering_name, agent_name, description) makes
        // the trigger skip these UPDATEs entirely.
        await _db.InitializeSchemaAsync();
        await SeedOfferings(rowCount: 500);

        // Migration should rebuild offerings_fts to match offerings.
        await _db.InitializeSchemaAsync();
        Assert.Equal(500, await Count("offerings"));
        Assert.Equal(500, await Count("offerings_fts"));

        // Simulate 5 indexer cycles — each touches every row.
        for (int cycle = 0; cycle < 5; cycle++)
            await SimulateTouchCycle();

        Assert.Equal("ok", await IntegrityCheck());
        // FTS row count must still match offerings; a corrupt index
        // can present with phantom rows or missing ones.
        Assert.Equal(500, await Count("offerings_fts"));
    }

    [Fact]
    public async Task ContentChangeUpdate_KeepsFtsIndexInSync()
    {
        await _db.InitializeSchemaAsync();
        await SeedOfferings(rowCount: 10);
        await _db.InitializeSchemaAsync();

        // Update one row's description — the trigger SHOULD fire here.
        await Exec(@"
            UPDATE offerings SET description = 'a brand new description'
            WHERE id = 5;");
        Assert.Equal("ok", await IntegrityCheck());

        // BM25 search for the new token must find the updated row.
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM offerings_fts WHERE offerings_fts MATCH 'brand';";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(5L, reader.GetInt64(0));
    }

    // -----------------------------------------------------------------
    // v1.3 migration tests — marketplace_version + composite UNIQUE
    // -----------------------------------------------------------------

    [Fact]
    public async Task FreshDb_HasMarketplaceVersionColumnDefaultV1()
    {
        await _db.InitializeSchemaAsync();
        await SeedOfferings(rowCount: 3);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT marketplace_version FROM offerings ORDER BY id;";
        await using var reader = await cmd.ExecuteReaderAsync();
        var versions = new List<string>();
        while (await reader.ReadAsync()) versions.Add(reader.GetString(0));
        Assert.Equal(new[] { "v1", "v1", "v1" }, versions);
    }

    [Fact]
    public async Task FreshDb_OfferingsDdlMentionsCompositeUnique()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='offerings';";
        var ddl = (string?)(await cmd.ExecuteScalarAsync()) ?? "";
        Assert.Contains("UNIQUE(marketplace_version, agent_address, offering_name)", ddl);
    }

    [Fact]
    public async Task LegacySchema_GetsMigratedToCompositeUnique()
    {
        // Build a "v1.2-shaped" DB by hand — no marketplace_version column,
        // legacy UNIQUE(agent_address, offering_name).
        await CreateLegacySchemaAsync();
        await SeedOfferingsLegacy(rowCount: 12);
        await using (var conn = _db.OpenConnection())
        {
            await using var pre = conn.CreateCommand();
            pre.CommandText =
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='offerings';";
            var preDdl = (string?)(await pre.ExecuteScalarAsync()) ?? "";
            Assert.Contains("UNIQUE(agent_address, offering_name)", preDdl);
            Assert.DoesNotContain("marketplace_version", preDdl);
        }

        // Run the v1.3 migration.
        await _db.InitializeSchemaAsync();

        // All rows preserved, ids unchanged, version='v1'.
        Assert.Equal(12, await Count("offerings"));
        await using (var conn = _db.OpenConnection())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, marketplace_version FROM offerings ORDER BY id;";
            await using var reader = await cmd.ExecuteReaderAsync();
            long expectedId = 1;
            while (await reader.ReadAsync())
            {
                Assert.Equal(expectedId++, reader.GetInt64(0));
                Assert.Equal("v1", reader.GetString(1));
            }
        }

        // DDL now has the composite UNIQUE.
        await using (var conn = _db.OpenConnection())
        {
            await using var post = conn.CreateCommand();
            post.CommandText =
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='offerings';";
            var postDdl = (string?)(await post.ExecuteScalarAsync()) ?? "";
            Assert.Contains(
                "UNIQUE(marketplace_version, agent_address, offering_name)",
                postDdl);
        }

        // FTS index intact.
        Assert.Equal(12, await Count("offerings_fts"));
        Assert.Equal("ok", await IntegrityCheck());
    }

    [Fact]
    public async Task CompositeUnique_AllowsSameAddrNameAcrossVersions()
    {
        await _db.InitializeSchemaAsync();

        // Insert a v1 row.
        await InsertOffering("0xdead", "agent", "search", "v1");
        // Same wallet+name on v2 must succeed.
        await InsertOffering("0xdead", "agent", "search", "v2");

        // But a duplicate on the same version must fail.
        var dupeEx = await Assert.ThrowsAsync<SqliteException>(async () =>
            await InsertOffering("0xdead", "agent", "search", "v1"));
        Assert.Contains("UNIQUE constraint failed", dupeEx.Message);

        Assert.Equal(2, await Count("offerings"));
        Assert.Equal("ok", await IntegrityCheck());
    }

    [Fact]
    public async Task LegacyMigration_RunTwice_IsIdempotent()
    {
        await CreateLegacySchemaAsync();
        await SeedOfferingsLegacy(rowCount: 5);
        await _db.InitializeSchemaAsync();
        await _db.InitializeSchemaAsync();
        Assert.Equal(5, await Count("offerings"));
        Assert.Equal("ok", await IntegrityCheck());
    }

    [Fact]
    public async Task LegacyMigration_PreservesIndexerTouchSafety()
    {
        // Migration must not re-introduce the v1.2 trigger bug. After
        // migrating from a legacy schema, the touch path on the new offerings
        // table must still leave FTS untouched.
        await CreateLegacySchemaAsync();
        await SeedOfferingsLegacy(rowCount: 50);
        await _db.InitializeSchemaAsync();

        Assert.Equal(50, await Count("offerings_fts"));
        for (int cycle = 0; cycle < 5; cycle++)
            await SimulateTouchCycle();

        Assert.Equal("ok", await IntegrityCheck());
        Assert.Equal(50, await Count("offerings_fts"));
    }

    [Fact]
    public async Task V2EnumerationTables_Created()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name FROM sqlite_master
            WHERE type='table' AND name IN ('v2_known_sellers','v2_seller_scan_checkpoint')
            ORDER BY name;";
        await using var reader = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        Assert.Equal(
            new[] { "v2_known_sellers", "v2_seller_scan_checkpoint" },
            names);
    }

    // ---- v1.3 helpers ----

    /// <summary>
    /// Builds a "v1.2-shaped" offerings table without marketplace_version
    /// and with the legacy UNIQUE(agent_address, offering_name) constraint —
    /// exactly the shape the production droplet's DB is in before the v1.3
    /// migration runs.
    /// </summary>
    private async Task CreateLegacySchemaAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
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
                UNIQUE(agent_address, offering_name)
            );

            CREATE INDEX ix_offerings_content_hash ON offerings(content_hash);
            CREATE INDEX ix_offerings_last_seen    ON offerings(last_seen_at);
            CREATE INDEX ix_offerings_agent        ON offerings(agent_address);

            CREATE VIRTUAL TABLE offerings_fts USING fts5(
                offering_name, agent_name, description,
                content='offerings', content_rowid='id',
                tokenize='unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER offerings_ai AFTER INSERT ON offerings BEGIN
                INSERT INTO offerings_fts(rowid, offering_name, agent_name, description)
                VALUES (new.id, new.offering_name, new.agent_name, new.description);
            END;
            CREATE TRIGGER offerings_ad AFTER DELETE ON offerings BEGIN
                INSERT INTO offerings_fts(offerings_fts, rowid, offering_name, agent_name, description)
                VALUES ('delete', old.id, old.offering_name, old.agent_name, old.description);
            END;
            CREATE TRIGGER offerings_au
            AFTER UPDATE OF offering_name, agent_name, description ON offerings BEGIN
                INSERT INTO offerings_fts(offerings_fts, rowid, offering_name, agent_name, description)
                VALUES ('delete', old.id, old.offering_name, old.agent_name, old.description);
                INSERT INTO offerings_fts(rowid, offering_name, agent_name, description)
                VALUES (new.id, new.offering_name, new.agent_name, new.description);
            END;
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds rows into the legacy schema (no marketplace_version column).</summary>
    private async Task SeedOfferingsLegacy(int rowCount)
    {
        await using var conn = _db.OpenConnection();
        await using var tx = conn.BeginTransaction();
        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
            INSERT INTO offerings (
                agent_address, agent_name, offering_name, description,
                price_usdc, price_type, is_private, chain, content_hash,
                first_seen_at, last_seen_at, usage_count, agent_job_count
            ) VALUES (
                $a, $an, $on, $desc,
                0.10, 'one-time', 0, 'base', $hash,
                $now, $now, 0, 0
            );";
        var pa   = ins.Parameters.Add("$a",   SqliteType.Text);
        var pAn  = ins.Parameters.Add("$an",  SqliteType.Text);
        var pOn  = ins.Parameters.Add("$on",  SqliteType.Text);
        var pDe  = ins.Parameters.Add("$desc", SqliteType.Text);
        var pHa  = ins.Parameters.Add("$hash", SqliteType.Text);
        var pNow = ins.Parameters.Add("$now", SqliteType.Text);
        pNow.Value = DateTime.UtcNow.ToString("O");

        for (int i = 1; i <= rowCount; i++)
        {
            pa.Value  = $"0x{i:x40}";
            pAn.Value = $"agent-{i}";
            pOn.Value = $"offering-{i}";
            pDe.Value = $"a description for offering {i} mentioning swaps and yields";
            pHa.Value = $"hash-{i}";
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private async Task InsertOffering(string addr, string agentName, string offeringName, string mv)
    {
        await using var conn = _db.OpenConnection();
        await using var ins = conn.CreateCommand();
        ins.CommandText = @"
            INSERT INTO offerings (
                agent_address, agent_name, offering_name, description,
                price_usdc, price_type, is_private, chain, content_hash,
                first_seen_at, last_seen_at, marketplace_version
            ) VALUES (
                $a, $an, $on, $desc,
                0.10, 'fixed', 0, 'base', $hash,
                $now, $now, $mv
            );";
        ins.Parameters.AddWithValue("$a", addr);
        ins.Parameters.AddWithValue("$an", agentName);
        ins.Parameters.AddWithValue("$on", offeringName);
        ins.Parameters.AddWithValue("$desc", "test offering");
        ins.Parameters.AddWithValue("$hash", $"{addr}|{offeringName}|{mv}");
        ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        ins.Parameters.AddWithValue("$mv", mv);
        await ins.ExecuteNonQueryAsync();
    }

    // ---- helpers ----

    private async Task SeedOfferings(int rowCount)
    {
        await using var conn = _db.OpenConnection();
        await using var tx = conn.BeginTransaction();
        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
            INSERT INTO offerings (
                agent_address, agent_name, offering_name, description,
                price_usdc, price_type, is_private, chain, content_hash,
                first_seen_at, last_seen_at, usage_count, agent_job_count
            ) VALUES (
                $a, $an, $on, $desc,
                0.10, 'one-time', 0, 'base', $hash,
                $now, $now, 0, 0
            );";
        var pa   = ins.Parameters.Add("$a",   SqliteType.Text);
        var pAn  = ins.Parameters.Add("$an",  SqliteType.Text);
        var pOn  = ins.Parameters.Add("$on",  SqliteType.Text);
        var pDe  = ins.Parameters.Add("$desc", SqliteType.Text);
        var pHa  = ins.Parameters.Add("$hash", SqliteType.Text);
        var pNow = ins.Parameters.Add("$now", SqliteType.Text);
        pNow.Value = DateTime.UtcNow.ToString("O");

        for (int i = 1; i <= rowCount; i++)
        {
            pa.Value  = $"0x{i:x40}";
            pAn.Value = $"agent-{i}";
            pOn.Value = $"offering-{i}";
            pDe.Value = $"a description for offering {i} mentioning swaps and yields";
            pHa.Value = $"hash-{i}";
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    /// Mirrors the touch path inside OfferingRepository.UpsertManyAsync —
    /// only writes columns that aren't FTS-mirrored.
    private async Task SimulateTouchCycle()
    {
        await using var conn = _db.OpenConnection();
        await using var tx = conn.BeginTransaction();
        await using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = @"
            UPDATE offerings
            SET last_seen_at = $now, usage_count = usage_count + 1, agent_job_count = agent_job_count + 1
            WHERE id = $id;";
        var pNow = upd.Parameters.Add("$now", SqliteType.Text);
        var pId  = upd.Parameters.Add("$id",  SqliteType.Integer);
        pNow.Value = DateTime.UtcNow.ToString("O");

        await using var idsCmd = conn.CreateCommand();
        idsCmd.Transaction = tx;
        idsCmd.CommandText = "SELECT id FROM offerings;";
        var ids = new List<long>();
        await using (var reader = await idsCmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));

        foreach (var id in ids)
        {
            pId.Value = id;
            await upd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private async Task Exec(string sql)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> Count(string table)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<string> IntegrityCheck()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }
}
