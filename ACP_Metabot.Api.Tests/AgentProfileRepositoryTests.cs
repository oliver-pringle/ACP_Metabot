using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class AgentProfileRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly AgentProfileRepository _repo;

    public AgentProfileRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_agentprofile_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new AgentProfileRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task UpsertAsync_InsertsNewRow_WithDirtyFlag()
    {
        await _repo.UpsertAsync("0xABC", "AgentA", "profile text A");
        var dirty = await _repo.ListDirtyAsync(limit: 100);
        Assert.Single(dirty);
        Assert.Equal("0xabc", dirty[0].AgentAddress); // lowercased
        Assert.Equal("AgentA", dirty[0].AgentName);
        Assert.Equal("profile text A", dirty[0].ProfileText);
        Assert.Null(dirty[0].EmbeddedAt);
    }

    [Fact]
    public async Task UpsertAsync_UpdatesExistingRow_BumpsLastChangeAt()
    {
        await _repo.UpsertAsync("0xabc", "AgentA", "v1");
        await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1, 2, 3 });
        var beforeDirty = await _repo.ListDirtyAsync(100);
        Assert.Empty(beforeDirty);

        await Task.Delay(20); // ensure last_change_at strictly newer
        await _repo.UpsertAsync("0xabc", "AgentA", "v2 changed");

        var afterDirty = await _repo.ListDirtyAsync(100);
        Assert.Single(afterDirty);
        Assert.Equal("v2 changed", afterDirty[0].ProfileText);
    }

    [Fact]
    public async Task MarkEmbeddedAsync_ClearsDirty()
    {
        await _repo.UpsertAsync("0xabc", "A", "p");
        Assert.Single(await _repo.ListDirtyAsync(100));

        await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1, 2, 3 });
        Assert.Empty(await _repo.ListDirtyAsync(100));
    }

    [Fact]
    public async Task BumpLastChangeAtAsync_NoOpIfMissing()
    {
        await _repo.BumpLastChangeAtAsync("0xnonexistent");
        Assert.Empty(await _repo.ListDirtyAsync(100));
    }

    [Fact]
    public async Task BumpLastChangeAtAsync_MakesExistingRowDirtyAgain()
    {
        await _repo.UpsertAsync("0xabc", "A", "p");
        await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1 });
        Assert.Empty(await _repo.ListDirtyAsync(100));

        await Task.Delay(20);
        await _repo.BumpLastChangeAtAsync("0xabc");
        Assert.Single(await _repo.ListDirtyAsync(100));
    }

    // ── OfferingRepository integration: dirty-flag wiring ──────────────────

    [Fact]
    public async Task OfferingUpsert_BumpsAgentProfileDirtyFlag_OnNewOffering()
    {
        // Pre-condition: agent profile exists and is clean.
        await _repo.UpsertAsync("0xabc", "AgentA", "profile A");
        await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1 });
        Assert.Empty(await _repo.ListDirtyAsync(100));

        // Indexer ingests a new offering for that agent through OfferingRepository.
        var offeringRepo = new OfferingRepository(_db, _repo);
        await Task.Delay(20);
        await offeringRepo.UpsertManyAsync(new[]
        {
            new UpsertItem(
                AgentAddress: "0xabc",
                AgentName: "AgentA",
                OfferingName: "my_offering",
                Description: "does stuff",
                RequirementSchemaJson: null,
                PriceUsdc: 0.99,
                PriceType: "flat",
                IsPrivate: false,
                Chain: "base",
                ContentHash: "hash1",
                UsageCount: 0,
                AgentJobCount: 0)
        }, DateTime.UtcNow);

        Assert.Single(await _repo.ListDirtyAsync(100));
    }

    [Fact]
    public async Task OfferingUpsert_DoesNotBump_OnPureTouchUpdate()
    {
        // Pre-condition: profile exists and is clean; offering already in DB.
        await _repo.UpsertAsync("0xabc", "AgentA", "profile A");
        var offeringRepo = new OfferingRepository(_db, _repo);
        var item = new UpsertItem(
            AgentAddress: "0xabc",
            AgentName: "AgentA",
            OfferingName: "my_offering",
            Description: "does stuff",
            RequirementSchemaJson: null,
            PriceUsdc: 0.99,
            PriceType: "flat",
            IsPrivate: false,
            Chain: "base",
            ContentHash: "hash1",
            UsageCount: 5,
            AgentJobCount: 1);
        // First upsert inserts the offering and bumps.
        await offeringRepo.UpsertManyAsync(new[] { item }, DateTime.UtcNow);
        // Now mark the profile clean.
        await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1 });
        Assert.Empty(await _repo.ListDirtyAsync(100));

        await Task.Delay(20);
        // Second upsert: same content_hash, only usage_count changed → pure touch, no bump.
        var touch = item with { UsageCount = 10, AgentJobCount = 2 };
        await offeringRepo.UpsertManyAsync(new[] { touch }, DateTime.UtcNow);

        Assert.Empty(await _repo.ListDirtyAsync(100));
    }
}
