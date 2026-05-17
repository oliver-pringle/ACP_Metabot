using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// v1.8 Portfolio Risk Bot — subscription tick scheduler for daily_risk_watch.
//
// Polling cadence: every 5 minutes the worker scans risk_subscriptions for
// rows with status='active' AND next_run_at <= now. For each due row it
// computes a fresh risk_snapshot, diffs the score against last_score, and
// POSTs an HMAC-signed payload to the buyer's webhookUrl. The next_run_at
// is then advanced by 24h.
//
// Default OFF (`RiskWatch:Worker:Enabled=true` flag) — same flip-on-when-
// ready convention as ReputationFeedPublisherWorker so the offering can be
// callable / hireable before the worker is activated.
public sealed class RiskWatchWorker : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(5);
    private const int BatchSize = 25;
    private const int BodyCapBytes = 1_048_576; // 1 MB

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RiskWatchWorker> _log;
    private readonly bool _enabled;
    private readonly TimeSpan _pollInterval;

    public RiskWatchWorker(IServiceScopeFactory scopes,
        IConfiguration config, ILogger<RiskWatchWorker> log)
    {
        _scopes = scopes;
        _log = log;
        _enabled = config.GetValue<bool?>("RiskWatch:Worker:Enabled") ?? false;
        var seconds = config.GetValue<int?>("RiskWatch:Worker:PollSeconds") ?? 300;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(60, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _log.LogInformation("[risk-watch] disabled — set RiskWatch:Worker:Enabled=true to activate");
            return;
        }
        _log.LogInformation("[risk-watch] enabled, poll every {Interval}", _pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "[risk-watch] tick failed; continuing"); }
            try { await Task.Delay(_pollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var subs = scope.ServiceProvider.GetRequiredService<RiskSubscriptionRepository>();
        var synth = scope.ServiceProvider.GetRequiredService<RiskSynthesisService>();
        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        var due = await subs.GetDueAsync(DateTime.UtcNow, BatchSize);
        if (due.Count == 0) return;
        _log.LogInformation("[risk-watch] {Count} due subscriptions", due.Count);

        foreach (var sub in due)
        {
            try { await ProcessOneAsync(sub, subs, synth, http, ct); }
            catch (Exception ex) { _log.LogError(ex, "[risk-watch] sub {Id} failed", sub.Id); }
        }
    }

    private async Task ProcessOneAsync(
        RiskSubscription sub, RiskSubscriptionRepository subs,
        RiskSynthesisService synth, HttpClient http, CancellationToken ct)
    {
        var snap = await synth.ComputeAsync(sub.WalletAddress, sub.Chain, ct);
        var snapJson = JsonSerializer.Serialize(RiskOrchestrationService.SerialiseSnapshot(snap));

        // Diff against the prior tick when present. v1 surfaces a numeric
        // delta + alert flag; v1.1 may include per-component deltas.
        int? prevScore = sub.LastScore;
        int? delta = prevScore is null ? null : snap.RiskScore - prevScore.Value;
        bool alert = delta is not null && delta.Value <= -10;

        var tickNumber = sub.TicksDelivered + 1;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new
        {
            subscriptionId = sub.Id,
            tick = tickNumber,
            cadence = sub.Cadence,
            snapshot = JsonSerializer.Deserialize<JsonElement>(snapJson),
            diff = new
            {
                previousScore = prevScore,
                currentScore = snap.RiskScore,
                delta,
                alert
            }
        });

        if (Encoding.UTF8.GetByteCount(payload) > BodyCapBytes)
        {
            _log.LogWarning("[risk-watch] payload too large for sub {Id}; skipping", sub.Id);
            await subs.RecordTickResultAsync(sub.Id, ok: false,
                ranAtUtc: DateTime.UtcNow,
                nextRunAtUtc: DateTime.UtcNow.AddDays(1),
                newScore: snap.RiskScore,
                snapshotJson: snapJson,
                windowComplete: false);
            return;
        }

        var sig = ComputeSignature(sub.WebhookSecret, tickNumber, ts, payload);

        bool ok = false;
        string? error = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, sub.WebhookUrl);
            req.Content = new StringContent(payload, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Add("X-Subscription-Id", sub.Id);
            req.Headers.Add("X-Subscription-Tick", tickNumber.ToString());
            req.Headers.Add("X-Subscription-Timestamp", ts.ToString());
            req.Headers.Add("X-Subscription-Signature", sig);
            using var resp = await http.SendAsync(req, ct);
            ok = (int)resp.StatusCode is >= 200 and < 300;
            if (!ok) error = $"HTTP {(int)resp.StatusCode}";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { error = "timeout"; }
        catch (HttpRequestException ex) { error = $"http: {ex.Message}"; }

        var nextRunAt = DateTime.UtcNow.AddDays(1);
        var windowComplete = tickNumber >= sub.TicksPurchased;
        await subs.RecordTickResultAsync(sub.Id, ok,
            ranAtUtc: DateTime.UtcNow,
            nextRunAtUtc: nextRunAt,
            newScore: snap.RiskScore,
            snapshotJson: snapJson,
            windowComplete: windowComplete);

        if (!ok)
            _log.LogWarning("[risk-watch] webhook delivery failed for sub {Id} tick {N}: {Err}",
                sub.Id, tickNumber, error);
    }

    public static string ComputeSignature(string secret, int tick, long timestamp, string body)
    {
        var canonical = $"{tick}.{timestamp}.{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
