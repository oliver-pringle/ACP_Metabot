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
