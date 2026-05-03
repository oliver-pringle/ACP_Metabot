using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ACP_Metabot.Api.Services;

public class WebhookDeliveryService
{
    private const string UserAgent = "TheMetaBot/1.0 (acp-watch)";
    private const int MaxRedirectHops = 5;

    private readonly HttpClient _http;
    private readonly ILogger<WebhookDeliveryService> _logger;

    private static readonly TimeSpan[] RetryDelays = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public WebhookDeliveryService(ILogger<WebhookDeliveryService> logger)
    {
        // Build our own HttpClient with auto-redirects DISABLED. The default
        // SocketsHttpHandler blindly follows 3xx Location headers, which means
        // a buyer can register a public webhookUrl that 302s to
        // http://169.254.169.254/latest/meta-data/ (cloud metadata) or
        // http://10.0.0.1/internal-api — bypassing WebhookUrlValidator
        // entirely. With auto-redirect off, every hop's Location is validated
        // through the same SSRF guard used at registration.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _logger = logger;
    }

    /// <summary>
    /// POSTs the payload as JSON to the URL. Returns true on first 2xx response.
    /// On failure, retries up to 3 times with backoff (1s, 4s, 16s).
    /// Re-validates the URL before each attempt to defend against DNS rebinding,
    /// and re-validates every redirect hop's Location target the same way.
    /// </summary>
    public async Task<bool> DeliverAsync(
        string url,
        string watchId,
        int alertNumber,
        object payload,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            if (attempt > 0)
            {
                try { await Task.Delay(RetryDelays[attempt - 1], ct); }
                catch (OperationCanceledException) { return false; }
            }

            try
            {
                if (await SendOnceWithSafeRedirectsAsync(url, watchId, alertNumber, payload, ct))
                {
                    if (attempt > 0)
                        _logger.LogInformation("[webhook] success on retry {Attempt} for {Url}", attempt, url);
                    return true;
                }
                _logger.LogWarning("[webhook] failed on attempt {Attempt} for {Url}", attempt + 1, url);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[webhook] error on attempt {Attempt} for {Url}", attempt + 1, url);
            }
        }
        return false;
    }

    // Returns true on a 2xx anywhere in the redirect chain. Validates the
    // initial URL and every Location target through WebhookUrlValidator so a
    // 302 → 169.254.169.254 (or any other private/internal address) is
    // refused before the next request goes out.
    private async Task<bool> SendOnceWithSafeRedirectsAsync(
        string url, string watchId, int alertNumber, object payload, CancellationToken ct)
    {
        var current = url;
        for (int hop = 0; hop <= MaxRedirectHops; hop++)
        {
            var check = await WebhookUrlValidator.ValidateAsync(current, ct);
            if (!check.Ok)
            {
                _logger.LogWarning(
                    "[webhook] url validation failed at hop {Hop}: {Reason} ({Url})",
                    hop, check.Reason, current);
                return false;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, current)
            {
                Content = JsonContent.Create(payload, options: JsonOpts),
            };
            req.Headers.TryAddWithoutValidation("X-Watch-Id", watchId);
            req.Headers.TryAddWithoutValidation("X-Alert-Number", alertNumber.ToString());

            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return true;

            // 3xx with Location → validate target and continue. Any other
            // non-2xx (4xx/5xx, or 3xx without Location) is a definitive
            // delivery failure for this attempt.
            if (IsRedirect(res.StatusCode))
            {
                var location = res.Headers.Location;
                if (location is null)
                {
                    _logger.LogWarning(
                        "[webhook] {Status} without Location header from {Url}", (int)res.StatusCode, current);
                    return false;
                }
                var nextUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(new Uri(current), location);
                current = nextUri.ToString();
                continue;
            }

            _logger.LogWarning("[webhook] non-2xx ({Status}) from {Url}",
                (int)res.StatusCode, current);
            return false;
        }
        _logger.LogWarning("[webhook] exceeded {Max} redirects starting from {Url}", MaxRedirectHops, url);
        return false;
    }

    private static bool IsRedirect(HttpStatusCode s)
        => s == HttpStatusCode.MovedPermanently     // 301
        || s == HttpStatusCode.Found                 // 302
        || s == HttpStatusCode.SeeOther              // 303
        || s == HttpStatusCode.TemporaryRedirect     // 307
        || s == HttpStatusCode.PermanentRedirect;    // 308
}
