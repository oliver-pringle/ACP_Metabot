using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// Response shape from ACP_ChainlinkBot's <c>POST /v1/internal/functions</c> endpoint.
/// </summary>
public sealed record FunctionsResponse(
    [property: JsonPropertyName("requestId")] string RequestId);

/// <summary>
/// Response shape from ACP_ChainlinkBot's <c>POST /feed-address</c> endpoint. The
/// aggregator address is the on-chain AggregatorV3Interface that consumers
/// (DeFi protocols, indexers, other bots) can read with the same shape as
/// Chainlink's ETH/USD feed. Idempotent on agentAddress — subsequent calls
/// return the existing aggregator without a redeploy.
/// </summary>
public sealed record FeedAddressResponse(
    [property: JsonPropertyName("agentAddress")]      string AgentAddress,
    [property: JsonPropertyName("aggregatorAddress")] string AggregatorAddress,
    [property: JsonPropertyName("methodologyHash")]   string MethodologyHash,
    [property: JsonPropertyName("decimals")]          int Decimals,
    [property: JsonPropertyName("deployedAt")]        DateTime DeployedAt,
    [property: JsonPropertyName("latestScore")]       double LatestScore);

/// <summary>
/// Response shape from ACP_ChainlinkBot's <c>GET /v1/feed/{agent}/score</c>
/// endpoint. Returns the most recent on-chain push for the agent — the
/// payload the AggregatorV3 feed reflects. 404 when never pushed.
/// </summary>
public sealed record FeedScoreResponse(
    [property: JsonPropertyName("agentAddress")]    string AgentAddress,
    [property: JsonPropertyName("publishedValue")]  double PublishedValue,
    [property: JsonPropertyName("methodologyHash")] string MethodologyHash,
    [property: JsonPropertyName("pushedAt")]        DateTime PushedAt,
    [property: JsonPropertyName("roundId")]         long RoundId);

/// <summary>
/// Cross-bot contract for ACP_ChainlinkBot HTTP calls. Extracted so tests can
/// substitute a fake without spinning up an HttpClient + IHttpClientFactory.
/// </summary>
public interface ITheChainlinkBotClient
{
    Task<string> ExecuteFunctionsAsync(
        string jobId, string buyerAgent, string sourceCode, string[] args,
        string secretsLocation, CancellationToken ct = default);

    Task<FeedAddressResponse> RequestFeedAddressAsync(
        string agentAddress, CancellationToken ct = default);

    /// <summary>
    /// Latest on-chain push for the agent, or null when no push has happened
    /// yet. Used by ReputationFeedSyncWorker to keep Metabot's view of the
    /// reputation_feeds table in sync with ChainlinkBot's ScoringPushWorker
    /// activity. 404 → null; other non-2xx → throw.
    /// </summary>
    Task<FeedScoreResponse?> GetFeedScoreAsync(
        string agentAddress, CancellationToken ct = default);
}

/// <summary>
/// HTTP client for cross-bot calls to ACP_ChainlinkBot via the <c>acp-shared</c>
/// external Docker bridge. Used by TheMetaBot to hire Chainlink Functions for
/// off-chain signal enrichment (GitHub, social, etc.) when scoring agents.
///
/// Auth: <c>X-API-Key</c> header. The header value is ACP_ChainlinkBot's own
/// <c>INTERNAL_API_KEY</c>, exposed here as <c>THECHAINLINKBOT_API_KEY</c> in
/// TheMetaBot's environment so the two bots' independent keys don't collide.
/// </summary>
public sealed class TheChainlinkBotClient : ITheChainlinkBotClient
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

    /// <summary>
    /// Request an on-chain reputation feed for the given agent. ChainlinkBot
    /// resolves through its 4-tier cache (in-process → SQLite → factory.aggregatorOf
    /// view → factory.deploy) and returns the aggregator address. The first call
    /// for a never-deployed agent costs ~$0.0001 gas on Base; subsequent calls
    /// are idempotent reads.
    /// </summary>
    public async Task<FeedAddressResponse> RequestFeedAddressAsync(
        string agentAddress, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient("thechainlinkbot");
        using var req = new HttpRequestMessage(HttpMethod.Post, "feed-address")
        {
            Content = JsonContent.Create(new { agentAddress })
        };
        if (!string.IsNullOrEmpty(_apiKey)) req.Headers.Add("X-API-Key", _apiKey);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("[thechainlinkbot] /feed-address returned {status}: {body}",
                resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        var result = await resp.Content.ReadFromJsonAsync<FeedAddressResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("ChainlinkBot returned empty feed-address response");
        return result;
    }

    /// <inheritdoc />
    public async Task<FeedScoreResponse?> GetFeedScoreAsync(
        string agentAddress, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient("thechainlinkbot");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/feed/{agentAddress}/score");
        if (!string.IsNullOrEmpty(_apiKey)) req.Headers.Add("X-API-Key", _apiKey);

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("[thechainlinkbot] /v1/feed/{Agent}/score returned {Status}: {Body}",
                agentAddress, resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        return await resp.Content.ReadFromJsonAsync<FeedScoreResponse>(cancellationToken: ct);
    }
}
