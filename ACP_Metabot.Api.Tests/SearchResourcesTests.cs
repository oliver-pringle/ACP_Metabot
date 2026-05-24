using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.10 Phase 1 (T6): exercises <see cref="AgentResourcesRepository.SearchHybridAsync"/>.
/// The FTS5 path is the production hot path (resources_fts mirror is
/// trigger-maintained); a populated table proves the path is wired correctly
/// and that the rank-ordered MATCH returns the expected row.
/// </summary>
public class SearchResourcesTests : IAsyncLifetime
{
    private string _dbPath = "";
    private Db _db = null!;
    private AgentResourcesRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"metabot-rs-{Guid.NewGuid():N}.db");
        // Matches the canonical pattern used by DbMigrationTests — Db reads
        // ConnectionStrings:Sqlite, not ConnectionStrings:Default.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            })
            .Build();
        _db = new Db(config);
        await _db.InitializeSchemaAsync();
        _repo = new AgentResourcesRepository(_db);

        // Seed one matching + one non-matching resource. The matching row's
        // description contains the literal token "drift" so FTS5 unicode61
        // tokenisation surfaces it under the "drift" query.
        await _repo.UpsertManyForAgentAsync(
            agentAddress: "0x" + new string('a', 40),
            agentName: "OracleBot",
            marketplaceVersion: "v2",
            resources: new[]
            {
                ("driftWindow", "https://example/d", (string?)null,
                    "Returns recent cross-source price drift incidents"),
                ("randomThing", "https://example/r", (string?)null,
                    "Unrelated description"),
            });
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* ignore — test cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SearchHybrid_returns_FTS_matches_when_table_populated()
    {
        var hits = await _repo.SearchHybridAsync("drift", limit: 5, marketplaceFilter: null);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Name == "driftWindow");
        // FTS5 should NOT surface the unrelated row for this query.
        Assert.DoesNotContain(hits, h => h.Name == "randomThing");
    }

    [Fact]
    public async Task SearchHybrid_returns_empty_when_query_doesnt_match()
    {
        var hits = await _repo.SearchHybridAsync("unrelatedqueryxyzzy", limit: 5,
            marketplaceFilter: null);
        Assert.Empty(hits);
    }
}
