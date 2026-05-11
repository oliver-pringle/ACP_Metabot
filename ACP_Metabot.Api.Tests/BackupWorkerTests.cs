using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class BackupWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly Db _db;
    private readonly BackupWorker _worker;

    public BackupWorkerTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_backup_test_{stamp}.db");
        _backupDir = Path.Combine(Path.GetTempPath(), $"acp_metabot_backups_{stamp}");
        Directory.CreateDirectory(_backupDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
                ["Backup:Directory"] = _backupDir,
                ["Backup:BaseName"] = "acp_metabot_t",
                ["Backup:KeepDays"] = "3"
            }).Build();

        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _worker = new BackupWorker(config, _db, NullLogger<BackupWorker>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RunOnceAsync_WritesBackupFile_OfSimilarSize()
    {
        // Insert a couple of rows so the source DB has some content.
        await using (var conn = _db.OpenConnection())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO offerings
                (agent_address, agent_name, offering_name, description, price_usdc, price_type, chain, content_hash, first_seen_at, last_seen_at)
                VALUES ('0xabc', 'Agent', 'test_offer', 'desc', 0.1, 'one_shot', 'base', 'h1', $now, $now);";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        var backup = await _worker.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));

        // Open the backup file as a SQLite DB and verify the row is present.
        await using var verify = new SqliteConnection($"Data Source={backup}");
        await verify.OpenAsync();
        await using var check = verify.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM offerings WHERE agent_address='0xabc';";
        var count = Convert.ToInt64(await check.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RunOnceAsync_IsIdempotentSameDay_OverwritesAtomically()
    {
        var first = await _worker.RunOnceAsync(new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        var second = await _worker.RunOnceAsync(new DateTime(2026, 5, 11, 18, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        Assert.Equal(first, second); // Same date → same filename
        Assert.True(File.Exists(first));
        Assert.False(File.Exists(first + ".tmp")); // Tmp cleaned up via Move
    }

    [Fact]
    public async Task RunOnceAsync_PrunesOldBackups_RespectsKeepDays()
    {
        // Drop 5 backup files with descending date stamps; KeepDays=3 should retain only the 3 newest.
        for (var i = 0; i < 5; i++)
        {
            var day = new DateTime(2026, 5, 11, 4, 0, 0, DateTimeKind.Utc).AddDays(-i);
            await _worker.RunOnceAsync(day, CancellationToken.None);
        }
        var remaining = Directory.GetFiles(_backupDir, "acp_metabot_t.*.db");
        Assert.Equal(3, remaining.Length);
    }
}
