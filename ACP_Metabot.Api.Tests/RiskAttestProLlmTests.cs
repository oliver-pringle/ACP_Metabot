using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// v1.0 riskAttestPro Task 5: Haiku narration with $0.50/day cap + deterministic
/// cache by componentsHash. Mirrors RiskTrajectoryStoreTests temp-DB scaffolding
/// so the xunit parallel matrix doesn't collide on a shared file.
/// </summary>
public class RiskAttestProLlmTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public RiskAttestProLlmTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_llm_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private RiskAttestProLlm MakeLlm(Func<string, Task<(string, decimal)>>? injector = null) =>
        new RiskAttestProLlm(_db, NullLogger<RiskAttestProLlm>.Instance, injector);

    [Fact]
    public async Task Same_componentsHash_returns_cached_narration_without_burning_budget()
    {
        int callCount = 0;
        Func<string, Task<(string, decimal)>> injector = async _ =>
        {
            callCount++;
            await Task.Yield();
            return ("narration text", 0.005m);
        };
        var llm = MakeLlm(injector);

        var first = await llm.NarrateAsync("hashA", "{\"hf\":80}", "OK", 72);
        var second = await llm.NarrateAsync("hashA", "{\"hf\":80}", "OK", 72);

        Assert.Equal("narration text", first);
        Assert.Equal("narration text", second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Budget_cap_hit_returns_fallback_template()
    {
        // Pre-fill today's spend row above the $0.50 cap.
        await using (var conn = _db.OpenConnection())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO risk_attest_pro_spend (day_utc, llm_calls, llm_cost_usd)
                VALUES (strftime('%Y-%m-%d','now'), 100, 0.60);";
            await cmd.ExecuteNonQueryAsync();
        }

        int callCount = 0;
        Func<string, Task<(string, decimal)>> injector = async _ =>
        {
            callCount++;
            await Task.Yield();
            return ("should never see this", 0.001m);
        };
        var llm = MakeLlm(injector);

        var result = await llm.NarrateAsync("hashCap", "{}", "OK", 72);

        Assert.Contains("Verdict:", result);
        Assert.Contains("score 72", result);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task Successful_call_updates_spend_table_atomically()
    {
        Func<string, Task<(string, decimal)>> injector = async _ =>
        {
            await Task.Yield();
            return ("narration", 0.006m);
        };
        var llm = MakeLlm(injector);

        await llm.NarrateAsync("hashSpend", "{}", "OK", 50);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT llm_calls, llm_cost_usd FROM risk_attest_pro_spend WHERE day_utc = strftime('%Y-%m-%d','now');";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var calls = reader.GetInt32(0);
        var cost = reader.GetDecimal(1);
        Assert.Equal(1, calls);
        Assert.InRange(cost, 0.0059m, 0.0061m);
    }

    [Fact]
    public async Task Different_componentsHash_makes_separate_calls()
    {
        int callCount = 0;
        Func<string, Task<(string, decimal)>> injector = async _ =>
        {
            callCount++;
            await Task.Yield();
            return ("narration-" + callCount, 0.003m);
        };
        var llm = MakeLlm(injector);

        var a = await llm.NarrateAsync("hashA", "{\"a\":1}", "OK", 70);
        var b = await llm.NarrateAsync("hashB", "{\"b\":2}", "OK", 70);

        Assert.NotEqual(a, b);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Cache_persists_across_instances_via_db()
    {
        int callCount = 0;
        Func<string, Task<(string, decimal)>> injector = async _ =>
        {
            callCount++;
            await Task.Yield();
            return ("persistent narration", 0.004m);
        };

        var llmA = MakeLlm(injector);
        var first = await llmA.NarrateAsync("hashC", "{}", "OK", 60);

        var llmB = MakeLlm(injector);
        var second = await llmB.NarrateAsync("hashC", "{}", "OK", 60);

        Assert.Equal("persistent narration", first);
        Assert.Equal("persistent narration", second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Fallback_template_explains_each_component_status()
    {
        // Force cap-hit path so the deterministic fallback is exercised.
        await using (var conn = _db.OpenConnection())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO risk_attest_pro_spend (day_utc, llm_calls, llm_cost_usd)
                VALUES (strftime('%Y-%m-%d','now'), 50, 0.55);";
            await cmd.ExecuteNonQueryAsync();
        }

        var llm = MakeLlm();

        var componentsJson = """
            {
              "healthFactor": {"score":85,"source":"LiquidGuard","status":"fresh"},
              "approvals":    {"score":60,"source":"RevokeBot","status":"fresh"},
              "mev":          {"score":50,"source":"MEVProtect","status":"unavailable","details":"503 from peer"},
              "reputation":   {"score":50,"source":"TheMetaBot","status":"fresh"},
              "arena":        {"score":50,"source":"TheArenaBot","status":"unavailable","details":"network"},
              "witness":      {"score":50,"source":"TheWitnessBot","status":"fresh"},
              "trajectory":   {"score":75,"source":"history","status":"fresh"}
            }
            """;

        var result = await llm.NarrateAsync("hashFb", componentsJson, "WATCH", 55);

        Assert.Contains("Verdict: WATCH", result);
        Assert.Contains("score 55", result);
        Assert.Contains("daily budget cap", result);
    }
}
