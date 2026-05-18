using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// v1.9 marketplacePulseSub — subscription tick scheduler.
//
// Mirrors v1.8 RiskWatchWorker exactly: poll every 5 minutes, pick due rows
// (status='active' AND next_run_at <= now), drive MarketplacePulseService
// .TickAsync per row. Default OFF — set MarketplacePulse:Worker:Enabled=true
// to activate. Same flip-on-when-ready convention as the other dormant
// workers so the offering can be hireable before the worker runs.
public sealed class MarketplacePulseWorker : BackgroundService
{
    private const int BatchSize = 25;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MarketplacePulseWorker> _log;
    private readonly bool _enabled;
    private readonly TimeSpan _pollInterval;

    public MarketplacePulseWorker(IServiceScopeFactory scopes,
        IConfiguration config, ILogger<MarketplacePulseWorker> log)
    {
        _scopes = scopes;
        _log = log;
        _enabled = config.GetValue<bool?>("MarketplacePulse:Worker:Enabled") ?? false;
        var seconds = config.GetValue<int?>("MarketplacePulse:Worker:PollSeconds") ?? 300;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(60, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _log.LogInformation(
                "[pulse-sub] disabled — set MarketplacePulse:Worker:Enabled=true to activate");
            return;
        }
        _log.LogInformation("[pulse-sub] enabled, poll every {Interval}", _pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "[pulse-sub] tick failed; continuing"); }
            try { await Task.Delay(_pollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Public so the operator-only POST /admin/pulse/tick-now endpoint can
    // invoke it for verification right after the env flip.
    public async Task<int> TickOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var subs    = scope.ServiceProvider.GetRequiredService<PulseSubscriptionRepository>();
        var service = scope.ServiceProvider.GetRequiredService<MarketplacePulseService>();

        var due = await subs.GetDueAsync(DateTime.UtcNow, BatchSize);
        if (due.Count == 0) return 0;
        _log.LogInformation("[pulse-sub] {Count} due subscriptions", due.Count);

        int delivered = 0;
        foreach (var sub in due)
        {
            try
            {
                var ok = await service.TickAsync(sub, ct);
                if (ok) delivered++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[pulse-sub] sub {Id} failed", sub.Id);
            }
        }
        return delivered;
    }
}
