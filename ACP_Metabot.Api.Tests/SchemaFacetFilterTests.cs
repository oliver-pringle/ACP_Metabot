using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.10 Phase 2 T4 — exercises OfferingRepository.GetOfferingIdsByFacetAsync,
/// the read-side query that backs the requiresField / producesField filters on
/// /v1/search. The write side (UpsertAsync → WriteSchemaFacetsAsync) is covered
/// by SchemaFacetExtractorTests + DbMigrationTests' backfill assertions; this
/// file focuses on the lookup query's contract:
///   - (field_name, role) returns matching offering ids
///   - field_name match is case-insensitive (rows are stored lowercased)
/// </summary>
public class SchemaFacetFilterTests : IAsyncLifetime
{
    private string _dbPath = "";
    private Db _db = null!;
    private OfferingRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_sf_filter_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        await _db.InitializeSchemaAsync();
        _repo = new OfferingRepository(_db);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    // Seed minimal offering + schema_facets rows directly. We bypass the
    // upsert path because that's covered by T3a's tests — this fixture
    // focuses on the read query in isolation.
    private async Task SeedOfferingAsync(long id, string contentHash)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings (id, agent_address, agent_name, offering_name, description,
                price_usdc, price_type, chain, content_hash,
                first_seen_at, last_seen_at, marketplace_version, is_removed)
            VALUES ($id, $a, 'TestAgent', $on, 'desc', 0.10, 'per_call', 'base', $h,
                    '2026-01-01T00:00:00Z', '2026-01-02T00:00:00Z', 'v2', 0);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$a", $"0x{id:x40}");
        cmd.Parameters.AddWithValue("$on", $"offering_{id}");
        cmd.Parameters.AddWithValue("$h", contentHash);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedFacetAsync(long offeringId, string fieldNameLower, string role)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO schema_facets(offering_id, field_name, role)
            VALUES ($id, $name, $role);";
        cmd.Parameters.AddWithValue("$id", offeringId);
        cmd.Parameters.AddWithValue("$name", fieldNameLower);
        cmd.Parameters.AddWithValue("$role", role);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task GetOfferingIdsByFacet_returns_matching_offerings_for_requirement()
    {
        await SeedOfferingAsync(1, "h1");
        await SeedOfferingAsync(2, "h2");
        await SeedFacetAsync(1, "tokensymbol", "requirement");

        var ids = await _repo.GetOfferingIdsByFacetAsync("tokenSymbol", "requirement");

        Assert.Contains(1L, ids);
        Assert.DoesNotContain(2L, ids);
    }

    [Fact]
    public async Task GetOfferingIdsByFacet_is_case_insensitive_on_field_name()
    {
        await SeedOfferingAsync(3, "h3");
        await SeedFacetAsync(3, "healthfactor", "deliverable");

        var ids1 = await _repo.GetOfferingIdsByFacetAsync("healthFactor", "deliverable");
        var ids2 = await _repo.GetOfferingIdsByFacetAsync("HEALTHFACTOR", "deliverable");

        Assert.Contains(3L, ids1);
        Assert.Contains(3L, ids2);
    }
}
