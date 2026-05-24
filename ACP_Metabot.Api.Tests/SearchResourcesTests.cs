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

    /// <summary>
    /// Phase 1 bug repro: the second UpsertManyForAgentAsync call hits the
    /// ON CONFLICT DO UPDATE path, which fires <c>trg_agent_resources_au</c>.
    /// Pre-fix that trigger threw "SQL logic error" because the FTS5 'delete'
    /// command was misaligned with the resources_fts vtable layout (it passed
    /// <c>old.id</c> in the <c>resource_id</c> column slot, but FTS5 'delete'
    /// expects the FTS-internal <c>rowid</c> in the SECOND column position,
    /// not an UNINDEXED user column). The bug was warn-only in production
    /// because <c>AcpV2MarketplaceSource</c> catches and logs, but it
    /// generated noisy log lines on every V2 sweep that updated an existing
    /// resource's description.
    ///
    /// This test additionally verifies that AFTER the update the FTS5 index
    /// reflects the NEW description and no longer matches the OLD one — i.e.
    /// that the trigger maintenance is correct, not just non-throwing.
    /// </summary>
    [Fact]
    public async Task UpsertManyForAgent_can_update_existing_row_description()
    {
        // First call: T1 in InitializeAsync inserted ("driftWindow", "...recent...drift incidents").
        // Now flip the description so the upsert collides on the
        // (marketplace_version, agent_address, name) unique key and updates
        // the description, firing trg_agent_resources_au.
        var addr = "0x" + new string('a', 40);
        await _repo.UpsertManyForAgentAsync(
            agentAddress: addr,
            agentName: "OracleBot",
            marketplaceVersion: "v2",
            resources: new[]
            {
                ("driftWindow", "https://example/d", (string?)null,
                    "Returns recent oracle peg spreads across sources"),
            });

        // FTS index now reflects "peg" not "drift".
        var pegHits = await _repo.SearchHybridAsync("peg", limit: 5, marketplaceFilter: null);
        Assert.Contains(pegHits, h => h.Name == "driftWindow");

        // And the old token no longer matches THIS resource's body — but the
        // SECOND seed row from InitializeAsync ("randomThing") also doesn't
        // contain "drift" either, so we look specifically at name=driftWindow.
        // The description was rewritten; "drift" should no longer match it.
        var driftHits = await _repo.SearchHybridAsync("drift", limit: 5, marketplaceFilter: null);
        Assert.DoesNotContain(driftHits, h => h.Name == "driftWindow");
    }
}
