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

    // ── BackfillFromOfferingsAsync ─────────────────────────────────────────

    [Fact]
    public async Task BackfillFromOfferings_PopulatesEveryDistinctAgent()
    {
        // Seed offerings table directly with two agents, three offerings total.
        await using (var conn = _db.OpenConnection())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                    price_usdc, price_type, chain, content_hash, first_seen_at, last_seen_at,
                    marketplace_version, is_removed)
                VALUES
                    ('0xa', 'AgentA', 'scan', 'Scan a wallet', 0.10, 'per_call', 'base', 'h1', $f, $f, 'v2', 0),
                    ('0xa', 'AgentA', 'audit', 'Deep audit', 5.00, 'per_call', 'base', 'h2', $f, $f, 'v2', 0),
                    ('0xb', 'AgentB', 'alert', 'Alert on tx', 0.20, 'per_call', 'base', 'h3', $f, $f, 'v1', 0);";
            cmd.Parameters.AddWithValue("$f", "2026-05-01T00:00:00Z");
            await cmd.ExecuteNonQueryAsync();
        }

        var n = await _repo.BackfillFromOfferingsAsync(profileTextCap: 2000);
        Assert.Equal(2, n);

        var a = await _repo.GetAsync("0xa");
        Assert.NotNull(a);
        Assert.Equal("AgentA", a.AgentName);
        Assert.Contains("scan", a.ProfileText);
        Assert.Contains("audit", a.ProfileText);
        Assert.Null(a.EmbeddedAt);

        var b = await _repo.GetAsync("0xb");
        Assert.NotNull(b);
        Assert.Contains("alert", b.ProfileText);

        Assert.Equal(2, (await _repo.ListDirtyAsync(100)).Count);
    }

    [Fact]
    public async Task BackfillFromOfferings_SkipsTombstoned()
    {
        await using (var conn = _db.OpenConnection())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                    price_usdc, price_type, chain, content_hash, first_seen_at, last_seen_at,
                    marketplace_version, is_removed)
                VALUES
                    ('0xa', 'AgentA', 'old',  'tombstoned', 0.10, 'per_call', 'base', 'h1', $f, $f, 'v2', 1),
                    ('0xa', 'AgentA', 'live', 'active',     0.10, 'per_call', 'base', 'h2', $f, $f, 'v2', 0);";
            cmd.Parameters.AddWithValue("$f", "2026-05-01T00:00:00Z");
            await cmd.ExecuteNonQueryAsync();
        }

        var n = await _repo.BackfillFromOfferingsAsync(profileTextCap: 2000);
        Assert.Equal(1, n);

        var a = await _repo.GetAsync("0xa");
        Assert.NotNull(a);
        Assert.DoesNotContain("tombstoned", a.ProfileText);
        Assert.Contains("active", a.ProfileText);
    }

    [Fact]
    public async Task BackfillFromOfferings_RespectsCap()
    {
        var longDesc = new string('x', 5000);
        await using (var conn = _db.OpenConnection())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                    price_usdc, price_type, chain, content_hash, first_seen_at, last_seen_at,
                    marketplace_version, is_removed)
                VALUES ('0xa', 'A', 'big', $d, 0.10, 'per_call', 'base', 'h1', $f, $f, 'v2', 0)";
            cmd.Parameters.AddWithValue("$d", longDesc);
            cmd.Parameters.AddWithValue("$f", "2026-05-01T00:00:00Z");
            await cmd.ExecuteNonQueryAsync();
        }

        await _repo.BackfillFromOfferingsAsync(profileTextCap: 500);
        var a = await _repo.GetAsync("0xa");
        Assert.NotNull(a);
        Assert.True(a.ProfileText.Length <= 500);
    }

    [Fact]
    public async Task BackfillFromOfferings_IsIdempotent()
    {
        await using (var conn = _db.OpenConnection())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                    price_usdc, price_type, chain, content_hash, first_seen_at, last_seen_at,
                    marketplace_version, is_removed)
                VALUES ('0xa', 'AgentA', 'scan', 'Scan', 0.10, 'per_call', 'base', 'h', $f, $f, 'v2', 0);";
            cmd.Parameters.AddWithValue("$f", "2026-05-01T00:00:00Z");
            await cmd.ExecuteNonQueryAsync();
        }

        var n1 = await _repo.BackfillFromOfferingsAsync(2000);
        var n2 = await _repo.BackfillFromOfferingsAsync(2000);
        Assert.Equal(1, n1);
        Assert.Equal(1, n2);  // re-run produces the same row count (UPSERT)

        Assert.Single(await _repo.ListDirtyAsync(100));  // exactly one agent_profiles row
    }
}
