using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class AgentProfileEmbedderServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly AgentProfileRepository _repo;
    private readonly StubEmbedder _embedder;
    private readonly AgentProfileEmbedderService _svc;

    public AgentProfileEmbedderServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"emb_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new AgentProfileRepository(_db);
        _embedder = new StubEmbedder();
        _svc = new AgentProfileEmbedderService(_repo, _embedder, NullLogger<AgentProfileEmbedderService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task DrainOnce_EmbedsAllDirty_MarksClean()
    {
        await _repo.UpsertAsync("0xa", "A", "profile A");
        await _repo.UpsertAsync("0xb", "B", "profile B");
        Assert.Equal(2, (await _repo.ListDirtyAsync(100)).Count);

        await _svc.DrainOnceAsync(batchSize: 128, default);

        Assert.Empty(await _repo.ListDirtyAsync(100));
        var a = await _repo.GetAsync("0xa");
        Assert.NotNull(a);
        Assert.NotNull(a.Embedding);
        Assert.Equal("voyage-stub", a.EmbeddingModel);
    }

    [Fact]
    public async Task DrainOnce_VoyageFailure_LeavesDirty()
    {
        _embedder.FailNext = true;
        await _repo.UpsertAsync("0xa", "A", "profile A");

        await _svc.DrainOnceAsync(batchSize: 128, default);

        Assert.Single(await _repo.ListDirtyAsync(100)); // still dirty for next cycle
    }

    [Fact]
    public async Task DrainOnce_EmptyQueue_NoOp()
    {
        await _svc.DrainOnceAsync(batchSize: 128, default); // does not throw
        Assert.Empty(await _repo.ListDirtyAsync(100));
    }
}

internal class StubEmbedder : IEmbeddingProvider
{
    public string ModelId => "voyage-stub";
    public int Dimension => 3;
    public bool FailNext { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("simulated voyage failure");
        }
        var vectors = texts.Select(t => new float[] { t.Length, 1f, 0f }).ToArray();
        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }
}
