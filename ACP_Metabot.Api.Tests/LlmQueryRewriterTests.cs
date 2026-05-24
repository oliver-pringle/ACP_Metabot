using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class LlmQueryRewriterTests : IAsyncLifetime
{
    private string _dbPath = "";
    private Db _db = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"metabot-rw-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        await _db.InitializeSchemaAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    private LlmQueryRewriter Build(IClaudeClient claude, double dailyCap = 0.50)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Search:QueryRewriterDailyUsdCap"] = dailyCap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Search:QueryRewriterMaxUsdPerCall"] = "0.002"
            }).Build();
        return new LlmQueryRewriter(claude, _db, config, NullLogger<LlmQueryRewriter>.Instance);
    }

    private sealed class FakeClaudeClient : IClaudeClient
    {
        public string? FixedResponse { get; init; }
        public Exception? ThrowsOnCall { get; init; }
        public int CallCount { get; private set; }
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt,
            int maxTokens, CancellationToken ct)
        {
            CallCount++;
            if (ThrowsOnCall is not null) throw ThrowsOnCall;
            return Task.FromResult(FixedResponse ?? "{\"intent\":\"other\",\"synonyms\":[]}");
        }
    }

    [Fact]
    public async Task RewriteAsync_returns_passthrough_when_daily_cap_breached()
    {
        await using (var conn = _db.OpenConnection())
        await using (var ins = conn.CreateCommand())
        {
            ins.CommandText = @"
                INSERT INTO query_rewrite_spend(spent_at, usd_amount, query_hash, rewriter_model)
                VALUES (datetime('now'), 0.50, 'hash', 'test');";
            await ins.ExecuteNonQueryAsync();
        }
        var claude = new FakeClaudeClient { FixedResponse = "{\"intent\":\"defi\",\"synonyms\":[\"x\"]}" };
        var r = Build(claude);

        var result = await r.RewriteAsync("watch HF on aave", CancellationToken.None);

        Assert.Equal("passthrough", result.Intent);
        Assert.Empty(result.Synonyms);
        Assert.Equal(0, claude.CallCount);
    }

    [Fact]
    public async Task RewriteAsync_parses_valid_LLM_response()
    {
        var claude = new FakeClaudeClient
        {
            FixedResponse = "{\"intent\":\"defi\",\"synonyms\":[\"aave lending\",\"compound supply\"]}"
        };
        var r = Build(claude);

        var result = await r.RewriteAsync("watch HF on aave", CancellationToken.None);

        Assert.Equal("defi", result.Intent);
        Assert.Equal(new[] { "aave lending", "compound supply" }, result.Synonyms);
        Assert.Equal(1, claude.CallCount);
    }

    [Fact]
    public async Task RewriteAsync_returns_passthrough_on_LLM_failure()
    {
        var claude = new FakeClaudeClient { ThrowsOnCall = new InvalidOperationException("boom") };
        var r = Build(claude);

        var result = await r.RewriteAsync("any query", CancellationToken.None);

        Assert.Equal("passthrough", result.Intent);
        Assert.Empty(result.Synonyms);
    }

    [Fact]
    public async Task RewriteAsync_records_spend_after_successful_call()
    {
        var claude = new FakeClaudeClient
        {
            FixedResponse = "{\"intent\":\"oracle\",\"synonyms\":[\"price feed\"]}"
        };
        var r = Build(claude);

        var before = await r.GetDailySpendUsdAsync(CancellationToken.None);
        await r.RewriteAsync("oracle drift", CancellationToken.None);
        var after = await r.GetDailySpendUsdAsync(CancellationToken.None);

        Assert.True(after > before, $"daily spend should have increased; before={before}, after={after}");
    }
}
