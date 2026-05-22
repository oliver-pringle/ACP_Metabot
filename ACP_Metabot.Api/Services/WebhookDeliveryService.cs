using System.Net.Http.Json;
using System.Text.Json;

namespace ACP_Metabot.Api.Services;

public class WebhookDeliveryService
{
    private const string UserAgent = "TheMetaBot/1.0 (acp-watch)";

    private readonly SafeWebhookHttpClient _safeHttp;
    private readonly ILogger<WebhookDeliveryService> _logger;

    private static readonly TimeSpan[] RetryDelays = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public WebhookDeliveryService(SafeWebhookHttpClient safeHttp, ILogger<WebhookDeliveryService> logger)
    {
        _safeHttp = safeHttp;
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
                var ok = await _safeHttp.SendWithValidatedRedirectsAsync(
                    url,
                    hopUrl =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post, hopUrl)
                        {
                            Content = JsonContent.Create(payload, options: JsonOpts),
                        };
                        req.Headers.UserAgent.ParseAdd(UserAgent);
                        req.Headers.TryAddWithoutValidation("X-Watch-Id", watchId);
                        req.Headers.TryAddWithoutValidation("X-Alert-Number", alertNumber.ToString());
                        return req;
                    },
                    requestTimeout: TimeSpan.FromSeconds(5),
                    ct: ct);

                if (ok)
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
}
