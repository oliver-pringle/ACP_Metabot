using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ACP_Metabot.Api.Services;

public class WebhookDeliveryService
{
    private const string UserAgent = "TheMetaBot/1.0 (acp-watch)";

    private readonly HttpClient _http;
    private readonly ILogger<WebhookDeliveryService> _logger;

    private static readonly TimeSpan[] RetryDelays = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(16),
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public WebhookDeliveryService(IHttpClientFactory factory, ILogger<WebhookDeliveryService> logger)
    {
        _http = factory.CreateClient(nameof(WebhookDeliveryService));
        _http.Timeout = TimeSpan.FromSeconds(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _logger = logger;
    }

    /// <summary>
    /// POSTs the payload as JSON to the URL. Returns true on first 2xx response.
    /// On failure, retries up to 3 times with backoff (1s, 4s, 16s).
    /// Re-validates the URL before each attempt to defend against DNS rebinding.
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

            // Re-resolve and re-validate every attempt — guards against a DNS server
            // flipping between public and private IPs after registration.
            var urlCheck = await WebhookUrlValidator.ValidateAsync(url, ct);
            if (!urlCheck.Ok)
            {
                _logger.LogWarning("[webhook] url validation failed pre-POST: {Reason} ({Url})",
                    urlCheck.Reason, url);
                return false;
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(payload, options: JsonOpts),
                };
                req.Headers.TryAddWithoutValidation("X-Watch-Id", watchId);
                req.Headers.TryAddWithoutValidation("X-Alert-Number", alertNumber.ToString());

                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    if (attempt > 0)
                        _logger.LogInformation("[webhook] success on retry {Attempt} for {Url}", attempt, url);
                    return true;
                }
                _logger.LogWarning("[webhook] non-2xx ({Status}) on attempt {Attempt} for {Url}",
                    (int)res.StatusCode, attempt + 1, url);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[webhook] error on attempt {Attempt} for {Url}", attempt + 1, url);
            }
        }
        return false;
    }
}
