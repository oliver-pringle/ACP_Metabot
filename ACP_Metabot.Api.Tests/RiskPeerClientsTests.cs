using System.Net;
using System.Text;
using ACP_Metabot.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.0 riskAttestPro Task 3: WitnessBot peer client tests.
///
/// The orchestrator (Task 6) treats Status="unavailable" as a soft failure
/// (counts against the 4-of-7 floor). Status="fresh" with IsAcpAgent=false
/// is a positive datum — the wallet is verifiably NOT a witnessed ACP agent.
/// Status="fresh" with IsAcpAgent=true is the populated-manifest case.
/// </summary>
public class WitnessBotClientTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?>? values = null)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();

    private static IHttpClientFactory BuildFactory(StubHandler handler)
        => new StubHttpClientFactory(handler);

    [Fact]
    public async Task WitnessClient_returns_unavailable_when_base_url_unset()
    {
        // No config values set → both BaseUrl and ApiKey resolve empty.
        // The client must short-circuit BEFORE creating an HttpClient and
        // return Status="unavailable".
        var config = BuildConfig();
        var client = new WitnessBotClient(
            new StubHttpClientFactory(new StubHandler(_ => throw new InvalidOperationException(
                "WitnessBotClient must NOT make an HTTP call when base url is unset"))),
            config,
            NullLogger<WitnessBotClient>.Instance);

        var result = await client.ManifestByAgentAsync("0xabc");

        Assert.Equal("unavailable", result.Status);
        Assert.False(result.IsAcpAgent);
        Assert.Null(result.CatalogueHash);
        Assert.Null(result.SignerEoa);
        Assert.Null(result.SignedAt);
        Assert.Null(result.ManifestUid);
        Assert.Contains("WITNESSBOT", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WitnessClient_returns_fresh_no_manifest_on_404()
    {
        // Wallet has not been witnessed (e.g. not an ACP agent or hasn't run
        // manifest_sign). That's POSITIVE evidence the wallet is unwitnessed,
        // not a transport failure — fresh, not unavailable.
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["WitnessBot:BaseUrl"] = "http://witnessbot-api:5000/",
            ["WitnessBot:ApiKey"]  = "k",
        });
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"not_found\"}", Encoding.UTF8, "application/json"),
        });
        var client = new WitnessBotClient(BuildFactory(handler), config,
            NullLogger<WitnessBotClient>.Instance);

        var result = await client.ManifestByAgentAsync("0xabc");

        Assert.Equal("fresh", result.Status);
        Assert.False(result.IsAcpAgent);
        Assert.Null(result.CatalogueHash);
        Assert.Null(result.SignerEoa);
        Assert.Null(result.SignedAt);
        Assert.Null(result.ManifestUid);
        Assert.Contains("no manifest", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WitnessClient_returns_manifest_on_200()
    {
        var json = "{" +
            "\"agentAddress\":\"0xc834e81ebe0921fdf9458ac422861df441a6caf9\"," +
            "\"catalogueHash\":\"0xdeadbeef\"," +
            "\"signerAddress\":\"0x13f3000000000000000000000000000000000193b\"," +
            "\"signedAt\":\"2026-05-30T00:00:00Z\"," +
            "\"manifestUid\":\"0xfeedface\"" +
            "}";
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["WitnessBot:BaseUrl"] = "http://witnessbot-api:5000/",
            ["WitnessBot:ApiKey"]  = "k",
        });
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var client = new WitnessBotClient(BuildFactory(handler), config,
            NullLogger<WitnessBotClient>.Instance);

        var result = await client.ManifestByAgentAsync(
            "0xc834e81ebe0921fdf9458ac422861df441a6caf9");

        Assert.Equal("fresh", result.Status);
        Assert.True(result.IsAcpAgent);
        Assert.Equal("0xdeadbeef", result.CatalogueHash);
        Assert.Equal("0x13f3000000000000000000000000000000000193b", result.SignerEoa);
        Assert.Equal("2026-05-30T00:00:00Z", result.SignedAt);
        Assert.Equal("0xfeedface", result.ManifestUid);
    }

    [Fact]
    public async Task WitnessClient_returns_unavailable_on_500()
    {
        // Defensive: upstream 5xx is a transport-class failure (counts against
        // the 4-of-7 floor in the orchestrator), distinct from the 404 path.
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["WitnessBot:BaseUrl"] = "http://witnessbot-api:5000/",
            ["WitnessBot:ApiKey"]  = "k",
        });
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new WitnessBotClient(BuildFactory(handler), config,
            NullLogger<WitnessBotClient>.Instance);

        var result = await client.ManifestByAgentAsync("0xabc");

        Assert.Equal("unavailable", result.Status);
        Assert.False(result.IsAcpAgent);
        Assert.Contains("500", result.Details);
    }

    [Fact]
    public async Task WitnessClient_returns_unavailable_on_transport_exception()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["WitnessBot:BaseUrl"] = "http://witnessbot-api:5000/",
            ["WitnessBot:ApiKey"]  = "k",
        });
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var client = new WitnessBotClient(BuildFactory(handler), config,
            NullLogger<WitnessBotClient>.Instance);

        var result = await client.ManifestByAgentAsync("0xabc");

        Assert.Equal("unavailable", result.Status);
        Assert.False(result.IsAcpAgent);
        Assert.Contains("connection refused", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test scaffolding ────────────────────────────────────────────────────

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;
        public StubHttpClientFactory(StubHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
