using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

public record AcpOffChainAgent(string WalletAddress, string Name, DateTime? LastActiveAt);

public class AcpOffChainClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AcpOffChainClient> _logger;

    public AcpOffChainClient(IHttpClientFactory httpFactory, IConfiguration config,
        ILogger<AcpOffChainClient> logger)
    {
        _logger = logger;
        var baseUrl = config["Indexer:ApiBaseUrl"] ?? "https://acpx.virtuals.io/";
        if (!baseUrl.EndsWith("/")) baseUrl += "/";

        _http = httpFactory.CreateClient(nameof(AcpOffChainClient));
        _http.BaseAddress ??= new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ACP_Metabot/1.0 (+https://app.virtuals.io)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.Add("Origin",  "https://app.virtuals.io");
        _http.DefaultRequestHeaders.Add("Referer", "https://app.virtuals.io/");
    }

    // Returns null on 404 / parse failure / timeout. Single retry on 5xx.
    public async Task<AcpOffChainAgent?> GetAgentAsync(string walletAddress, CancellationToken ct)
    {
        var addr = walletAddress.ToLowerInvariant();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var res = await _http.GetAsync($"api/agents/{addr}", ct);
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                if ((int)res.StatusCode >= 500 && attempt == 0)
                {
                    await Task.Delay(500, ct);
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[acp-offchain] {addr} returned {status}", addr, res.StatusCode);
                    return null;
                }
                var body = await res.Content.ReadFromJsonAsync<AgentDetailDto>(cancellationToken: ct);
                if (body is null || body.Data is null) return null;
                DateTime? lastActive = null;
                if (!string.IsNullOrEmpty(body.Data.LastActiveAt) &&
                    DateTime.TryParse(body.Data.LastActiveAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed))
                {
                    lastActive = parsed.ToUniversalTime();
                }
                return new AcpOffChainAgent(addr, body.Data.Name ?? "", lastActive);
            }
            catch (TaskCanceledException) { return null; }
            catch (HttpRequestException ex) when (attempt == 0)
            {
                _logger.LogInformation("[acp-offchain] {addr} retry: {msg}", addr, ex.Message);
                await Task.Delay(500, ct);
            }
        }
        return null;
    }

    private class AgentDetailDto
    {
        [JsonPropertyName("data")] public AgentInner? Data { get; set; }
    }

    private class AgentInner
    {
        [JsonPropertyName("walletAddress")] public string? WalletAddress { get; set; }
        [JsonPropertyName("name")]          public string? Name          { get; set; }
        [JsonPropertyName("lastActiveAt")]  public string? LastActiveAt  { get; set; }
    }
}
