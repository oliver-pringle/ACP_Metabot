using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class ReputationFeedSyncWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly ReputationFeedRepository _feeds;
    private readonly IConfiguration _config;

    public ReputationFeedSyncWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_feedsync_test_{Guid.NewGuid():N}.db");
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
                ["Feeds:Sync:Enabled"]         = "true",
                ["Feeds:Sync:IntervalMinutes"] = "5",
                ["Feeds:Sync:BatchSize"]       = "3",
                ["Feeds:Sync:MaxAgents"]       = "500"
            }).Build();
        _db = new Db(_config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _feeds = new ReputationFeedRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task RunOnceAsync_NoFeeds_ReturnsAllZero()
    {
        var fake = new FakeChainlinkBotClient();
        var worker = NewWorker(fake);

        var (synced, notPushed, failed) = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, synced);
        Assert.Equal(0, notPushed);
        Assert.Equal(0, failed);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task RunOnceAsync_SyncsPublishedFeeds_AndUpdatesRepo()
    {
        await _feeds.UpsertDeployedAsync(
            "0xalice", "0xagg_alice", "0xm", 8, null,
            DateTime.UtcNow, DateTime.UtcNow);
        await _feeds.UpsertDeployedAsync(
            "0xbob", "0xagg_bob", "0xm", 8, null,
            DateTime.UtcNow, DateTime.UtcNow);

        var pushedAt = new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);
        var fake = new FakeChainlinkBotClient
        {
            Handler = (addr, _) => Task.FromResult<FeedScoreResponse?>(
                new FeedScoreResponse(addr, 73.5, "0xm", pushedAt, 42))
        };
        var worker = NewWorker(fake);

        var (synced, notPushed, failed) = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, synced);
        Assert.Equal(0, notPushed);
        Assert.Equal(0, failed);

        var alice = await _feeds.GetAsync("0xalice");
        Assert.NotNull(alice);
        Assert.Equal(73.5, alice.LatestScore);
        Assert.Equal(42, alice.LastPushedRound);
        Assert.Equal(pushedAt, alice.LastPushedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public async Task RunOnceAsync_404FromUpstream_CountsAsNotPushed_DoesNotUpdateRepo()
    {
        await _feeds.UpsertDeployedAsync(
            "0xneverpushed", "0xagg", "0xm", 8, null,
            DateTime.UtcNow, DateTime.UtcNow);

        var fake = new FakeChainlinkBotClient
        {
            Handler = (_, _) => Task.FromResult<FeedScoreResponse?>(null) // 404 → null
        };
        var worker = NewWorker(fake);

        var (synced, notPushed, failed) = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, synced);
        Assert.Equal(1, notPushed);
        Assert.Equal(0, failed);

        var row = await _feeds.GetAsync("0xneverpushed");
        Assert.NotNull(row);
        Assert.Null(row.LastPushedRound);
        Assert.Null(row.LastPushedAt);
    }

    [Fact]
    public async Task RunOnceAsync_ClientThrows_CountsAsFailed_DoesNotPropagate()
    {
        await _feeds.UpsertDeployedAsync(
            "0xerrors", "0xagg", "0xm", 8, null,
            DateTime.UtcNow, DateTime.UtcNow);

        var fake = new FakeChainlinkBotClient
        {
            Handler = (_, _) => throw new HttpRequestException("upstream down")
        };
        var worker = NewWorker(fake);

        var (synced, notPushed, failed) = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, synced);
        Assert.Equal(1, failed);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsFeedsWithEmptyAggregator()
    {
        // RecordErrorAsync creates a row with empty aggregator_address.
        await _feeds.RecordErrorAsync("0xfailed", "deploy attempt failed");

        var fake = new FakeChainlinkBotClient();
        var worker = NewWorker(fake);

        var result = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal((0, 0, 0), result);
        Assert.Equal(0, fake.CallCount); // skipped at ListDeployedAsync filter
    }

    private ReputationFeedSyncWorker NewWorker(ITheChainlinkBotClient client) =>
        new(_config, _feeds, client, NullLogger<ReputationFeedSyncWorker>.Instance);

    private sealed class FakeChainlinkBotClient : ITheChainlinkBotClient
    {
        public int CallCount;
        public Func<string, CancellationToken, Task<FeedScoreResponse?>> Handler { get; set; } =
            (_, _) => Task.FromResult<FeedScoreResponse?>(null);

        public Task<string> ExecuteFunctionsAsync(string jobId, string buyerAgent,
            string sourceCode, string[] args, string secretsLocation, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<FeedAddressResponse> RequestFeedAddressAsync(
            string agentAddress, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<FeedScoreResponse?> GetFeedScoreAsync(
            string agentAddress, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            return Handler(agentAddress, ct);
        }
    }
}
