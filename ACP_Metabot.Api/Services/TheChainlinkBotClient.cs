using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// Response shape from ACP_ChainlinkBot's <c>POST /v1/internal/functions</c> endpoint.
/// </summary>
public sealed record FunctionsResponse(
    [property: JsonPropertyName("requestId")] string RequestId);

/// <summary>
/// HTTP client for cross-bot calls to ACP_ChainlinkBot via the <c>acp-shared</c>
/// external Docker bridge. Used by TheMetaBot to hire Chainlink Functions for
/// off-chain signal enrichment (GitHub, social, etc.) when scoring agents.
///
/// Auth: <c>X-API-Key</c> header. The header value is ACP_ChainlinkBot's own
/// <c>INTERNAL_API_KEY</c>, exposed here as <c>THECHAINLINKBOT_API_KEY</c> in
/// TheMetaBot's environment so the two bots' independent keys don't collide.
/// </summary>
public sealed class TheChainlinkBotClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<TheChainlinkBotClient> _log;

    public TheChainlinkBotClient(IHttpClientFactory httpFactory, IConfiguration config,
        ILogger<TheChainlinkBotClient> log)
    {
        _httpFactory = httpFactory;
        _apiKey = config["TheChainlinkBot:ApiKey"] ?? "";
        _log = log;
    }

    /// <summary>
    /// Hire ACP_ChainlinkBot to execute a Chainlink Functions request. Returns
    /// the upstream <c>requestId</c> on 2xx; throws on non-2xx so the caller
    /// surfaces the failure to its own buyer.
    /// </summary>
    public async Task<string> ExecuteFunctionsAsync(
        string jobId,
        string buyerAgent,
        string sourceCode,
        string[] args,
        string secretsLocation,
        CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient("thechainlinkbot");
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/internal/functions")
        {
            Content = JsonContent.Create(new
            {
                jobId,
                buyerAgent,
                sourceCode,
                args,
                secretsLocation
            })
        };
        req.Headers.Add("X-API-Key", _apiKey);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("[thechainlinkbot] /v1/internal/functions returned {status}",
                resp.StatusCode);
        }
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<FunctionsResponse>(cancellationToken: ct);
        return result!.RequestId;
    }
}
