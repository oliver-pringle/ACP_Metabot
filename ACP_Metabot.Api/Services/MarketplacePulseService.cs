using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// v1.9 marketplacePulseSub — create + tick logic for the daily marketplace
// digest subscription. Mirrors v1.8 RiskWatchWorker's design (same HMAC
// signing convention, same per-bot key rotation pattern, same 5-strike
// auto-suspend). The only divergence is the payload: this one ships a
// /digest snapshot instead of a /risk-snapshot.
//
// Workflow:
//   1. Buyer hires marketplacePulseSub → /v1/marketplace/pulse/subscribe
//      with jobId + buyerAddress + webhookUrl.
//   2. CreateAsync persists the row, generates a one-shot webhookSecret
//      (returned in the response; sidecar surfaces it to the buyer ONCE).
//   3. MarketplacePulseWorker (default OFF) polls every 5 min for due rows
//      and calls TickAsync, which fetches the digest, HMAC-signs the body,
//      POSTs to the webhook, and advances next_run_at by 24h.
public class MarketplacePulseService
{
    private const int BodyCapBytes = 1_048_576; // 1 MB

    private readonly PulseSubscriptionRepository _repo;
    private readonly DigestService _digests;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MarketplacePulseService> _log;

    public MarketplacePulseService(
        PulseSubscriptionRepository repo,
        DigestService digests,
        IHttpClientFactory httpFactory,
        ILogger<MarketplacePulseService> log)
    {
        _repo = repo;
        _digests = digests;
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<CreatePulseSubscriptionResponse> CreateAsync(
        CreatePulseSubscriptionRequest req, CancellationToken ct)
    {
        if (req.JobId <= 0) throw new ArgumentException("jobId must be positive");
        if (string.IsNullOrWhiteSpace(req.BuyerAddress))
            throw new ArgumentException("buyerAddress required");
        if (string.IsNullOrWhiteSpace(req.WebhookUrl))
            throw new ArgumentException("webhookUrl required");
        if (!Uri.TryCreate(req.WebhookUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("webhookUrl must be https://");

        var marketplace = NormalizeMarketplace(req.Marketplace);
        var now = DateTime.UtcNow;
        var firstTick = now.Date.AddDays(1).AddHours(12); // tomorrow 12:00 UTC
        var sub = new PulseSubscription(
            Id:                  $"pls_{Guid.NewGuid():N}",
            JobId:               req.JobId,
            BuyerAddress:        req.BuyerAddress,
            WebhookUrl:          req.WebhookUrl,
            WebhookSecret:       GenerateSecret(),
            Cadence:             "daily",
            Marketplace:         marketplace,
            TicksPurchased:      30,
            TicksDelivered:      0,
            ConsecutiveFailures: 0,
            Status:              "active",
            CreatedAt:           now,
            ExpiresAt:           now.AddDays(31),
            FirstTickAt:         firstTick,
            LastRunAt:           null,
            NextRunAt:           firstTick,
            LastPayloadHash:     null);

        await _repo.InsertAsync(sub);
        return new CreatePulseSubscriptionResponse(
            SubscriptionId: sub.Id,
            WebhookSecret:  sub.WebhookSecret,
            FirstTickAt:    sub.FirstTickAt,
            ExpiresAt:      sub.ExpiresAt,
            TicksPurchased: sub.TicksPurchased,
            Cadence:        sub.Cadence,
            Marketplace:    sub.Marketplace);
    }

    // One tick: compute digest, sign, POST, persist result. Public so the
    // worker can drive it and tests can exercise it directly.
    public async Task<bool> TickAsync(PulseSubscription sub, CancellationToken ct)
    {
        DateTime ranAt = DateTime.UtcNow;
        DateTime nextRunAt = ranAt.AddDays(1);
        string? payloadHash = null;
        try
        {
            var market = sub.Marketplace == "both" ? null : sub.Marketplace;
            var digest = await _digests.BuildAsync(1, market, chainFilter: null, priceMaxUsdc: null);
            var tickNumber = sub.TicksDelivered + 1;
            var payload = JsonSerializer.Serialize(new
            {
                subscriptionId = sub.Id,
                tickNumber,
                cadenceDays    = 1,
                computedAt     = ranAt,
                marketplace    = sub.Marketplace,
                digest
            });

            if (Encoding.UTF8.GetByteCount(payload) > BodyCapBytes)
            {
                _log.LogWarning("[pulse-sub] {Id} tick #{T} payload over 1MB cap; truncating gainers",
                    sub.Id, tickNumber);
                // Best-effort safety net — drop the higher-volume arrays.
                payload = JsonSerializer.Serialize(new
                {
                    subscriptionId = sub.Id,
                    tickNumber,
                    cadenceDays    = 1,
                    computedAt     = ranAt,
                    marketplace    = sub.Marketplace,
                    note           = "payload truncated (over 1MB cap)",
                    digestComputedAt = digest.ComputedAt,
                });
            }
            payloadHash = ComputeSha256(payload);

            var ok = await PostSignedAsync(sub.WebhookUrl, sub.WebhookSecret, payload, ct);
            var windowComplete = sub.TicksDelivered + 1 >= sub.TicksPurchased;
            await _repo.RecordTickResultAsync(sub.Id, ok, ranAt, nextRunAt,
                ok ? payloadHash : null, ok && windowComplete);
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[pulse-sub] tick failed for {Id}", sub.Id);
            await _repo.RecordTickResultAsync(sub.Id, false, ranAt, nextRunAt, null, false);
            return false;
        }
    }

    // Cross-portfolio convention: header is X-Signature with format
    //   t=<unix-seconds>,v1=<hex-hmac-sha256(t + "." + body, secret)>
    // RevokeBot and v1.8 risk_watch use the same shape, so buyer-side
    // verifiers written once work across every Metabot/portfolio subscription.
    private async Task<bool> PostSignedAsync(string url, string secret, string body,
        CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sigBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{ts}.{body}"));
        var sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant();

        using var http = _httpFactory.CreateClient("pulse-sub-webhook");
        http.Timeout = TimeSpan.FromSeconds(15);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("X-Signature", $"t={ts},v1={sigHex}");
        req.Headers.UserAgent.TryParseAdd("ACP_Metabot/1.9 (marketplacePulseSub)");

        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[pulse-sub] webhook {Url} returned {Status}", url, resp.StatusCode);
                return false;
            }
            return true;
        }
        catch (TaskCanceledException) { return false; }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[pulse-sub] webhook {Url} threw", url);
            return false;
        }
    }

    public static string ComputeSha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateSecret()
    {
        var buf = new byte[32];
        RandomNumberGenerator.Fill(buf);
        return "whs_" + Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static string NormalizeMarketplace(string? raw)
        => raw?.ToLowerInvariant() switch
        {
            "v1"   => "v1",
            "v2"   => "v2",
            "both" => "both",
            null   => "both",
            ""     => "both",
            _      => throw new ArgumentException("marketplace must be 'v1', 'v2', or 'both'")
        };
}

public record CreatePulseSubscriptionRequest(
    long JobId, string BuyerAddress, string WebhookUrl, string? Marketplace);

public record CreatePulseSubscriptionResponse(
    string SubscriptionId, string WebhookSecret, DateTime FirstTickAt,
    DateTime ExpiresAt, int TicksPurchased, string Cadence, string Marketplace);
