using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

// BackgroundService that owns two responsibilities:
//
//   1. DRAIN — consume RequestMetricEvent rows from MetricsChannel and
//      INSERT them into request_log in batches. Pattern: block on the
//      first event, then collect up to 100 more or 250ms, then flush.
//
//   2. ROLLOVER — every hour at minute 5, aggregate the hour just past
//      into request_rollup_hourly. At 03:00 UTC each day, additionally
//      aggregate yesterday into request_rollup_daily and prune raw rows
//      older than 14 days + hourly rollups older than 90 days.
//
// The xx:05 / 03:00 schedule is offset from LifetimeSnapshotService's
// 02:00 daily run to avoid SQLite write contention.
public class MetricsWriterService : BackgroundService
{
    private const int BatchThreshold = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RolloverMinuteOffset = TimeSpan.FromMinutes(5);
    private const int DailyRolloverHourUtc = 3;
    private const int RawRetentionDays = 14;
    private const int HourlyRetentionDays = 90;

    private readonly MetricsChannel _channel;
    private readonly RequestMetricsRepository _repo;
    private readonly ILogger<MetricsWriterService> _logger;

    public MetricsWriterService(MetricsChannel channel, RequestMetricsRepository repo,
        ILogger<MetricsWriterService> logger)
    {
        _channel = channel;
        _repo = repo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var drain    = DrainLoopAsync(stoppingToken);
        var rollover = RolloverLoopAsync(stoppingToken);
        await Task.WhenAll(drain, rollover);
    }

    private async Task DrainLoopAsync(CancellationToken token)
    {
        var buffer = new List<RequestMetricEvent>(BatchThreshold * 2);

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Block until at least one event is available.
                if (!await _channel.Reader.WaitToReadAsync(token)) return;

                // Drain everything currently queued, up to threshold.
                while (buffer.Count < BatchThreshold && _channel.Reader.TryRead(out var evt))
                    buffer.Add(evt);

                // If we didn't fill the batch, wait briefly for more arrivals.
                if (buffer.Count < BatchThreshold)
                {
                    try { await Task.Delay(FlushInterval, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Service shutting down — still flush what we have before exiting.
                    }
                    while (buffer.Count < BatchThreshold && _channel.Reader.TryRead(out var evt))
                        buffer.Add(evt);
                }

                if (buffer.Count > 0)
                {
                    try
                    {
                        await _repo.InsertManyAsync(buffer, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[metrics] insert failed for {n} events", buffer.Count);
                    }
                    buffer.Clear();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[metrics] drain loop iteration failed");
                try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
                catch { return; }
            }
        }
    }

    private async Task RolloverLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextTick = NextHourTickUtc(now);
                var delay = nextTick - now;
                if (delay > TimeSpan.Zero)
                {
                    _logger.LogInformation("[metrics] next rollover at {next:O}", nextTick);
                    try { await Task.Delay(delay, token); }
                    catch (TaskCanceledException) { return; }
                }

                var firedAt = DateTime.UtcNow;
                var hourStart = new DateTime(firedAt.Year, firedAt.Month, firedAt.Day,
                    firedAt.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);

                try
                {
                    await _repo.RolloverHourlyAsync(hourStart, token);
                    _logger.LogInformation("[metrics] hourly rollover wrote {hour:yyyy-MM-dd HH}", hourStart);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[metrics] hourly rollover failed for {hour:O}", hourStart);
                }

                if (firedAt.Hour == DailyRolloverHourUtc)
                {
                    var yesterday = DateOnly.FromDateTime(firedAt).AddDays(-1);
                    try
                    {
                        await _repo.RolloverDailyAsync(yesterday, token);
                        _logger.LogInformation("[metrics] daily rollover wrote {day}", yesterday);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[metrics] daily rollover failed for {day}", yesterday);
                    }

                    try
                    {
                        var rawCut = firedAt.AddDays(-RawRetentionDays);
                        var pruned = await _repo.PruneRawOlderThanAsync(rawCut, token);
                        if (pruned > 0)
                            _logger.LogInformation("[metrics] pruned {n} raw rows older than {cut:O}", pruned, rawCut);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[metrics] raw prune failed");
                    }

                    try
                    {
                        var hourlyCut = firedAt.AddDays(-HourlyRetentionDays);
                        var pruned = await _repo.PruneHourlyRollupOlderThanAsync(hourlyCut, token);
                        if (pruned > 0)
                            _logger.LogInformation("[metrics] pruned {n} hourly rollup rows older than {cut:O}", pruned, hourlyCut);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[metrics] hourly rollup prune failed");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[metrics] rollover loop iteration failed");
                try { await Task.Delay(TimeSpan.FromMinutes(5), token); }
                catch { return; }
            }
        }
    }

    private static DateTime NextHourTickUtc(DateTime now)
    {
        var topOfHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var tick = topOfHour + RolloverMinuteOffset;
        if (tick <= now) tick = tick.AddHours(1);
        return tick;
    }
}
