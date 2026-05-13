using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

// v1.7.4: the today/digest result now includes a newResources block of free
// V2 Resources first-seen within the window. Backed by AgentResourcesRepository.
public class DigestServiceNewResourcesTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _offerings;
    private readonly AgentResourcesRepository _resources;
    private readonly DigestService _svc;

    public DigestServiceNewResourcesTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_digest_resources_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();

        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _offerings = new OfferingRepository(_db);
        _resources = new AgentResourcesRepository(_db);

        var satCalc = new SaturationCalculator(threshold: 0.85);
        var repSvc = new ReputationService(
            new AgentReputationCacheRepository(_db),
            new AgentReputationHistoryRepository(_db),
            new LifetimeSnapshotRepository(_db),
            null!, null!, new ScoreCalculator(), _offerings,
            NullLogger<ReputationService>.Instance);

        _svc = new DigestService(_offerings, repSvc, satCalc, _resources);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task IncludesResourcesFirstSeenInWindow()
    {
        // Three Resources upserted now → all first_seen_at ≈ DateTime.UtcNow.
        await _resources.UpsertManyForAgentAsync(
            "0xaaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1aaa1",
            "AgentA",
            "v2",
            new[]
            {
                ("res1", "https://api.example/res1", (string?)null, "First resource"),
                ("res2", "https://api.example/res2", (string?)null, "Second resource"),
                ("res3", "https://api.example/res3", (string?)null, "Third resource"),
            });

        var result = await _svc.BuildAsync(windowDays: 1, marketplaceFilter: null);

        Assert.Equal(3, result.NewResources.Count);
        Assert.All(result.NewResources, r => Assert.Equal("v2", r.MarketplaceVersion));
        Assert.Contains(result.NewResources, r => r.Name == "res1");
        Assert.Contains(result.NewResources, r => r.Name == "res2");
        Assert.Contains(result.NewResources, r => r.Name == "res3");
    }

    [Fact]
    public async Task ReturnsEmptyWhenMarketplaceFilterIsV1()
    {
        await _resources.UpsertManyForAgentAsync(
            "0xbbb1bbb1bbb1bbb1bbb1bbb1bbb1bbb1bbb1bbb1",
            "AgentB",
            "v2",
            new[] { ("res", "https://api.example/res", (string?)null, "desc") });

        // marketplace=v1 means V1-only context. Resources are V2-only, so the block is empty.
        var result = await _svc.BuildAsync(windowDays: 1, marketplaceFilter: "v1");

        Assert.Empty(result.NewResources);
    }

    [Fact]
    public async Task IncludesResourcesWhenMarketplaceFilterIsV2()
    {
        await _resources.UpsertManyForAgentAsync(
            "0xccc1ccc1ccc1ccc1ccc1ccc1ccc1ccc1ccc1ccc1",
            "AgentC",
            "v2",
            new[] { ("only", "https://api.example/only", (string?)null, "desc") });

        var result = await _svc.BuildAsync(windowDays: 1, marketplaceFilter: "v2");

        Assert.Single(result.NewResources);
        Assert.Equal("only", result.NewResources[0].Name);
    }

    [Fact]
    public async Task EmptyArrayWhenNoResourcesUpserted()
    {
        var result = await _svc.BuildAsync(windowDays: 7, marketplaceFilter: null);

        Assert.NotNull(result.NewResources);
        Assert.Empty(result.NewResources);
    }

    [Fact]
    public async Task ListNewSinceAsync_FiltersByCutoff()
    {
        // Insert a Resource with an explicit old first_seen_at by writing the row directly.
        await using (var conn = _db.OpenConnection())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO agent_resources
                    (agent_address, agent_name, name, url, params_json, description,
                     marketplace_version, first_seen_at, last_seen_at)
                VALUES ('0xddd1ddd1ddd1ddd1ddd1ddd1ddd1ddd1ddd1ddd1','OldAgent','old',
                        'https://api.example/old', NULL, 'old desc',
                        'v2', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');";
            await cmd.ExecuteNonQueryAsync();
        }

        // Now insert a fresh one.
        await _resources.UpsertManyForAgentAsync(
            "0xeee1eee1eee1eee1eee1eee1eee1eee1eee1eee1",
            "FreshAgent",
            "v2",
            new[] { ("fresh", "https://api.example/fresh", (string?)null, "fresh desc") });

        // 1-day window from now should only include the fresh one.
        var rows = await _resources.ListNewSinceAsync(
            DateTime.UtcNow.AddDays(-1), marketplaceFilter: null, limit: 100);

        Assert.Single(rows);
        Assert.Equal("fresh", rows[0].Name);
    }
}
