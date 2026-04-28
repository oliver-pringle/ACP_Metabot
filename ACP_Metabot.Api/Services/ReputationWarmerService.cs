using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// Daily 02:30 UTC warmer. Computes reputation for the top-N agents by
// lifetime job count, hard-stops after a configured budget. Runs incremental
// rescans (each compute starts from last_scanned_block + 1).
public class ReputationWarmerService : BackgroundService
{
    private static readonly TimeOnly RunAt = new(2, 30);

    private readonly ReputationService            _reputation;
    private readonly AgentReputationCacheRepository _cacheRepo;
    private readonly MarketplaceIndexerService    _indexer;
    private readonly IConfiguration               _config;
    private readonly ILogger<ReputationWarmerService> _logger;

    public ReputationWarmerService(
        ReputationService reputation,
        AgentReputationCacheRepository cacheRepo,
        MarketplaceIndexerService indexer,
        IConfiguration config,
        ILogger<ReputationWarmerService> logger)
    {
        _reputation = reputation;
        _cacheRepo  = cacheRepo;
        _indexer    = indexer;
        _config     = config;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the indexer to have at least one successful fetch so the
        // top-N selection has data to work with.
        while (!stoppingToken.IsCancellationRequested && _indexer.LastFetchAt is null)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(now);
            var nextRunDate = TimeOnly.FromDateTime(now) >= RunAt ? today.AddDays(1) : today;
            var nextRun = nextRunDate.ToDateTime(RunAt, DateTimeKind.Utc);
            var delay = nextRun - now;
            if (delay.TotalSeconds > 0)
            {
                _logger.LogInformation("[warmer] sleeping until {next:O}", nextRun);
                try { await Task.Delay(delay, stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "[warmer] run failed"); }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var topN = _config.GetValue<int?>("Reputation:WarmerTopN") ?? 500;
        var budgetMin = _config.GetValue<int?>("Reputation:WarmerBudgetMinutes") ?? 60;
        var concurrency = _config.GetValue<int?>("Reputation:WarmerConcurrency") ?? 4;

        var agents = await _cacheRepo.ListWarmAgentsAsync(topN);
        if (agents.Count == 0)
        {
            _logger.LogInformation("[warmer] no agents to warm");
            return;
        }
        var deadline = DateTime.UtcNow.AddMinutes(budgetMin);
        int done = 0, failed = 0, skipped = 0;

        await Parallel.ForEachAsync(agents,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            async (agent, innerCt) =>
            {
                if (DateTime.UtcNow > deadline) { Interlocked.Increment(ref skipped); return; }
                try
                {
                    await _reputation.ComputeAsync(agent.AgentAddress, source: "warmer", innerCt);
                    var n = Interlocked.Increment(ref done);
                    if (n % 50 == 0) _logger.LogInformation("[warmer] {n}/{total} done", n, agents.Count);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "[warmer] {addr} failed", agent.AgentAddress);
                }
            });

        _logger.LogInformation("[warmer] pass complete: done={done} failed={failed} skipped={skipped}",
            done, failed, skipped);

        await _reputation.RebuildPercentilesFromCacheAsync(DateTime.UtcNow);
    }
}
