using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// Daily 02:00 UTC snapshot of (agent_address → total_jobs) sourced from the
// already-indexed `offerings` table. Feeds the reputation warmer (which runs
// 30 min later) and the 30-day-delta math in the score formula.
public class LifetimeSnapshotService : BackgroundService
{
    private static readonly TimeOnly RunAt = new(2, 0);
    private const int RetainDays = 35;

    private readonly OfferingRepository _offeringRepo;
    private readonly LifetimeSnapshotRepository _snapshotRepo;
    private readonly ILogger<LifetimeSnapshotService> _logger;

    public LifetimeSnapshotService(
        OfferingRepository offeringRepo,
        LifetimeSnapshotRepository snapshotRepo,
        ILogger<LifetimeSnapshotService> logger)
    {
        _offeringRepo = offeringRepo;
        _snapshotRepo = snapshotRepo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once at startup if today's snapshot is missing, then on the daily schedule.
        try { await RunIfDueAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogError(ex, "[snapshot] startup run failed"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(now);
            var nextRunDate = TimeOnly.FromDateTime(now) >= RunAt ? today.AddDays(1) : today;
            var nextRun = nextRunDate.ToDateTime(RunAt, DateTimeKind.Utc);
            var delay = nextRun - now;
            if (delay.TotalSeconds > 0)
            {
                _logger.LogInformation("[snapshot] sleeping until {next:O}", nextRun);
                try { await Task.Delay(delay, stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
            try { await RunOnceAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "[snapshot] run failed"); }
        }
    }

    private async Task RunIfDueAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        // Cheap probe: does any agent have a snapshot for today?
        var probe = await _offeringRepo.PickFirstAgentAsync(); // small helper, see below
        if (probe is null) return;
        var existing = await _snapshotRepo.GetAsync(probe, today);
        if (existing is null) await RunOnceAsync(DateTime.UtcNow, ct);
    }

    public async Task RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        var totals = await _offeringRepo.SumJobCountsByAgentAsync();
        if (totals.Count == 0)
        {
            _logger.LogInformation("[snapshot] no agents to snapshot yet");
            return;
        }
        await _snapshotRepo.UpsertManyAsync(totals, nowUtc.Date);
        await _snapshotRepo.PruneOlderThanAsync(nowUtc.Date.AddDays(-RetainDays));
        _logger.LogInformation("[snapshot] wrote {count} agent rows for {date:yyyy-MM-dd}",
            totals.Count, nowUtc.Date);
    }
}
