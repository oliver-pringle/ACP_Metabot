using System.Net;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// SSRF-safe POST primitive used by every outbound buyer-supplied webhook in
/// Metabot. Owns a singleton HttpClient with AllowAutoRedirect disabled, and
/// follows redirects MANUALLY so every Location target is re-validated through
/// WebhookUrlValidator (catches 302 → 169.254.169.254 / RFC1918 / loopback).
///
/// Used by:
///   • WebhookDeliveryService (search-watch alert push)
///   • MarketplacePulseService (marketplacePulseSub daily digest push)
///   • RiskWatchWorker (daily_risk_watch subscription push)
///
/// Each caller composes its own HttpRequestMessage (headers + HMAC signature
/// + body) per hop via the requestBuilder lambda — this class only owns the
/// network primitive + redirect-following.
/// </summary>
public sealed class SafeWebhookHttpClient : IDisposable
{
    private const int MaxRedirectHops = 5;

    private readonly HttpClient _http;
    private readonly ILogger<SafeWebhookHttpClient> _logger;

    public SafeWebhookHttpClient(ILogger<SafeWebhookHttpClient> logger)
    {
        // Auto-redirect off — buyer-supplied URLs can 302 into internal hosts
        // unless every hop is re-validated. ConnectTimeout caps slow-loris
        // attacks against the connect phase; per-request timeout is set by
        // the caller via the linked CTS so different callers (5s alert push,
        // 10s risk tick, 15s pulse tick) get their own ceiling.
        //
        // 2026-05-24 hardening: ConnectCallback re-validates the actual
        // resolved IPEndPoint against WebhookUrlValidator.IsConnectBlocked
        // at TCP connect time. Closes the DNS-rebind TOCTOU window between
        // ValidateAsync's DNS resolution and HttpClient's own connect-time
        // resolution. Defense-in-depth on top of the manual per-hop
        // ValidateAsync that SendWithValidatedRedirectsAsync already does.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = WebhookConnectCallbacks.PinValidatedIp,
        };
        _http = new HttpClient(handler, disposeHandler: true);
        _logger = logger;
    }

    /// <summary>
    /// POSTs through a manually-followed, SSRF-validated redirect chain.
    /// Returns true on the first 2xx anywhere in the chain (max 5 hops).
    ///
    /// <paramref name="requestBuilder"/> is invoked once per hop with the
    /// current target URL and must return a fresh HttpRequestMessage —
    /// HttpContent is consumed when sent, so the body has to be reconstructed
    /// for each redirect hop.
    /// </summary>
    public async Task<bool> SendWithValidatedRedirectsAsync(
        string initialUrl,
        Func<string, HttpRequestMessage> requestBuilder,
        TimeSpan requestTimeout,
        CancellationToken ct)
    {
        var current = initialUrl;
        for (int hop = 0; hop <= MaxRedirectHops; hop++)
        {
            var check = await WebhookUrlValidator.ValidateAsync(current, ct);
            if (!check.Ok)
            {
                _logger.LogWarning(
                    "[safe-webhook] url validation failed at hop {Hop}: {Reason} ({Url})",
                    hop, check.Reason, current);
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(requestTimeout);

            HttpResponseMessage? res = null;
            try
            {
                using var req = requestBuilder(current);
                try
                {
                    res = await _http.SendAsync(req, timeoutCts.Token);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("[safe-webhook] timeout at hop {Hop} ({Url})", hop, current);
                    return false;
                }

                if (res.IsSuccessStatusCode) return true;

                if (IsRedirect(res.StatusCode))
                {
                    var loc = res.Headers.Location;
                    if (loc is null)
                    {
                        _logger.LogWarning(
                            "[safe-webhook] {Status} without Location at {Url}",
                            (int)res.StatusCode, current);
                        return false;
                    }
                    current = (loc.IsAbsoluteUri ? loc : new Uri(new Uri(current), loc)).ToString();
                    continue;
                }

                _logger.LogWarning(
                    "[safe-webhook] non-2xx ({Status}) from {Url}",
                    (int)res.StatusCode, current);
                return false;
            }
            finally
            {
                res?.Dispose();
            }
        }

        _logger.LogWarning(
            "[safe-webhook] exceeded {Max} redirects starting from {Url}",
            MaxRedirectHops, initialUrl);
        return false;
    }

    public void Dispose() => _http.Dispose();

    private static bool IsRedirect(HttpStatusCode s)
        => s == HttpStatusCode.MovedPermanently     // 301
        || s == HttpStatusCode.Found                 // 302
        || s == HttpStatusCode.SeeOther              // 303
        || s == HttpStatusCode.TemporaryRedirect     // 307
        || s == HttpStatusCode.PermanentRedirect;    // 308
}
