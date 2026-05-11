using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// v1.6 #1 v0.1 — Reputation as Chainlink AggregatorV3 feed.
//
// Daily cron that picks top-N agents by cached reputation score, asks
// ChainlinkBot's POST /feed-address to deploy (or fetch) an on-chain
// ReputationAggregator per agent, and records the address in reputation_feeds.
//
// Why this is leverage: today `agentReputation` is a Metabot HTTP call that
// requires trusting Metabot's API surface. As an AggregatorV3 feed, any DeFi
// protocol can read the same scores on-chain with the same shape they use
// for ETH/USD — composability instead of custodial trust.
//
// v0.1 scope: deploy + persist. The score-PUSH flow (calling the aggregator's
// submit(roundId, value) on every cache refresh, plus the ChainlinkBot
// Automation integration that drives subsequent rounds) is v0.2.
//
// Dependencies: ChainlinkBot must be reachable on the acp-shared docker
// bridge at the URL in TheChainlinkBot:BaseUrl. The worker SILENTLY does
// nothing if ChainlinkBot:BaseUrl points at the default placeholder and no
// override is set in env — useful for local dev where ChainlinkBot isn't
// running.
public class ReputationFeedPublisherWorker : BackgroundService
{
    private readonly TimeOnly _runAt;
    private readonly int _topN;
    private readonly int _minScore;
    private readonly int _batchSize;
    private readonly bool _enabled;

    private readonly AgentReputationCacheRepository _cache;
    private readonly ReputationFeedRepository _feeds;
    private readonly ITheChainlinkBotClient _chainlinkBot;
    private readonly ILogger<ReputationFeedPublisherWorker> _log;

    public ReputationFeedPublisherWorker(
        IConfiguration config,
        AgentReputationCacheRepository cache,
        ReputationFeedRepository feeds,
        ITheChainlinkBotClient chainlinkBot,
        ILogger<ReputationFeedPublisherWorker> log)
    {
        _enabled  = config.GetValue<bool?>("Feeds:Enabled") ?? false;
        _runAt    = new TimeOnly(config.GetValue<int?>("Feeds:HourUtc") ?? 5, 0);
        _topN     = Math.Max(1, config.GetValue<int?>("Feeds:TopN") ?? 50);
        _minScore = Math.Max(0, config.GetValue<int?>("Feeds:MinScore") ?? 50);
        _batchSize = Math.Max(1, config.GetValue<int?>("Feeds:BatchSize") ?? 5);
        _cache    = cache;
        _feeds    = feeds;
        _chainlinkBot = chainlinkBot;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _log.LogInformation("[feeds] disabled via config (Feeds:Enabled=false)");
            return;
        }
        _log.LogInformation(
            "[feeds] enabled — daily at {Hour:D2}:00 UTC, top {TopN} agents, min score {Min}, batch {Batch}",
            _runAt.Hour, _topN, _minScore, _batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(now);
            var nextRunDate = TimeOnly.FromDateTime(now) >= _runAt ? today.AddDays(1) : today;
            var nextRun = nextRunDate.ToDateTime(_runAt, DateTimeKind.Utc);
            var delay = nextRun - now;
            if (delay.TotalSeconds > 0)
            {
                _log.LogInformation("[feeds] sleeping until {next:O}", nextRun);
                try { await Task.Delay(delay, stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
            try { await RunOnceAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "[feeds] run failed"); }
        }
    }

    public async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        var candidates = await _cache.ListTopByScoreAsync(_topN, _minScore, nowUtc);
        if (candidates.Count == 0)
        {
            _log.LogInformation("[feeds] no candidates with score >= {Min} in fresh cache", _minScore);
            return 0;
        }
        var addresses = candidates.Select(c => c.AgentAddress.ToLowerInvariant()).ToList();
        var pending = await _feeds.FilterAlreadyPublishedAsync(addresses);
        if (pending.Count == 0)
        {
            _log.LogInformation("[feeds] all {N} top agents already published", candidates.Count);
            return 0;
        }
        _log.LogInformation(
            "[feeds] {N} of {Top} top agents not yet published — requesting deployment",
            pending.Count, candidates.Count);

        var byAddr = candidates.ToDictionary(c => c.AgentAddress.ToLowerInvariant());

        int published = 0;
        int failed = 0;
        // Throttle: at most _batchSize concurrent in-flight ChainlinkBot calls
        // so we don't hammer the cross-bot endpoint with N parallel deploys.
        var sem = new SemaphoreSlim(_batchSize);
        var firstSeen = DateTime.UtcNow;
        var tasks = pending.Select(async addr =>
        {
            await sem.WaitAsync(ct);
            try
            {
                try
                {
                    var resp = await _chainlinkBot.RequestFeedAddressAsync(addr, ct);
                    if (string.IsNullOrEmpty(resp.AggregatorAddress) ||
                        resp.AggregatorAddress == "0x0000000000000000000000000000000000000000")
                    {
                        await _feeds.RecordErrorAsync(addr, "ChainlinkBot returned zero aggregator address");
                        Interlocked.Increment(ref failed);
                        return;
                    }
                    var score = byAddr.TryGetValue(addr, out var row) ? (double)row.AgentScore : (double?)null;
                    await _feeds.UpsertDeployedAsync(
                        agentAddress: addr,
                        aggregatorAddress: resp.AggregatorAddress,
                        methodologyHash: resp.MethodologyHash,
                        decimals: resp.Decimals,
                        latestScore: resp.LatestScore != 0 ? resp.LatestScore : score,
                        deployedAt: resp.DeployedAt,
                        firstSeenAt: firstSeen);
                    Interlocked.Increment(ref published);
                    _log.LogInformation(
                        "[feeds] published {Agent} -> {Aggregator}", addr, resp.AggregatorAddress);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _log.LogWarning(ex, "[feeds] failed to publish {Agent}", addr);
                    try { await _feeds.RecordErrorAsync(addr, ex.Message); } catch { /* best effort */ }
                }
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        _log.LogInformation(
            "[feeds] run complete — published {Pub}, failed {Fail}", published, failed);
        return published;
    }
}
