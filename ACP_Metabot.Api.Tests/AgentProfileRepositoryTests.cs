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
}
