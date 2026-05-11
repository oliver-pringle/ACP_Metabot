using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class ReputationFeedPublisherWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly AgentReputationCacheRepository _cache;
    private readonly ReputationFeedRepository _feeds;

    public ReputationFeedPublisherWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_feedpublisher_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
                ["Feeds:Enabled"]  = "true",
                ["Feeds:TopN"]     = "10",
                ["Feeds:MinScore"] = "50",
                ["Feeds:BatchSize"] = "3"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _cache = new AgentReputationCacheRepository(_db);
        _feeds = new ReputationFeedRepository(_db);

        _config = config;
    }

    private readonly IConfiguration _config;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task RunOnceAsync_NoCandidates_ReturnsZero()
    {
        var fakeClient = new FakeChainlinkBotClient();
        var worker = NewWorker(fakeClient);

        var published = await worker.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(0, published);
        Assert.Equal(0, fakeClient.CallCount);
    }

    [Fact]
    public async Task RunOnceAsync_PublishesPendingHighScoreAgents()
    {
        await SeedAgent("0xAlice", "Alice", score: 90);
        await SeedAgent("0xBob",   "Bob",   score: 75);
        await SeedAgent("0xLow",   "Low",   score: 30);  // below MinScore=50

        var fakeClient = new FakeChainlinkBotClient
        {
            Handler = (addr, ct) => Task.FromResult(new FeedAddressResponse(
                AgentAddress: addr,
                AggregatorAddress: $"0xagg_{addr}",
                MethodologyHash: "0xfeed",
                Decimals: 8,
                DeployedAt: DateTime.UtcNow,
                LatestScore: 0))
        };
        var worker = NewWorker(fakeClient);

        var published = await worker.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(2, published);
        Assert.Equal(2, fakeClient.CallCount);
        Assert.Equal(2, await _feeds.CountPublishedAsync());
        Assert.Null((await _feeds.GetAsync("0xLow"))); // low-score not published
    }

    [Fact]
    public async Task RunOnceAsync_SkipsAlreadyPublishedAgents()
    {
        await SeedAgent("0xAlice", "Alice", score: 90);
        await _feeds.UpsertDeployedAsync(
            "0xalice", "0xprior", "0xm", 8, 90, DateTime.UtcNow, DateTime.UtcNow);

        var fakeClient = new FakeChainlinkBotClient();
        var worker = NewWorker(fakeClient);

        var published = await worker.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(0, published);
        Assert.Equal(0, fakeClient.CallCount);
    }

    [Fact]
    public async Task RunOnceAsync_RecordsErrorOnClientException_DoesNotThrow()
    {
        await SeedAgent("0xCharlie", "Charlie", score: 80);

        var fakeClient = new FakeChainlinkBotClient
        {
            Handler = (_, _) => throw new HttpRequestException("ChainlinkBot unreachable")
        };
        var worker = NewWorker(fakeClient);

        var published = await worker.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(0, published);
        var row = await _feeds.GetAsync("0xCharlie");
        Assert.NotNull(row);
        Assert.Contains("unreachable", row.LastError, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SeedAgent(string addr, string name, int score)
    {
        await _cache.UpsertAsync(new CachedReputationRow(
            AgentAddress: addr.ToLowerInvariant(),
            AgentName: name,
            AgentScore: score,
            SubScoresJson: "{}",
            RawCountsJson: "{}",
            FlagsJson: "[]",
            ComputedAt: DateTime.UtcNow,
            LastScannedBlock: 1L,
            Source: "test"));
    }

    private ReputationFeedPublisherWorker NewWorker(ITheChainlinkBotClient client) =>
        new(_config, _cache, _feeds, client,
            NullLogger<ReputationFeedPublisherWorker>.Instance);

    private sealed class FakeChainlinkBotClient : ITheChainlinkBotClient
    {
        public int CallCount;
        public Func<string, CancellationToken, Task<FeedAddressResponse>> Handler { get; set; } =
            (_, _) => Task.FromResult(new FeedAddressResponse(
                "", "0x0000000000000000000000000000000000000000", "", 0, DateTime.UtcNow, 0));

        public Task<string> ExecuteFunctionsAsync(string jobId, string buyerAgent,
            string sourceCode, string[] args, string secretsLocation, CancellationToken ct = default)
            => throw new NotImplementedException("not exercised in this test");

        public Task<FeedAddressResponse> RequestFeedAddressAsync(
            string agentAddress, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            return Handler(agentAddress, ct);
        }
    }
}
