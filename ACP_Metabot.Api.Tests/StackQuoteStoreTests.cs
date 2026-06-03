using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ACP_Metabot.Api.Tests;

public class StackQuoteStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public StackQuoteStoreTests()
    {
        _dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"acp_stackquote_{System.Guid.NewGuid():N}.db");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
        }).Build();
        _db = new Db(cfg);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (System.IO.File.Exists(_dbPath)) System.IO.File.Delete(_dbPath);
    }

    private static StackPlanStep Step(string agent, decimal price) =>
        new(agent, "risk_snapshot", "risk", price, "low", new Dictionary<string, object> { ["agentAddress"] = "0xabc" });

    [Fact]
    public async Task Save_then_Load_roundtrips_the_plan()
    {
        var store = new StackQuoteStore(_db);
        var steps = new List<StackPlanStep> { Step("0x1111111111111111111111111111111111111111", 0.05m) };
        await store.SaveAsync("q1", "0xbuyer", "0xsubject", steps, 0.05m, 0.25m,
            DateTime.UtcNow.AddMinutes(15), default);

        var loaded = await store.LoadAsync("q1", default);

        Assert.NotNull(loaded);
        Assert.Equal("0xbuyer", loaded!.BuyerKey);
        Assert.Equal("0xsubject", loaded.Subject);
        Assert.Single(loaded.Steps);
        Assert.Equal(0.05m, loaded.Steps[0].QuotedPriceUsdc);
        Assert.Equal("risk_snapshot", loaded.Steps[0].TargetOffering);
    }

    [Fact]
    public async Task LoadActive_returns_null_when_expired()
    {
        var store = new StackQuoteStore(_db);
        await store.SaveAsync("q2", "0xbuyer", "0xsubject",
            new List<StackPlanStep> { Step("0x2222222222222222222222222222222222222222", 0.05m) },
            0.05m, 0.25m, DateTime.UtcNow.AddMinutes(-1), default);

        var loaded = await store.LoadActiveAsync("q2", DateTime.UtcNow, default);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Load_returns_null_for_unknown_quote()
        => Assert.Null(await new StackQuoteStore(_db).LoadAsync("nope", default));
}
