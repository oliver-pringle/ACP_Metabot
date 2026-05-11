using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class ReputationFeedRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly ReputationFeedRepository _repo;

    public ReputationFeedRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_repfeeds_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new ReputationFeedRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task UpsertDeployedAsync_ThenGetAsync_RoundtripsAllFields()
    {
        var deployedAt = new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);
        var firstSeen = new DateTime(2026, 5, 11, 9, 0, 0, DateTimeKind.Utc);

        await _repo.UpsertDeployedAsync(
            agentAddress: "0xABC",
            aggregatorAddress: "0xDEAD",
            methodologyHash: "0xfeed",
            decimals: 8,
            latestScore: 73.5,
            deployedAt: deployedAt,
            firstSeenAt: firstSeen);

        var row = await _repo.GetAsync("0xABC");
        Assert.NotNull(row);
        Assert.Equal("0xabc", row.AgentAddress); // lower-cased on insert
        Assert.Equal("0xDEAD", row.AggregatorAddress);
        Assert.Equal("0xfeed", row.MethodologyHash);
        Assert.Equal(8, row.Decimals);
        Assert.Equal(73.5, row.LatestScore);
        Assert.Equal(deployedAt, row.DeployedAt.ToUniversalTime());
        Assert.Equal(firstSeen, row.FirstSeenAt.ToUniversalTime());
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task UpsertDeployedAsync_OverwritesExistingRow_ClearsLastError()
    {
        await _repo.RecordErrorAsync("0xabc", "ChainlinkBot unreachable");
        var withError = await _repo.GetAsync("0xabc");
        Assert.Equal("ChainlinkBot unreachable", withError!.LastError);

        await _repo.UpsertDeployedAsync(
            "0xabc", "0xagg", "0xm", 8, 50.0,
            DateTime.UtcNow, DateTime.UtcNow);

        var after = await _repo.GetAsync("0xabc");
        Assert.Null(after!.LastError);
        Assert.Equal("0xagg", after.AggregatorAddress);
    }

    [Fact]
    public async Task FilterAlreadyPublishedAsync_ReturnsOnlyPending()
    {
        await _repo.UpsertDeployedAsync("0xa", "0x1", "0xm", 8, 60, DateTime.UtcNow, DateTime.UtcNow);
        await _repo.UpsertDeployedAsync("0xb", "0x2", "0xm", 8, 70, DateTime.UtcNow, DateTime.UtcNow);

        var pending = await _repo.FilterAlreadyPublishedAsync(new[] { "0xa", "0xb", "0xc" });
        Assert.Single(pending);
        Assert.Contains("0xc", pending);
    }

    [Fact]
    public async Task FilterAlreadyPublishedAsync_EmptyInput_ReturnsEmpty()
    {
        var pending = await _repo.FilterAlreadyPublishedAsync(Array.Empty<string>());
        Assert.Empty(pending);
    }

    [Fact]
    public async Task CountPublishedAsync_ExcludesErrorOnlyRows()
    {
        await _repo.UpsertDeployedAsync("0xa", "0x1", "0xm", 8, 60, DateTime.UtcNow, DateTime.UtcNow);
        await _repo.RecordErrorAsync("0xb", "boom");

        Assert.Equal(1, await _repo.CountPublishedAsync());
    }

    [Fact]
    public async Task ListAllAsync_OrdersByDeployedAtDesc()
    {
        var earlier = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var later   = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc);
        await _repo.UpsertDeployedAsync("0xa", "0x1", "0xm", 8, 60, earlier, DateTime.UtcNow);
        await _repo.UpsertDeployedAsync("0xb", "0x2", "0xm", 8, 70, later,   DateTime.UtcNow);

        var rows = await _repo.ListAllAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("0xb", rows[0].AgentAddress); // most recent first
        Assert.Equal("0xa", rows[1].AgentAddress);
    }
}
