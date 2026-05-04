using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class AgentSearchServiceTests
{
    [Fact]
    public void RrfFusion_AgreementBoosts()
    {
        // Both rankers agree 0xa is best, but put 0xb and 0xc in opposite order.
        // 0xa should win because it collects a high score from both legs.
        var bm25  = new[] { "0xa", "0xb", "0xc" };
        var dense = new[] { "0xa", "0xc", "0xb" };

        var fused = AgentSearchService.ReciprocalRankFusion(bm25, dense, k: 60);

        var ranked = fused.OrderByDescending(kv => kv.Value)
                          .Select(kv => kv.Key)
                          .ToList();
        Assert.Equal("0xa", ranked[0]);
    }

    [Fact]
    public void RrfFusion_MissingFromOneRanker_StillRanks()
    {
        // 0xc only appears in dense, 0xa and 0xb only in BM25.
        // All three should appear in the fused map.
        var bm25  = new[] { "0xa", "0xb" };
        var dense = new[] { "0xc" };

        var fused = AgentSearchService.ReciprocalRankFusion(bm25, dense, k: 60);

        Assert.Contains("0xa", fused.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("0xb", fused.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("0xc", fused.Keys, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Integration-shaped test for <see cref="AgentSearchService.SearchAsync"/>.
/// Uses a real in-memory SQLite DB, a stub embedder, and real repo/service
/// instances with null-safe wiring for heavy deps (chain scanner / off-chain
/// client) that are never invoked during a cache-miss <c>GetCachedAsync</c>.
/// </summary>
public class AgentSearchServiceIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _offerings;
    private readonly AgentProfileRepository _profiles;
    private readonly AgentReputationCacheRepository _reputationCache;
    private readonly AgentReputationHistoryRepository _reputationHistory;
    private readonly LifetimeSnapshotRepository _snapshot;
    private readonly ReputationService _reputation;
    private readonly CrossPresenceBuilder _crossPresence;
    private readonly AgentSearchService _svc;

    public AgentSearchServiceIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_agentsearch_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();

        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();

        _offerings         = new OfferingRepository(_db);
        _profiles          = new AgentProfileRepository(_db);
        _reputationCache   = new AgentReputationCacheRepository(_db);
        _reputationHistory = new AgentReputationHistoryRepository(_db);
        _snapshot          = new LifetimeSnapshotRepository(_db);

        // ReputationService: only GetCachedAsync (DB cache lookup) is exercised
        // here. ChainEventScanner / AcpOffChainClient / ScoreCalculator are
        // never called when the cache is empty — pass null! safely.
        _reputation = new ReputationService(
            _reputationCache,
            _reputationHistory,
            _snapshot,
            null!,  // ChainEventScanner — not called on cache-miss
            null!,  // AcpOffChainClient — not called on cache-miss
            new ScoreCalculator(),
            _offerings,
            NullLogger<ReputationService>.Instance);

        _crossPresence = new CrossPresenceBuilder(_offerings);

        _svc = new AgentSearchService(
            _db,
            _offerings,
            new StubEmbedderForSearch(),
            _reputation,
            _crossPresence,
            rerank: null);  // No reranker — falls back to RRF order.
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task SeedOfferingAsync(
        string addr, string agentName, string offeringName,
        string marketplace, double priceUsdc = 0.10)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings
                (agent_address, agent_name, offering_name, description,
                 price_usdc, price_type, chain, content_hash,
                 first_seen_at, last_seen_at, marketplace_version, is_removed)
            VALUES
                ($a, $n, $o, $d, $p, 'per_call', 'base', $h,
                 '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', $m, 0)";
        cmd.Parameters.AddWithValue("$a", addr.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$n", agentName);
        cmd.Parameters.AddWithValue("$o", offeringName);
        cmd.Parameters.AddWithValue("$d", $"{agentName} {offeringName} defi swap wallet");
        cmd.Parameters.AddWithValue("$p", priceUsdc);
        cmd.Parameters.AddWithValue("$h", $"h_{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("$m", marketplace);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedProfileEmbeddingAsync(string addr, float[] vec)
    {
        // Upsert the profile row then stamp the embedding blob.
        await _profiles.UpsertAsync(addr, $"agent_{addr}", $"profile text {addr}");
        var bytes = new byte[vec.Length * sizeof(float)];
        Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        await _profiles.MarkEmbeddedAsync(addr, "voyage-stub", bytes);
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_ReturnsHitsWithV17Fields()
    {
        const string addr1 = "0x0000000000000000000000000000000000000001";
        const string addr2 = "0x0000000000000000000000000000000000000002";

        // Seed two agents with offerings that contain the query term.
        await SeedOfferingAsync(addr1, "AlphaBot",   "defi_swap",    "v1", 0.50);
        await SeedOfferingAsync(addr1, "AlphaBot",   "defi_analyze", "v2", 1.00);
        await SeedOfferingAsync(addr2, "BetaProtocol", "wallet_scan", "v2", 0.10);

        // Seed an embedding for addr1 so the dense leg can match.
        await SeedProfileEmbeddingAsync(addr1, new float[] { 1f, 0f, 0f });

        var hits = await _svc.SearchAsync("defi", limit: 5, marketplaceFilter: null, CancellationToken.None);

        // At least addr1 must appear (BM25 on "defi" + dense leg has its vec).
        Assert.NotEmpty(hits);
        var hit1 = hits.FirstOrDefault(h =>
            string.Equals(h.AgentAddress, addr1, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(hit1);

        // v1.7 fields: Marketplaces must be non-empty and contain "v1" and "v2".
        Assert.NotEmpty(hit1!.Marketplaces);
        Assert.Contains("v1", hit1.Marketplaces);
        Assert.Contains("v2", hit1.Marketplaces);

        // DominantMarketplace must be set (not null or empty).
        Assert.False(string.IsNullOrEmpty(hit1.DominantMarketplace));

        // TopOfferings records must be populated with real price+version data.
        Assert.NotEmpty(hit1.TopOfferings);
        Assert.All(hit1.TopOfferings, o =>
        {
            Assert.False(string.IsNullOrEmpty(o.OfferingName));
            // MarketplaceVersion must be "v1" or "v2" (not the empty "" stub).
            Assert.True(o.MarketplaceVersion is "v1" or "v2");
        });

        // TopOfferingNames is the backward-compat mirror.
        Assert.NotEmpty(hit1.TopOfferingNames);
        Assert.Equal(
            hit1.TopOfferings.Select(o => o.OfferingName).OrderBy(x => x),
            hit1.TopOfferingNames.OrderBy(x => x));

        // AgentScore is null when the reputation cache is empty — that's fine.
        // Just assert the field exists on the model (type-level check).
        _ = hit1.AgentScore;
    }

    [Fact]
    public async Task SearchAsync_MarketplaceFilter_FiltersToV2Only()
    {
        const string addr = "0x0000000000000000000000000000000000000003";
        await SeedOfferingAsync(addr, "GammaAgent", "swap_exec",   "v1");
        await SeedOfferingAsync(addr, "GammaAgent", "swap_report", "v2");

        var hitsV2 = await _svc.SearchAsync("swap", limit: 5, marketplaceFilter: "v2", CancellationToken.None);

        // The agent surfaces because it has a v2 offering matching "swap".
        var hit = hitsV2.FirstOrDefault(h =>
            string.Equals(h.AgentAddress, addr, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(hit);

        // Every TopOffering returned must be v2 when filter is "v2".
        Assert.All(hit!.TopOfferings, o => Assert.Equal("v2", o.MarketplaceVersion));
    }

    [Fact]
    public async Task SearchAsync_EmptyCorpus_ReturnsEmpty()
    {
        // No seeded data — both legs yield nothing; result must be empty.
        var hits = await _svc.SearchAsync("liquidation", limit: 5, marketplaceFilter: null, CancellationToken.None);
        Assert.Empty(hits);
    }
}

/// <summary>Stub embedder for AgentSearchService tests — always returns a fixed unit vector.</summary>
internal class StubEmbedderForSearch : IEmbeddingProvider
{
    public string ModelId  => "voyage-stub-search";
    public int    Dimension => 3;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        // Deterministic: every text gets the same unit vector [1, 0, 0].
        // This means the dense leg cosine-ranks any stored [1,0,0] embedding at 1.0.
        var vecs = texts.Select(_ => new float[] { 1f, 0f, 0f }).ToArray();
        return Task.FromResult<IReadOnlyList<float[]>>(vecs);
    }
}
