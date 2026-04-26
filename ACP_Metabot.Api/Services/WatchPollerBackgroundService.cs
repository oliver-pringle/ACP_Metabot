namespace ACP_Metabot.Api.Services;

public class WatchPollerBackgroundService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WatchPollerBackgroundService> _logger;

    public WatchPollerBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<WatchPollerBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[watch-poller] started; tick={Tick}", TickInterval);

        // First tick after a short delay so the API has finished bootstrapping.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<WatchService>();
                var fired = await svc.PollDueWatchesAsync(stoppingToken);
                if (fired > 0)
                    _logger.LogInformation("[watch-poller] cycle complete, alerts fired: {Fired}", fired);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[watch-poller] cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
