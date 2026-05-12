namespace ACP_Metabot.Api.Services;

// BackgroundService that periodically refreshes Metabot's Arena participation
// cache by polling ArenaBot's Resources. Default cadence 15 min — Arena's own
// leaderboard updates every 5 min, but a 15-min Metabot ingest cadence is
// fine because the Resources are summary snapshots, not full event streams.
//
// Disabled if Arena:BaseUrl is unset (TheArenaBotClient.Enabled = false) or
// if Arena:WorkerEnabled = "false".
public class ArenaSourceWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ArenaSourceWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    public ArenaSourceWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<ArenaSourceWorker> logger)
    {
        _scopes = scopes; _logger = logger;
        var sec = int.TryParse(config["Arena:Worker:IntervalSeconds"], out var p) ? p : 900;
        _interval = TimeSpan.FromSeconds(Math.Max(60, sec));
        _enabled = !string.Equals(config["Arena:Worker:Enabled"], "false", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(config["Arena:BaseUrl"]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("ArenaSourceWorker disabled — set Arena:BaseUrl + Arena:Worker:Enabled=true to activate");
            return;
        }
        _logger.LogInformation("ArenaSourceWorker started; interval={Interval}", _interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var svc = scope.ServiceProvider.GetRequiredService<ArenaSourceService>();
                await svc.RefreshAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArenaSourceWorker tick failed; continuing");
            }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
