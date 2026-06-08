using System.Net;
using System.Text;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class TheSecurityBotClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public HttpRequestMessage? LastRequest;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null) await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://securitybot-api:5000/")
            };
    }

    private static TheSecurityBotClient Make(StubHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TheSecurityBot:ApiKey"]  = "test-key",
                ["TheSecurityBot:BaseUrl"] = "http://securitybot-api:5000/",
            }).Build();
        var env = new FakeEnv("Production");
        return new TheSecurityBotClient(new StubFactory(handler), config, env, NullLogger<TheSecurityBotClient>.Instance);
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public FakeEnv(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public async Task ScanAsync_Scanned_MapsScoreGradeAndCounts()
    {
        const string body = """
        {"agentAddress":"0xA","verdict":"PASS","score":88,"grade":"B",
         "observableCount":11,"totalPatterns":74,"scannedAt":"2026-06-08T10:00:00.0000000Z",
         "findings":[{"severity":"Medium"},{"severity":"High"},{"severity":"High"}]}
        """;
        var client = Make(new StubHandler(HttpStatusCode.OK, body));

        var r = await client.ScanAsync("0xA", default);
        var v = r.Verdict;

        Assert.Equal(SecurityStatus.Scanned, v.Status);
        Assert.Equal(88, v.Score);
        Assert.Equal("B", v.Grade);
        Assert.Equal(11, v.ObservableCount);
        Assert.Equal(3, v.FindingCount);
        Assert.Contains("\"High\":2", v.SeverityCountsJson);
        Assert.Equal("2026-06-08T10:00:00.0000000Z", v.ScannedAt); // normalized UTC "O"
        Assert.Null(v.LastError);
        Assert.NotNull(r.RawFindingsJson);
        Assert.Contains("\"severity\":\"High\"", r.RawFindingsJson!);
        Assert.Equal("PASS", r.RawVerdict);
    }

    [Fact]
    public async Task ScanAsync_NotAuditable_MapsStatus()
    {
        const string body = """
        {"agentAddress":"0xA","verdict":"NOT_AUDITABLE","reason":"no http surface"}
        """;
        var client = Make(new StubHandler(HttpStatusCode.OK, body));

        var r = await client.ScanAsync("0xA", default);
        var v = r.Verdict;

        Assert.Equal(SecurityStatus.NotAuditable, v.Status);
        Assert.Null(v.Score);
        Assert.Null(v.Grade);
        Assert.Null(r.RawFindingsJson);
    }

    [Fact]
    public async Task ScanAsync_Non2xx_MapsToError_NoThrow()
    {
        var client = Make(new StubHandler(HttpStatusCode.InternalServerError, "{\"error\":\"INTERNAL_ERROR\"}"));

        var r = await client.ScanAsync("0xA", default);
        var v = r.Verdict;

        Assert.Equal(SecurityStatus.Error, v.Status);
        Assert.NotNull(v.LastError);
        Assert.DoesNotContain("INTERNAL_ERROR", v.LastError!);
        Assert.Null(r.RawFindingsJson);
    }

    [Fact]
    public async Task ScanAsync_PreservesFullFindingsFields()
    {
        const string body = """
        {"verdict":"PASS","score":80,"grade":"B","observableCount":11,
         "findings":[{"patternId":"P9","title":"RPC url leak","severity":"High","verdict":"Present","evidence":"alchemy key in log","fixRef":"P9"}]}
        """;
        var client = Make(new StubHandler(HttpStatusCode.OK, body));

        var r = await client.ScanAsync("0xA", default);

        Assert.NotNull(r.RawFindingsJson);
        Assert.Contains("\"patternId\"", r.RawFindingsJson!);
        Assert.Contains("\"evidence\"", r.RawFindingsJson!);
        Assert.Contains("\"fixRef\"", r.RawFindingsJson!);
        Assert.Equal(1, r.Verdict.FindingCount);
    }

    [Fact]
    public async Task ScanAsync_SendsApiKeyHeader_AndAgentAddressBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"verdict\":\"PASS\",\"score\":90,\"grade\":\"A\",\"observableCount\":11,\"findings\":[]}");
        var client = Make(handler);

        await client.ScanAsync("0xABC", default);

        Assert.True(handler.LastRequest!.Headers.Contains("X-API-Key"));
        Assert.Equal("test-key", string.Join("", handler.LastRequest.Headers.GetValues("X-API-Key")));
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.EndsWith("v1/internal/scan", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public void Ctor_NonDev_KeyMissingButBaseUrlSet_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TheSecurityBot:ApiKey"]  = "",
                ["TheSecurityBot:BaseUrl"] = "http://securitybot-api:5000/",
            }).Build();
        var env = new FakeEnv("Production");
        Assert.Throws<InvalidOperationException>(() =>
            new TheSecurityBotClient(new StubFactory(new StubHandler(HttpStatusCode.OK, "{}")),
                config, env, NullLogger<TheSecurityBotClient>.Instance));
    }
}
