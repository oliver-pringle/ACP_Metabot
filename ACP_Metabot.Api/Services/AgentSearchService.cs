using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// Hybrid agent search pipeline for v1.7.
/// Leg 1 — BM25 via OfferingRepository.SearchAgentsAsync (top 200).
/// Leg 2 — Dense cosine over agent_profiles.embedding (top 200).
/// Step 3 — RRF fusion (k=60).
/// Step 4 — Take top 50; rerank via IRerankProvider if available (falls back to RRF order).
/// Step 5 — Enrich top `limit`: proper TopOfferings records, cross-presence, agentScore.
/// </summary>
public class AgentSearchService
{
    private readonly Db _db;
    private readonly OfferingRepository _offeringRepo;
    private readonly IEmbeddingProvider _embed;
    private readonly IRerankProvider? _rerank;
    private readonly ReputationService _reputation;
    private readonly CrossPresenceBuilder _crossPresence;
    private readonly QueryExpander _expander;

    public AgentSearchService(
        Db db,
        OfferingRepository offeringRepo,
        IEmbeddingProvider embed,
        ReputationService reputation,
        CrossPresenceBuilder crossPresence,
        QueryExpander expander,
        IRerankProvider? rerank = null)
    {
        _db = db;
        _offeringRepo = offeringRepo;
        _embed = embed;
        _reputation = reputation;
        _crossPresence = crossPresence;
        _expander = expander;
        _rerank = rerank;
    }

    public async Task<IReadOnlyList<AgentSearchHit>> SearchAsync(
        string query, int limit, string? marketplaceFilter, CancellationToken ct)
    {
        // v1.10 Phase 1: glossary expansion. The dense leg still embeds the
        // verbatim user query — synonym-aware embeddings are deferred to
        // Phase 3. The BM25 (agent-grouped FTS) leg below OR-merges canonical
        // synonyms via per-term lookups, mirroring SearchService's pattern.
        // GlossaryHits is plumbed for T5's response-envelope surfacing.
        var expanded = _expander.Expand(query);

        // ── 1. BM25 leg ──────────────────────────────────────────────────────
        // When the glossary fired, run SearchAgentsAsync once per term
        // (primary + each canonical synonym) and union the agent hits by
        // address, keeping the entry with the best (highest) Score and
        // merging unique TopOfferingNames up to a cap of 3. Concatenating
        // tokens into a single FTS query would collapse into a phrase match
        // via SanitizeFtsQuery's quote-wrapping (same footgun as the
        // SearchService leg) — per-term + union avoids it. Cost is N+1 FTS
        // reads (typically N≤3 in Phase 1).
        IReadOnlyList<AgentSearchHit> bm25Hits;
        if (expanded.Synonyms.Count > 0)
        {
            var merged = new Dictionary<string, AgentSearchHit>(StringComparer.OrdinalIgnoreCase);
            var terms = new List<string> { query };
            terms.AddRange(expanded.Synonyms);
            foreach (var term in terms)
            {
                var hits = await _offeringRepo.SearchAgentsAsync(term, limit: 200, marketplaceFilter);
                foreach (var h in hits)
                {
                    if (!merged.TryGetValue(h.AgentAddress, out var prev))
                    {
                        merged[h.AgentAddress] = h;
                    }
                    else if (h.Score > prev.Score)
                    {
                        // Higher Score wins; union the top-offering names so the
                        // enrichment pass still sees synonym-surfaced offerings.
                        var unioned = prev.TopOfferingNames
                            .Concat(h.TopOfferingNames)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(3)
                            .ToArray();
                        merged[h.AgentAddress] = h with
                        {
                            TopOfferingNames = unioned,
                            TopOfferings = unioned
                                .Select(n => new AgentSearchHitOffering(n, 0.0, ""))
                                .ToArray(),
                        };
                    }
                    else
                    {
                        var unioned = prev.TopOfferingNames
                            .Concat(h.TopOfferingNames)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(3)
                            .ToArray();
                        merged[h.AgentAddress] = prev with
                        {
                            TopOfferingNames = unioned,
                            TopOfferings = unioned
                                .Select(n => new AgentSearchHitOffering(n, 0.0, ""))
                                .ToArray(),
                        };
                    }
                }
            }
            bm25Hits = merged.Values.OrderByDescending(h => h.Score).Take(200).ToList();
        }
        else
        {
            bm25Hits = await _offeringRepo.SearchAgentsAsync(query, limit: 200, marketplaceFilter);
        }

        // ── 2. Dense leg — embed query; cosine-rank agent_profiles.embedding ─
        var qVec = (await _embed.EmbedAsync(new[] { query }, ct))[0];
        var denseHits = await DenseAgentRankAsync(qVec, limit: 200, ct);

        // ── 3. RRF fusion ─────────────────────────────────────────────────────
        var bm25Order  = bm25Hits.Select(h => h.AgentAddress).ToArray();
        var denseOrder = denseHits.Select(d => d.AgentAddress).ToArray();
        var fused = ReciprocalRankFusion(bm25Order, denseOrder, k: 60);

        // ── 4. Top 50; optional rerank ─────────────────────────────────────────
        var top50 = fused.OrderByDescending(kv => kv.Value)
                         .Take(50)
                         .Select(kv => kv.Key)
                         .ToList();
        var rerankScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var reranked = top50;

        if (_rerank is not null && top50.Count > 0)
        {
            var bm25ByAddr = bm25Hits.ToDictionary(h => h.AgentAddress, StringComparer.OrdinalIgnoreCase);
            var docs = top50.Select(addr =>
            {
                if (bm25ByAddr.TryGetValue(addr, out var h))
                    return $"{h.AgentName}\n{string.Join("\n", h.TopOfferingNames ?? Array.Empty<string>())}";
                return addr;
            }).ToList();

            try
            {
                var ranking = await _rerank.RankAsync(query, docs, ct);
                reranked = ranking.Select(r => top50[r.Index]).ToList();
                foreach (var r in ranking)
                    rerankScores[top50[r.Index]] = r.Score;
            }
            catch
            {
                // Fall back to RRF order on rerank failure.
                for (int i = 0; i < top50.Count; i++)
                    rerankScores[top50[i]] = fused[top50[i]];
            }
        }
        else
        {
            for (int i = 0; i < top50.Count; i++)
                rerankScores[top50[i]] = fused[top50[i]];
        }

        // ── 5. Enrich top `limit` ─────────────────────────────────────────────
        var bm25ByAddrFinal = bm25Hits.ToDictionary(h => h.AgentAddress, StringComparer.OrdinalIgnoreCase);
        var topAddrs = reranked.Take(limit).ToList();
        var enriched = new List<AgentSearchHit>(topAddrs.Count);

        foreach (var addr in topAddrs)
        {
            if (!bm25ByAddrFinal.TryGetValue(addr, out var bm)) continue;

            var cp  = await _crossPresence.BuildAsync(addr);
            var rep = await _reputation.GetCachedAsync(addr);

            var topOfferingNames = bm.TopOfferingNames ?? Array.Empty<string>();
            var topOfferingRecords = await BuildTopOfferingRecordsAsync(addr, topOfferingNames);

            enriched.Add(new AgentSearchHit(
                AgentAddress: addr,
                AgentName: bm.AgentName,
                Score: rerankScores.GetValueOrDefault(addr, 0.0),
                TotalOfferings: bm.TotalOfferings,
                TopOfferings: topOfferingRecords,
                TotalJobs: bm.TotalJobs,
                TopOfferingNames: topOfferingNames.ToArray(),
                Marketplaces: BuildMarketplaces(cp),
                DominantMarketplace: cp.Dominant,
                AgentScore: rep?.AgentScore));
        }

        return enriched;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildMarketplaces(CrossPresence cp)
    {
        var list = new List<string>(2);
        if (cp.V1 is not null) list.Add("v1");
        if (cp.V2 is not null) list.Add("v2");
        return list;
    }

    private async Task<IReadOnlyList<AgentSearchHitOffering>> BuildTopOfferingRecordsAsync(
        string agentAddress, IReadOnlyList<string> offeringNames)
    {
        if (offeringNames.Count == 0) return Array.Empty<AgentSearchHitOffering>();

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", offeringNames.Select((_, i) => $"$n{i}"));
        cmd.CommandText = $@"
            SELECT offering_name, price_usdc, marketplace_version
            FROM offerings
            WHERE agent_address = $a
              AND offering_name IN ({placeholders})
              AND is_removed = 0";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());
        for (int i = 0; i < offeringNames.Count; i++)
            cmd.Parameters.AddWithValue($"$n{i}", offeringNames[i]);

        var byName = new Dictionary<string, AgentSearchHitOffering>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            byName[name] = new AgentSearchHitOffering(name, r.GetDouble(1), r.GetString(2));
        }

        return offeringNames
            .Select(n => byName.TryGetValue(n, out var o) ? o : null)
            .Where(o => o is not null)
            .Cast<AgentSearchHitOffering>()
            .ToList();
    }

    private async Task<IReadOnlyList<(string AgentAddress, double Cosine)>> DenseAgentRankAsync(
        float[] qVec, int limit, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, embedding
            FROM agent_profiles
            WHERE embedding IS NOT NULL";

        var hits = new List<(string, double)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var addr = r.GetString(0);
            var blob = (byte[])r.GetValue(1);
            var v = BytesToFloats(blob);
            hits.Add((addr, Cosine(qVec, v)));
        }

        return hits.OrderByDescending(h => h.Item2).Take(limit).ToList();
    }

    // ── Pure-function helpers (public so tests can call them directly) ─────────

    public static Dictionary<string, double> ReciprocalRankFusion(
        IReadOnlyList<string> a, IReadOnlyList<string> b, int k)
    {
        var fused = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Count; i++)
            fused[a[i]] = fused.GetValueOrDefault(a[i]) + 1.0 / (k + i + 1);
        for (int i = 0; i < b.Count; i++)
            fused[b[i]] = fused.GetValueOrDefault(b[i]) + 1.0 / (k + i + 1);
        return fused;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        var d = Math.Sqrt(na) * Math.Sqrt(nb);
        return d == 0 ? 0 : dot / d;
    }
}
