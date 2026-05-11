using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Services;

// Daily online SQLite backup using Microsoft.Data.Sqlite's BackupDatabase API
// (SQLite Online Backup, page-level locking, WAL-aware). Writes to a temp file
// then renames atomically so a mid-backup crash never produces a torn file.
// Rotation is filename-based: KeepDays counts unique YYYY-MM-DD stamps.
//
// The bot DB now lives in WAL mode (PRAGMA journal_mode = WAL, shipped
// 2026-05-11) so a naive `cp acp_metabot.db backup.db` would miss anything
// still in the -wal file. This worker is the corpus's only safe backup path.
public class BackupWorker : BackgroundService
{
    private readonly Db _db;
    private readonly ILogger<BackupWorker> _logger;
    private readonly bool _enabled;
    private readonly TimeOnly _runAt;
    private readonly int _keepDays;
    private readonly string _backupDir;
    private readonly string _baseName;

    public BackupWorker(IConfiguration config, Db db, ILogger<BackupWorker> logger)
    {
        _db = db;
        _logger = logger;
        _enabled = config.GetValue<bool?>("Backup:Enabled") ?? true;
        _runAt = new TimeOnly(config.GetValue<int?>("Backup:HourUtc") ?? 4, 0);
        _keepDays = Math.Max(1, config.GetValue<int?>("Backup:KeepDays") ?? 7);
        _backupDir = config["Backup:Directory"] ?? "/data/backups";
        _baseName = config["Backup:BaseName"] ?? "acp_metabot";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("[backup] disabled via config (Backup:Enabled=false)");
            return;
        }
        try { Directory.CreateDirectory(_backupDir); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[backup] cannot create backup directory {Dir} — disabling worker", _backupDir);
            return;
        }

        _logger.LogInformation(
            "[backup] enabled — daily at {Hour:D2}:00 UTC, keep last {Days} day(s), dir {Dir}",
            _runAt.Hour, _keepDays, _backupDir);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(now);
            var nextRunDate = TimeOnly.FromDateTime(now) >= _runAt ? today.AddDays(1) : today;
            var nextRun = nextRunDate.ToDateTime(_runAt, DateTimeKind.Utc);
            var delay = nextRun - now;
            if (delay.TotalSeconds > 0)
            {
                _logger.LogInformation("[backup] sleeping until {next:O}", nextRun);
                try { await Task.Delay(delay, stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
            try { await RunOnceAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "[backup] run failed"); }
        }
    }

    public async Task<string?> RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        var stamp = nowUtc.ToString("yyyy-MM-dd");
        var finalPath = Path.Combine(_backupDir, $"{_baseName}.{stamp}.db");
        var tmpPath = finalPath + ".tmp";

        if (File.Exists(tmpPath))
        {
            try { File.Delete(tmpPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "[backup] could not remove stale tmp {Tmp}", tmpPath); }
        }

        var started = DateTime.UtcNow;
        // Pooling=False on the destination is critical: with default pooling,
        // Microsoft.Data.Sqlite keeps the OS file handle open after dispose,
        // and Windows refuses the subsequent File.Move with "file in use".
        await using (var src = _db.OpenConnection())
        await using (var dst = new SqliteConnection($"Data Source={tmpPath};Pooling=False"))
        {
            await dst.OpenAsync(ct);
            src.BackupDatabase(dst);
        }

        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmpPath, finalPath);

        var size = new FileInfo(finalPath).Length;
        _logger.LogInformation(
            "[backup] wrote {Path} ({SizeKB:N0} KB) in {Ms:N0} ms",
            finalPath, size / 1024, (DateTime.UtcNow - started).TotalMilliseconds);

        PruneOldBackups();
        return finalPath;
    }

    private void PruneOldBackups()
    {
        try
        {
            var pattern = $"{_baseName}.*.db";
            var files = Directory.GetFiles(_backupDir, pattern)
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToList();
            foreach (var old in files.Skip(_keepDays))
            {
                try { File.Delete(old); _logger.LogInformation("[backup] pruned {Path}", old); }
                catch (Exception ex) { _logger.LogWarning(ex, "[backup] failed to prune {Path}", old); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[backup] prune failed in {Dir}", _backupDir);
        }
    }
}
