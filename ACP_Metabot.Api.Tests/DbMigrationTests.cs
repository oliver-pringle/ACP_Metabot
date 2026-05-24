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

    [Fact]
    public async Task LegacyMigration_PreservesEmbeddingFkTarget()
    {
        // Reproduces the prod v1.3 bug: legacy DB has offering_embeddings with
        // FOREIGN KEY (offering_id) REFERENCES offerings(id). The v1.3 migration
        // RENAMEs offerings -> offerings_old_for_mv_migration, creates new
        // offerings, copies rows, drops the old. In modern SQLite (default
        // legacy_alter_table=OFF), the RENAME rewrites the FK reference in
        // offering_embeddings to point at the renamed table. After the DROP,
        // the FK target no longer exists, and any INSERT into offering_embeddings
        // throws "no such table: offerings_old_for_mv_migration" with FK ON.
        // That is exactly what's blocking V2 (and any new V1) rows from being
        // embedded on the production droplet.
        await CreateLegacySchemaAsync();
        await CreateLegacyEmbeddingTableAsync();
        await SeedOfferingsLegacy(rowCount: 3);

        // Seed an embedding for offering id=1 in the legacy schema, simulating
        // the prod state before the v1.3 deploy.
        await using (var conn = _db.OpenConnection())
        {
            await using var ins = conn.CreateCommand();
            ins.CommandText = @"
                INSERT INTO offering_embeddings
                  (offering_id, model, dimension, embedding_blob, embedded_at)
                VALUES (1, 'voyage-finance-2', 4, $blob, $now);";
            ins.Parameters.AddWithValue("$blob", new byte[16]);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await ins.ExecuteNonQueryAsync();
        }

        // Run the v1.3 migration.
        await _db.InitializeSchemaAsync();

        // The legacy embedding row should still be there (PreservedDuringRebuild).
        Assert.Equal(1, await Count("offering_embeddings"));

        // Insert a NEW offering (simulating a V2 row arriving after migration).
        // The trigger path will create offerings row id=4.
        await InsertOffering("0xdead", "agent-v2", "search", "v2");
        var newId = await ScalarLong("SELECT MAX(id) FROM offerings;");

        // CRITICAL: insert a NEW embedding for that new offering. This is the
        // path that fails on prod. With FK pointing to a dropped legacy table,
        // SQLite raises "no such table" before the insert even reaches the
        // offering_embeddings storage.
        await using (var conn = _db.OpenConnection())
        {
            await using var ins = conn.CreateCommand();
            ins.CommandText = @"
                INSERT INTO offering_embeddings
                  (offering_id, model, dimension, embedding_blob, embedded_at)
                VALUES ($id, 'voyage-finance-2', 4, $blob, $now);";
            ins.Parameters.AddWithValue("$id", newId);
            ins.Parameters.AddWithValue("$blob", new byte[16]);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await ins.ExecuteNonQueryAsync();
        }

        Assert.Equal(2, await Count("offering_embeddings"));
        Assert.Equal("ok", await IntegrityCheck());
    }

    // -----------------------------------------------------------------
    // v1.5 tombstone + re-embed-on-content-change tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task FreshDb_HasIsRemovedAndRemovedAtColumns()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(offerings);";
        await using var reader = await cmd.ExecuteReaderAsync();
        var cols = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) cols.Add(reader.GetString(1));
        Assert.Contains("is_removed", cols);
        Assert.Contains("removed_at", cols);
    }

    [Fact]
    public async Task LegacyMigration_AddsTombstoneColumnsAsZero()
    {
        await CreateLegacySchemaAsync();
        await SeedOfferingsLegacy(rowCount: 3);
        await _db.InitializeSchemaAsync();

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_removed, removed_at FROM offerings;";
        await using var reader = await cmd.ExecuteReaderAsync();
        int rowCount = 0;
        while (await reader.ReadAsync())
        {
            Assert.Equal(0, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
            rowCount++;
        }
        Assert.Equal(3, rowCount);
    }

    [Fact]
    public async Task MarkStaleAsRemoved_TombstonesOnlyMatchingMvAndStaleRows()
    {
        await _db.InitializeSchemaAsync();
        var repo = new OfferingRepository(_db);
        var nowUtc = DateTime.UtcNow;

        // Insert 4 offerings; 2 fresh, 2 stale, mixed marketplace.
        await InsertOfferingWithLastSeen("0x01", "a1", "fresh-v1",  "v1", nowUtc.AddHours(-1));
        await InsertOfferingWithLastSeen("0x02", "a2", "stale-v1",  "v1", nowUtc.AddDays(-3));
        await InsertOfferingWithLastSeen("0x03", "a3", "fresh-v2",  "v2", nowUtc.AddHours(-1));
        await InsertOfferingWithLastSeen("0x04", "a4", "stale-v2",  "v2", nowUtc.AddDays(-10));

        // Sweep V1 with 1-day threshold — only stale-v1 should flip.
        var v1Marked = await repo.MarkStaleAsRemovedAsync("v1", nowUtc.AddDays(-1), nowUtc);
        Assert.Equal(1, v1Marked);

        // Sweep V2 with 7-day threshold — only stale-v2 should flip.
        var v2Marked = await repo.MarkStaleAsRemovedAsync("v2", nowUtc.AddDays(-7), nowUtc);
        Assert.Equal(1, v2Marked);

        // Re-running the V1 sweep is a no-op (the row is already is_removed=1).
        var v1Again = await repo.MarkStaleAsRemovedAsync("v1", nowUtc.AddDays(-1), nowUtc);
        Assert.Equal(0, v1Again);

        // And ListAllAsync now hides the two tombstoned rows.
        var active = await repo.ListAllAsync();
        Assert.Equal(2, active.Count);
        Assert.All(active, o => Assert.False(o.IsRemoved));
    }

    [Fact]
    public async Task UpsertManyAsync_ReactivatesPreviouslyTombstonedRow()
    {
        await _db.InitializeSchemaAsync();
        var repo = new OfferingRepository(_db);
        var nowUtc = DateTime.UtcNow;

        // Seed a stale row, sweep it as removed.
        await InsertOfferingWithLastSeen("0x01", "a1", "search", "v1", nowUtc.AddDays(-3));
        var marked = await repo.MarkStaleAsRemovedAsync("v1", nowUtc.AddDays(-1), nowUtc);
        Assert.Equal(1, marked);
        Assert.Empty(await repo.ListAllAsync()); // hidden

        // The seller puts the offering back. Same content_hash → touch path.
        var item = new UpsertItem(
            AgentAddress: "0x01", AgentName: "a1", OfferingName: "search",
            Description: "test offering", RequirementSchemaJson: null,
            PriceUsdc: 0.10, PriceType: "fixed", IsPrivate: false, Chain: "base",
            ContentHash: "0x01|search|v1",
            UsageCount: 0, AgentJobCount: 0, MarketplaceVersion: "v1");
        var summary = await repo.UpsertManyAsync(new[] { item }, nowUtc);

        // Touch path advances last_seen_at + clears is_removed.
        Assert.Equal(0, summary.Added);
        Assert.Equal(1, summary.Unchanged);
        var active = await repo.ListAllAsync();
        Assert.Single(active);
        Assert.False(active[0].IsRemoved);
        Assert.Null(active[0].RemovedAt);
    }

    [Fact]
    public async Task UpsertManyAsync_ContentChange_DropsExistingEmbedding()
    {
        await _db.InitializeSchemaAsync();
        var repo = new OfferingRepository(_db);
        var nowUtc = DateTime.UtcNow;

        // Seed an offering + embedding.
        await InsertOffering("0x01", "agent", "search", "v1");
        var id = await ScalarLong("SELECT id FROM offerings WHERE offering_name = 'search';");
        await repo.UpsertEmbeddingAsync(id, "voyage-finance-2", 4, new byte[16], nowUtc);
        Assert.Equal(1, await Count("offering_embeddings"));

        // Upsert with a CHANGED content_hash (description rewritten).
        var item = new UpsertItem(
            AgentAddress: "0x01", AgentName: "agent", OfferingName: "search",
            Description: "completely rewritten description",
            RequirementSchemaJson: null,
            PriceUsdc: 0.10, PriceType: "fixed", IsPrivate: false, Chain: "base",
            ContentHash: "NEW-HASH-AFTER-REWRITE",
            UsageCount: 0, AgentJobCount: 0, MarketplaceVersion: "v1");
        var summary = await repo.UpsertManyAsync(new[] { item }, nowUtc);

        Assert.Equal(1, summary.Updated);
        // The old embedding should have been dropped so EmbedPendingAsync
        // re-fires for the new description on the next indexer cycle.
        Assert.Equal(0, await Count("offering_embeddings"));
    }

    [Fact]
    public async Task UpsertManyAsync_NoContentChange_KeepsExistingEmbedding()
    {
        await _db.InitializeSchemaAsync();
        var repo = new OfferingRepository(_db);
        var nowUtc = DateTime.UtcNow;

        // Seed an offering with a known content_hash, plus a matching embedding.
        await InsertOfferingWithHash("0x01", "agent", "search", "v1", "STABLE-HASH");
        var id = await ScalarLong("SELECT id FROM offerings WHERE offering_name = 'search';");
        await repo.UpsertEmbeddingAsync(id, "voyage-finance-2", 4, new byte[16], nowUtc);
        Assert.Equal(1, await Count("offering_embeddings"));

        // Upsert with the SAME content_hash (just bumping last_seen_at +
        // counters). The touch path must NOT delete the embedding.
        var item = new UpsertItem(
            AgentAddress: "0x01", AgentName: "agent", OfferingName: "search",
            Description: "test offering", RequirementSchemaJson: null,
            PriceUsdc: 0.10, PriceType: "fixed", IsPrivate: false, Chain: "base",
            ContentHash: "STABLE-HASH",
            UsageCount: 5, AgentJobCount: 5, MarketplaceVersion: "v1");
        var summary = await repo.UpsertManyAsync(new[] { item }, nowUtc);

        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(1, await Count("offering_embeddings"));
    }

    // -----------------------------------------------------------------
    // v1.7 agent_profiles schema tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task Migration_CreatesAgentProfilesTable()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='agent_profiles'";
        var n = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Migration_CreatesAgentProfilesFtsAndTriggers()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();

        await using var ftsCmd = conn.CreateCommand();
        ftsCmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='agent_profiles_fts'";
        Assert.Equal(1L, (long)(await ftsCmd.ExecuteScalarAsync())!);

        await using var trgCmd = conn.CreateCommand();
        trgCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'agent_profiles_%' ORDER BY name";
        var triggers = new List<string>();
        await using var r = await trgCmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) triggers.Add(r.GetString(0));
        Assert.Equal(new[] { "agent_profiles_ad", "agent_profiles_ai", "agent_profiles_au" }, triggers);
    }

    [Fact]
    public async Task Migration_AgentProfilesDirtyIndexWorks()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();

        await using var ins = conn.CreateCommand();
        ins.CommandText = @"
            INSERT INTO agent_profiles (agent_address, agent_name, profile_text, last_change_at)
            VALUES ('0xabc', 'AgentA', 'profile A', '2026-05-04T00:00:00Z')";
        await ins.ExecuteNonQueryAsync();

        await using var explain = conn.CreateCommand();
        explain.CommandText = @"
            EXPLAIN QUERY PLAN
            SELECT agent_address FROM agent_profiles
            WHERE embedded_at IS NULL OR last_change_at > embedded_at";
        var plan = "";
        await using var pr = await explain.ExecuteReaderAsync();
        while (await pr.ReadAsync()) plan += pr.GetString(3) + "|";
        Assert.Contains("ix_agent_profiles_dirty", plan);
    }

    [Fact]
    public async Task AgentProfilesFts_RoundTripsInsertAndMatch()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();

        await using var ins = conn.CreateCommand();
        ins.CommandText = @"
            INSERT INTO agent_profiles (agent_address, agent_name, profile_text, last_change_at)
            VALUES ('0xabc', 'WhaleWatcher', 'tracks large on-chain holders', '2026-05-04T00:00:00Z')";
        await ins.ExecuteNonQueryAsync();

        await using var match = conn.CreateCommand();
        match.CommandText = @"
            SELECT ap.agent_address
            FROM agent_profiles_fts
            JOIN agent_profiles ap ON ap.id = agent_profiles_fts.rowid
            WHERE agent_profiles_fts MATCH 'whale OR holders'";
        var hit = (string?)await match.ExecuteScalarAsync();
        Assert.Equal("0xabc", hit);
    }

    // -----------------------------------------------------------------
    // v1.10 Phase 1 — resources_embeddings + resources_fts schema tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task Migration_creates_resources_embeddings_and_resources_fts()
    {
        await _db.InitializeSchemaAsync();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name FROM sqlite_master
            WHERE type IN ('table','index','trigger')
              AND name IN ('resources_embeddings', 'resources_fts',
                           'ix_resources_embed_res',
                           'trg_agent_resources_ai', 'trg_agent_resources_au',
                           'trg_agent_resources_ad')
            ORDER BY name;";
        var found = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) found.Add(rdr.GetString(0));
        Assert.Equal(
            new[] { "ix_resources_embed_res", "resources_embeddings", "resources_fts",
                    "trg_agent_resources_ad", "trg_agent_resources_ai", "trg_agent_resources_au" },
            found.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    // -----------------------------------------------------------------
    // v1.10 Phase 2 — schema_facets schema tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task Migration_creates_schema_facets()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"metabot-sf-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={dbPath}"
            }).Build();
        var db = new Db(config);
        await db.InitializeSchemaAsync();
        try
        {
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT name FROM sqlite_master
                WHERE type IN ('table','index')
                  AND name IN ('schema_facets', 'ix_schema_facets_field', 'ix_schema_facets_off')
                ORDER BY name;";
            var found = new List<string>();
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) found.Add(rdr.GetString(0));
            Assert.Equal(
                new[] { "ix_schema_facets_field", "ix_schema_facets_off", "schema_facets" },
                found.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task Migration_adds_deliverable_schema_json_to_offerings()
    {
        // v1.10 Phase 2 T3a: the offerings table gains a deliverable_schema_json
        // column so the upsert path can persist the deliverable schema upstream
        // V2 returns (Record<string, unknown> | string per AcpAgentOffering)
        // and feed it into WriteSchemaFacetsAsync for the 'deliverable' role.
        var dbPath = Path.Combine(Path.GetTempPath(), $"metabot-del-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={dbPath}"
            }).Build();
        var db = new Db(config);
        await db.InitializeSchemaAsync();
        try
        {
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(offerings);";
            var hasColumn = false;
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var colName = rdr.GetString(1);
                if (colName == "deliverable_schema_json") { hasColumn = true; break; }
            }
            Assert.True(hasColumn, "offerings.deliverable_schema_json column should exist after migration");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task LegacyMigration_addsDeliverableSchemaJsonAsNull()
    {
        // Legacy DB (v1.2 shape, no marketplace_version, no tombstone, no
        // deliverable_schema_json). After migration, every row must have
        // deliverable_schema_json = NULL — no fake data.
        await CreateLegacySchemaAsync();
        await SeedOfferingsLegacy(rowCount: 4);
        await _db.InitializeSchemaAsync();

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT deliverable_schema_json FROM offerings;";
        await using var reader = await cmd.ExecuteReaderAsync();
        int rowCount = 0;
        while (await reader.ReadAsync())
        {
            Assert.True(reader.IsDBNull(0));
            rowCount++;
        }
        Assert.Equal(4, rowCount);
    }

    [Fact]
    public async Task UpsertManyAsync_persistsBothSchemaRolesIntoFacets()
    {
        // T3a end-to-end smoke: an UpsertItem with BOTH requirement and
        // deliverable schemas must populate schema_facets with rows for
        // both roles AND persist both columns onto offerings.
        await _db.InitializeSchemaAsync();
        var repo = new OfferingRepository(_db);
        var nowUtc = DateTime.UtcNow;

        var item = new UpsertItem(
            AgentAddress: "0xab", AgentName: "agent", OfferingName: "fetch",
            Description: "test offering",
            RequirementSchemaJson: """{"type":"object","properties":{"walletAddress":{"type":"string"}}}""",
            PriceUsdc: 0.10, PriceType: "fixed", IsPrivate: false, Chain: "base",
            ContentHash: "h1",
            UsageCount: 0, AgentJobCount: 0, MarketplaceVersion: "v2",
            DeliverableSchemaJson: """{"type":"object","properties":{"txHash":{"type":"string"},"blockNumber":{"type":"integer"}}}""");

        var summary = await repo.UpsertManyAsync(new[] { item }, nowUtc);
        Assert.Equal(1, summary.Added);

        // Both columns persisted.
        await using var conn = _db.OpenConnection();
        await using var colCmd = conn.CreateCommand();
        colCmd.CommandText = @"
            SELECT requirement_schema_json IS NOT NULL,
                   deliverable_schema_json IS NOT NULL
            FROM offerings;";
        await using var colReader = await colCmd.ExecuteReaderAsync();
        Assert.True(await colReader.ReadAsync());
        Assert.Equal(1, colReader.GetInt32(0));
        Assert.Equal(1, colReader.GetInt32(1));
        await colReader.CloseAsync();

        // Facets populated for BOTH roles.
        await using var facetCmd = conn.CreateCommand();
        facetCmd.CommandText = @"
            SELECT role, field_name FROM schema_facets
            ORDER BY role, field_name;";
        var facets = new List<(string Role, string Name)>();
        await using var fr = await facetCmd.ExecuteReaderAsync();
        while (await fr.ReadAsync())
        {
            facets.Add((fr.GetString(0), fr.GetString(1)));
        }
        // Expect: deliverable.blocknumber, deliverable.txhash, requirement.walletaddress
        Assert.Equal(3, facets.Count);
        Assert.Contains(facets, f => f.Role == "deliverable" && f.Name == "txhash");
        Assert.Contains(facets, f => f.Role == "deliverable" && f.Name == "blocknumber");
        Assert.Contains(facets, f => f.Role == "requirement" && f.Name == "walletaddress");
    }

    [Fact]
    public async Task UpsertManyAsync_contentChange_replacesDeliverableFacets()
    {
        // T3a lifecycle: when an offering's deliverable schema changes, the
        // stale deliverable facets must be cleared AND the new ones written.
        // The requirement facets must survive (they get cleared and rewritten
        // in the same DELETE-then-INSERT pass).
        await _db.InitializeSchemaAsync();
        var repo = new OfferingRepository(_db);
        var nowUtc = DateTime.UtcNow;

        // v1: deliverable has fields A,B
        var v1 = new UpsertItem(
            AgentAddress: "0xab", AgentName: "agent", OfferingName: "fetch",
            Description: "test", RequirementSchemaJson: """{"properties":{"x":{}}}""",
            PriceUsdc: 0.10, PriceType: "fixed", IsPrivate: false, Chain: "base",
            ContentHash: "h1",
            UsageCount: 0, AgentJobCount: 0, MarketplaceVersion: "v2",
            DeliverableSchemaJson: """{"properties":{"a":{},"b":{}}}""");
        await repo.UpsertManyAsync(new[] { v1 }, nowUtc);

        // v2: deliverable now has fields C,D — A and B should be removed.
        var v2 = v1 with
        {
            ContentHash = "h2",
            DeliverableSchemaJson = """{"properties":{"c":{},"d":{}}}"""
        };
        var summary = await repo.UpsertManyAsync(new[] { v2 }, nowUtc.AddMinutes(1));
        Assert.Equal(1, summary.Updated);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT role, field_name FROM schema_facets
            ORDER BY role, field_name;";
        var facets = new List<(string Role, string Name)>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
            facets.Add((rdr.GetString(0), rdr.GetString(1)));
        Assert.Equal(3, facets.Count);
        Assert.Contains(facets, f => f.Role == "deliverable" && f.Name == "c");
        Assert.Contains(facets, f => f.Role == "deliverable" && f.Name == "d");
        Assert.Contains(facets, f => f.Role == "requirement" && f.Name == "x");
        Assert.DoesNotContain(facets, f => f.Name == "a" || f.Name == "b");
    }

    // -----------------------------------------------------------------
    // v1.10 Phase 3 — query_rewrite_spend + search_narratives_cache
    //                 + agent_risk_cache schema tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task Migration_creates_phase3_tables()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"metabot-p3-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={dbPath}"
            }).Build();
        var db = new Db(config);
        await db.InitializeSchemaAsync();
        try
        {
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT name FROM sqlite_master
                WHERE type='table'
                  AND name IN ('query_rewrite_spend', 'search_narratives_cache', 'agent_risk_cache')
                ORDER BY name;";
            var found = new List<string>();
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) found.Add(rdr.GetString(0));
            Assert.Equal(
                new[] { "agent_risk_cache", "query_rewrite_spend", "search_narratives_cache" },
                found.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    private async Task InsertOfferingWithLastSeen(string addr, string agentName,
        string offeringName, string mv, DateTime lastSeenUtc)
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
                $first, $last, $mv
            );";
        ins.Parameters.AddWithValue("$a", addr);
        ins.Parameters.AddWithValue("$an", agentName);
        ins.Parameters.AddWithValue("$on", offeringName);
        ins.Parameters.AddWithValue("$desc", "test offering");
        ins.Parameters.AddWithValue("$hash", $"{addr}|{offeringName}|{mv}");
        ins.Parameters.AddWithValue("$first", lastSeenUtc.AddDays(-30).ToString("O"));
        ins.Parameters.AddWithValue("$last",  lastSeenUtc.ToString("O"));
        ins.Parameters.AddWithValue("$mv", mv);
        await ins.ExecuteNonQueryAsync();
    }

    private async Task InsertOfferingWithHash(string addr, string agentName,
        string offeringName, string mv, string hash)
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
        ins.Parameters.AddWithValue("$hash", hash);
        ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        ins.Parameters.AddWithValue("$mv", mv);
        await ins.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Builds the legacy offering_embeddings table with the FK that bites
    /// the v1.3 migration.
    /// </summary>
    private async Task CreateLegacyEmbeddingTableAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE offering_embeddings (
                offering_id     INTEGER PRIMARY KEY,
                model           TEXT    NOT NULL,
                dimension       INTEGER NOT NULL,
                embedding_blob  BLOB    NOT NULL,
                embedded_at     TEXT    NOT NULL,
                FOREIGN KEY (offering_id) REFERENCES offerings(id) ON DELETE CASCADE
            );";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarLong(string sql)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
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
