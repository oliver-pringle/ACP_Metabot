using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// v1.6 #1 v0.2 — Reputation feed sync.
//
// Hourly poll worker that keeps Metabot's reputation_feeds.{latest_score,
// last_pushed_round, last_pushed_at} columns in sync with ChainlinkBot's
// on-chain pushes. ChainlinkBot's ScoringPushWorker is the actual on-chain
// writer (calls registry.batchUpdate); this worker just reflects that
// activity back into Metabot so the /feeds/published admin endpoint shows
// "yes, this feed is being actively updated" instead of stale deploy-time
// values.
//
// Polls each published feed individually. At default cadence (60 min) and
// default top-N (50), that's 50 HTTP calls per hour against the acp-shared
// bridge — well within reason. SemaphoreSlim(BatchSize) caps concurrent
// in-flight calls so the burst doesn't fan out to 50 at once.
//
// 404 from ChainlinkBot means "never pushed yet" — leave the columns null
// and continue silently. Network / 5xx errors are logged but don't crash
// the worker; next tick retries.
//
// Disabled by default. Enable via Feeds:Sync:Enabled=true.
public class ReputationFeedSyncWorker : BackgroundService
{
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;
    private readonly int _maxAgents;
    private readonly string? _ownAgentAddress;
    private readonly int _ownScoreThreshold;

    private readonly ReputationFeedRepository _feeds;
    private readonly ITheChainlinkBotClient _chainlinkBot;
    private readonly ILogger<ReputationFeedSyncWorker> _log;

    public ReputationFeedSyncWorker(
        IConfiguration config,
        ReputationFeedRepository feeds,
        ITheChainlinkBotClient chainlinkBot,
        ILogger<ReputationFeedSyncWorker> log)
    {
        _enabled    = config.GetValue<bool?>("Feeds:Sync:Enabled") ?? false;
        var minutes = Math.Max(5, config.GetValue<int?>("Feeds:Sync:IntervalMinutes") ?? 60);
        _interval   = TimeSpan.FromMinutes(minutes);
        _batchSize  = Math.Max(1, config.GetValue<int?>("Feeds:Sync:BatchSize") ?? 5);
        _maxAgents  = Math.Max(1, config.GetValue<int?>("Feeds:Sync:MaxAgents") ?? 500);
        // M8 self-watch: if Metabot's own reputation drops below this, the
        // sync worker logs a high-visibility warning on every tick. Set
        // Feeds:Sync:OwnAgentAddress to the Privy smart-account address of
        // TheMetaBot agent on app.virtuals.io (the address that ACP hires
        // arrive at). Unset → self-watch disabled.
        var ownAddr = config["Feeds:Sync:OwnAgentAddress"];
        _ownAgentAddress = string.IsNullOrWhiteSpace(ownAddr) ? null : ownAddr.ToLowerInvariant();
        _ownScoreThreshold = config.GetValue<int?>("Feeds:Sync:OwnScoreThreshold") ?? 30;
        _feeds      = feeds;
        _chainlinkBot = chainlinkBot;
        _log        = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _log.LogInformation("[feeds-sync] disabled via config (Feeds:Sync:Enabled=false)");
            return;
        }
        _log.LogInformation(
            "[feeds-sync] enabled — every {Interval}, batch {Batch}, max {Max} feeds per tick",
            _interval, _batchSize, _maxAgents);

        // Initial delay so we don't compete with the publisher worker on the
        // first boot tick.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "[feeds-sync] tick failed"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    public async Task<(int Synced, int NotPushed, int Failed)> RunOnceAsync(CancellationToken ct)
    {
        var deployed = await _feeds.ListDeployedAsync(_maxAgents);
        if (deployed.Count == 0)
        {
            _log.LogInformation("[feeds-sync] no published feeds — nothing to sync");
            return (0, 0, 0);
        }

        int synced = 0, notPushed = 0, failed = 0;
        var sem = new SemaphoreSlim(_batchSize);
        var tasks = deployed.Select(async pair =>
        {
            var (agent, _) = pair;
            await sem.WaitAsync(ct);
            try
            {
                try
                {
                    var resp = await _chainlinkBot.GetFeedScoreAsync(agent, ct);
                    if (resp is null)
                    {
                        Interlocked.Increment(ref notPushed);
                        return;
                    }
                    await _feeds.UpdateSyncedScoreAsync(
                        agent, resp.PublishedValue, resp.RoundId, resp.PushedAt);
                    Interlocked.Increment(ref synced);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _log.LogWarning(ex, "[feeds-sync] failed for {Agent}", agent);
                }
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        _log.LogInformation(
            "[feeds-sync] tick complete: synced {Synced}, not-yet-pushed {NotPushed}, failed {Failed}",
            synced, notPushed, failed);

        await CheckOwnReputationAsync(ct);

        return (synced, notPushed, failed);
    }

    // M8 self-watch. Pulls Metabot's own latest pushed score from
    // ChainlinkBot and warns loudly if it has dropped below the configured
    // threshold. The warning is structured logging so an operator's log
    // alert system (Datadog / Loki / Grafana) can wire on the
    // "metabot-own-score-low" event id without parsing free text.
    private async Task CheckOwnReputationAsync(CancellationToken ct)
    {
        if (_ownAgentAddress is null) return;
        try
        {
            var resp = await _chainlinkBot.GetFeedScoreAsync(_ownAgentAddress, ct);
            if (resp is null)
            {
                _log.LogInformation(
                    "[feeds-sync] self-watch: own agent {Addr} has no on-chain push yet",
                    _ownAgentAddress);
                return;
            }
            if (resp.PublishedValue < _ownScoreThreshold)
            {
                _log.LogWarning(
                    new EventId(8001, "metabot-own-score-low"),
                    "[feeds-sync] SELF-WATCH ALERT: own reputation {Score} dropped below threshold {Threshold} (last pushed round {Round} at {PushedAt:o})",
                    resp.PublishedValue, _ownScoreThreshold, resp.RoundId, resp.PushedAt);
            }
            else
            {
                _log.LogInformation(
                    "[feeds-sync] self-watch: own reputation {Score} >= threshold {Threshold} (round {Round})",
                    resp.PublishedValue, _ownScoreThreshold, resp.RoundId);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[feeds-sync] self-watch probe failed for {Addr}", _ownAgentAddress);
        }
    }
}
