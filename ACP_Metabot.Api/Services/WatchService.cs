using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

public record RegisterWatchRequest(
    long JobId,
    string BuyerAddress,
    string Query,
    string WebhookUrl,
    int? DurationDays,
    int? IntervalHours,
    double? MinScore,
    double? PriceMaxUsdc,
    int? MaxAlerts);

public record RegisterWatchResult(
    string WatchId,
    DateTime ExpiresAt,
    int IntervalHours,
    int MaxAlerts,
    IReadOnlyList<OfferingMatch> InitialMatches);

public class WatchService
{
    private const int InitialMatchLimit = 20;
    private const int FailureWatchThreshold = 3;
    private const int FailureCancelThreshold = 5;

    private readonly WatchRepository _repo;
    private readonly SearchService _search;
    private readonly WebhookDeliveryService _webhook;
    private readonly ILogger<WatchService> _logger;

    public WatchService(
        WatchRepository repo,
        SearchService search,
        WebhookDeliveryService webhook,
        ILogger<WatchService> logger)
    {
        _repo = repo;
        _search = search;
        _webhook = webhook;
        _logger = logger;
    }

    public async Task<RegisterWatchResult> RegisterWatchAsync(
        RegisterWatchRequest req, CancellationToken ct)
    {
        var durationDays = Math.Clamp(req.DurationDays ?? 7, 1, 30);
        var intervalHours = Math.Clamp(req.IntervalHours ?? 6, 1, 24);
        var maxAlerts = Math.Clamp(req.MaxAlerts ?? 20, 1, 100);
        var minScore = req.MinScore ?? 0.0;
        var priceMax = req.PriceMaxUsdc ?? double.PositiveInfinity;

        var initial = await _search.SearchAsync(req.Query, InitialMatchLimit, minScore, priceMax, staleAfterDays: null, rerank: false, categoryFilter: null, ct);

        var now = DateTime.UtcNow;
        var watch = new Watch(
            Id: Guid.NewGuid().ToString("D"),
            JobId: req.JobId,
            BuyerAddress: req.BuyerAddress,
            Query: req.Query,
            WebhookUrl: req.WebhookUrl,
            DurationDays: durationDays,
            IntervalHours: intervalHours,
            MinScore: req.MinScore,
            PriceMaxUsdc: req.PriceMaxUsdc,
            MaxAlerts: maxAlerts,
            AlertsDelivered: 0,
            WebhookConsecutiveFailures: 0,
            Status: "active",
            CreatedAt: now,
            ExpiresAt: now.AddDays(durationDays),
            LastPolledAt: null);

        await _repo.CreateAsync(watch);

        // Seed watch_seen with initial matches so the first poll cycle does not
        // re-deliver them as "new".
        if (initial.Count > 0)
        {
            await _repo.RecordSeenAsync(watch.Id, initial.Select(m => m.OfferingId), now);
        }

        _logger.LogInformation(
            "[watch] registered id={Id} jobId={JobId} expires={Expires} initial={InitialCount}",
            watch.Id, watch.JobId, watch.ExpiresAt, initial.Count);

        return new RegisterWatchResult(watch.Id, watch.ExpiresAt, intervalHours, maxAlerts, initial);
    }

    /// <summary>
    /// Operator-only: clears watch_seen for the given watch and immediately runs a
    /// single poll. Lets us deterministically verify webhook delivery without
    /// waiting for a real new-offering to be indexed. Returns null if the watch
    /// does not exist; otherwise the same bool as PollOneAsync (true if the
    /// webhook delivered at least one match).
    /// </summary>
    public async Task<bool?> TestFireAsync(string watchId, CancellationToken ct)
    {
        var w = await _repo.GetByIdAsync(watchId);
        if (w is null) return null;
        await _repo.ClearSeenAsync(watchId);
        _logger.LogInformation("[watch] test-fire triggered for id={Id}", watchId);
        return await PollOneAsync(w, ct);
    }

    public async Task<int> PollDueWatchesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var expired = await _repo.MarkExpiredAsync(now);
        if (expired > 0) _logger.LogInformation("[watch] marked {Count} watches as expired", expired);

        var due = await _repo.GetDueAsync(now);
        if (due.Count == 0) return 0;

        _logger.LogInformation("[watch] polling {Count} due watches", due.Count);
        var alertsFired = 0;

        foreach (var w in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await PollOneAsync(w, ct)) alertsFired++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[watch] poll error for id={Id}", w.Id);
                // Keep going; one bad watch shouldn't kill the cycle.
                try { await _repo.MarkPolledAsync(w.Id, DateTime.UtcNow); } catch { /* swallow */ }
            }
        }

        return alertsFired;
    }

    private async Task<bool> PollOneAsync(Watch w, CancellationToken ct)
    {
        var minScore = w.MinScore ?? 0.0;
        var priceMax = w.PriceMaxUsdc ?? double.PositiveInfinity;
        var results = await _search.SearchAsync(w.Query, InitialMatchLimit, minScore, priceMax, staleAfterDays: null, rerank: false, categoryFilter: null, ct);

        var seen = await _repo.GetSeenIdsAsync(w.Id);
        var newMatches = results.Where(r => !seen.Contains(r.OfferingId)).ToList();

        var nowPolled = DateTime.UtcNow;

        if (newMatches.Count == 0)
        {
            await _repo.MarkPolledAsync(w.Id, nowPolled);
            await _repo.ResetFailureCountAsync(w.Id);
            return false;
        }

        var alertNumber = w.AlertsDelivered + 1;
        var remaining = Math.Max(0, w.MaxAlerts - alertNumber);
        var payload = new
        {
            watchId = w.Id,
            alertNumber,
            remainingAlerts = remaining,
            query = w.Query,
            matches = newMatches,
            polledAt = nowPolled.ToString("O"),
        };

        var ok = await _webhook.DeliverAsync(w.WebhookUrl, w.Id, alertNumber, payload, ct);
        await _repo.MarkPolledAsync(w.Id, nowPolled);

        if (ok)
        {
            await _repo.RecordSeenAsync(w.Id, newMatches.Select(m => m.OfferingId), nowPolled);
            var delivered = await _repo.IncrementAlertsAsync(w.Id);
            await _repo.ResetFailureCountAsync(w.Id);
            if (w.Status == "webhook_failing")
                await _repo.MarkStatusAsync(w.Id, "active");
            if (delivered >= w.MaxAlerts)
            {
                await _repo.MarkStatusAsync(w.Id, "exhausted");
                _logger.LogInformation("[watch] id={Id} exhausted at {Delivered}/{Max} alerts",
                    w.Id, delivered, w.MaxAlerts);
            }
            return true;
        }

        var failures = await _repo.IncrementFailureCountAsync(w.Id);
        if (failures >= FailureCancelThreshold)
        {
            await _repo.MarkStatusAsync(w.Id, "cancelled");
            _logger.LogWarning("[watch] id={Id} cancelled after {Failures} consecutive failures",
                w.Id, failures);
        }
        else if (failures >= FailureWatchThreshold && w.Status == "active")
        {
            await _repo.MarkStatusAsync(w.Id, "webhook_failing");
            _logger.LogWarning("[watch] id={Id} marked webhook_failing at {Failures} failures",
                w.Id, failures);
        }
        return false;
    }
}
