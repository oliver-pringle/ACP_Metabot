using ACP_Metabot.Api.Data;
using Microsoft.Extensions.DependencyInjection;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// Background scanner that keeps the security_verdicts cache fresh by calling
/// SecurityBot's free internal scan endpoint over acp-shared, and appends a full
/// row per scan to security_scan_history. Default OFF — set
/// SECURITY_SCAN_ENABLED=true once THESECURITYBOT_API_KEY is wired (same
/// flip-on-when-ready convention as MarketplacePulseWorker).
///
/// Each tick selects up to SECURITY_SCAN_BATCH stale agents (never-scanned or
/// past their per-status TTL), scans each with SECURITY_SCAN_DELAY_SECONDS
/// between calls, upserts the latest-verdict cache row, and appends one immutable
/// history row. Deliberately gentle on SecurityBot and the external targets it
/// probes. Single-replica assumption: the portfolio runs one Metabot instance,
/// so the serial batch needs no distributed lock.
/// </summary>
public sealed class SecurityScanWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SecurityScanWorker> _log;

    private readonly bool _enabled;
    private readonly int _batch;
    private readonly TimeSpan _delay;
    private readonly TimeSpan _tick;
    private readonly int _activeWindowDays;
    private readonly TimeSpan _scannedTtl;
    private readonly TimeSpan _notAuditableTtl;
    private readonly TimeSpan _errorTtl;

    public SecurityScanWorker(IServiceScopeFactory scopes,
        IConfiguration config, ILogger<SecurityScanWorker> log)
    {
        _scopes = scopes;
        _log = log;

        _enabled = config.GetValue<bool?>("SECURITY_SCAN_ENABLED") ?? false;
        _batch   = Math.Clamp(config.GetValue<int?>("SECURITY_SCAN_BATCH") ?? 10, 1, 100);
        _delay   = TimeSpan.FromSeconds(Math.Max(0, config.GetValue<int?>("SECURITY_SCAN_DELAY_SECONDS") ?? 5));
        _tick    = TimeSpan.FromSeconds(Math.Max(15, config.GetValue<int?>("SECURITY_SCAN_TICK_SECONDS") ?? 60));
        _activeWindowDays = Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_ACTIVE_WINDOW_DAYS") ?? 30);
        _scannedTtl      = TimeSpan.FromDays(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_TTL_DAYS") ?? 7));
        _notAuditableTtl = TimeSpan.FromDays(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_NOTAUDITABLE_TTL_DAYS") ?? 30));
        _errorTtl        = TimeSpan.FromHours(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_ERROR_TTL_HOURS") ?? 6));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _log.LogInformation("[security-scan] disabled — set SECURITY_SCAN_ENABLED=true to activate");
            return;
        }
        _log.LogInformation("[security-scan] enabled; batch={Batch}, delay={Delay}s, tick={Tick}",
            _batch, _delay.TotalSeconds, _tick);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "[security-scan] tick failed; continuing"); }
            try { await Task.Delay(_tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Run one batch. Returns the number of agents scanned. Public for tests.</summary>
    public async Task<int> TickOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<SecurityVerdictRepository>();
        var historyRepo = scope.ServiceProvider.GetRequiredService<SecurityScanHistoryRepository>();
        var scanService = scope.ServiceProvider.GetRequiredService<SecurityScanService>();

        var stale = await repo.GetStaleAgentsAsync(
            DateTime.UtcNow, _activeWindowDays,
            _scannedTtl, _notAuditableTtl, _errorTtl, _batch, ct);
        if (stale.Count == 0) return 0;

        _log.LogInformation("[security-scan] {Count} stale agents this tick", stale.Count);
        int scanned = 0;
        for (int i = 0; i < stale.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Single write-path: scan -> upsert latest-verdict cache -> append a
            // full history row (cache first, then append). Identical to the
            // on-demand POST /admin/securityScan path — see SecurityScanService.
            await scanService.ScanAndPersistAsync(stale[i], repo, historyRepo, ct);
            scanned++;
            if (_delay > TimeSpan.Zero && i < stale.Count - 1)
                await Task.Delay(_delay, ct);
        }
        return scanned;
    }
}
