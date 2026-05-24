using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

public class SearchService
{
    private readonly OfferingRepository _repo;
    private readonly VoyageEmbeddingProvider _embedder;
    private readonly VoyageRerankProvider _reranker;
    private readonly ReputationService _reputation;
    private readonly AgentReputationCacheRepository _agentScoreRepo;
    private readonly CategoryService _categories;
    private readonly SaturationCalculator _saturation;
    private readonly PricePercentileCalculator _pricePercentile;
    private readonly QueryExpander _expander;
    private readonly LlmQueryRewriter _rewriter;
    // v1.10 Phase 3 T6: AgentRiskScorer ctor depends on SearchService (for in-corpus
    // category lookup used by the pricing-outlier signal). Resolving it directly
    // here would close a DI cycle. Lazy-resolve via the service provider — the
    // riskFlag decoration only fires when filters.IncludeRisk=true, so the cycle
    // is broken at construction time and only paid on demand. Mirrors the
    // IServiceProvider pattern used by MarketplaceIndexerService / V2SellerScannerService.
    private readonly IServiceProvider _services;
    private readonly ILogger<SearchService> _logger;

    // Pool size for the rerank pass. The reranker is much more expensive
    // per-doc than cosine, so we feed it a generous-but-bounded shortlist
    // and let it pick the final top-`limit`.
    private const int RerankPoolSize = 50;

    // Candidate-pool sizes for the hybrid fusion. 200 is generous for an
    // ~50K-row corpus and keeps RRF cost negligible.
    private const int DensePoolSize = 200;
    private const int LexicalPoolSize = 200;
    // RRF rank-discount constant (TREC default). Higher = flatter contribution
    // from low-ranked items; 60 is the canonical value across IR literature.
    private const int RrfK = 60;

    // Embedded-corpus cache. Each row is pre-tagged with its nearest
    // category at refresh time so search responses can include `category`
    // without an extra cosine pass per request.
    private List<(Offering Offering, float[] Embedding, string? Category)>? _corpus;
    private DateTime _corpusRefreshedAtUtc;
    // (lowercased agent_address -> agent_score) snapshot taken alongside the
    // corpus refresh. Used by the optional minReputation filter on /search.
    // Empty dict on cold start; agents missing from the snapshot pass through
    // any minReputation gate (unindexed != bad).
    private IReadOnlyDictionary<string, int> _agentScoreLookup =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public DateTime CorpusRefreshedAtUtc => _corpusRefreshedAtUtc;
    public int CorpusCount => Volatile.Read(ref _corpus)?.Count ?? 0;

    /// <summary>
    /// Returns the pre-tagged category for a single offering, looked up by id
    /// in the in-memory corpus. Returns null when the corpus hasn't loaded yet
    /// or the offering isn't present (e.g. recently removed). O(n) scan is fine
    /// for typical agent portfolio sizes (single digits to low hundreds).
    /// </summary>
    public string? GetCategoryForOffering(long offeringId)
    {
        var corpus = Volatile.Read(ref _corpus);
        if (corpus is null) return null;
        foreach (var (offering, _, category) in corpus)
        {
            if (offering.Id == offeringId) return category;
        }
        return null;
    }

    /// <summary>
    /// (categoryName -> active offering count) over the embedded corpus.
    /// Cheap to compute on demand; the corpus already carries each row's
    /// pre-tagged category. Returns an empty dict before the first refresh.
    /// </summary>
    public Dictionary<string, int> CategoryCounts()
    {
        var corpus = Volatile.Read(ref _corpus);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (corpus is null) return counts;
        foreach (var (_, _, category) in corpus)
        {
            if (string.IsNullOrEmpty(category)) continue;
            counts[category] = counts.TryGetValue(category, out var c) ? c + 1 : 1;
        }
        return counts;
    }

    /// <summary>
    /// (marketplaceVersion -> corpus count). Used by /v1/health to show V1
    /// vs V2 split. Empty dict before the first refresh.
    /// </summary>
    public Dictionary<string, int> CorpusByMarketplace()
    {
        var corpus = Volatile.Read(ref _corpus);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (corpus is null) return counts;
        foreach (var (offering, _, _) in corpus)
        {
            var key = string.IsNullOrEmpty(offering.MarketplaceVersion) ? "v1" : offering.MarketplaceVersion;
            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
        }
        return counts;
    }

    public SearchService(OfferingRepository repo, VoyageEmbeddingProvider embedder,
        VoyageRerankProvider reranker, ReputationService reputation,
        AgentReputationCacheRepository agentScoreRepo,
        CategoryService categories,
        SaturationCalculator saturation,
        PricePercentileCalculator pricePercentile,
        QueryExpander expander,
        LlmQueryRewriter rewriter,
        IServiceProvider services,
        ILogger<SearchService> logger)
    {
        _repo = repo;
        _embedder = embedder;
        _reranker = reranker;
        _reputation = reputation;
        _agentScoreRepo = agentScoreRepo;
        _categories = categories;
        _saturation = saturation;
        _pricePercentile = pricePercentile;
        _expander = expander;
        _rewriter = rewriter;
        _services = services;
        _logger = logger;
    }

    public async Task RefreshCorpusAsync()
    {
        var fresh = await _repo.ListAllWithEmbeddingsAsync(_embedder.ModelId);
        var tagged = fresh
            .Select(t => (t.Offering, t.Embedding, _categories.Classify(t.Embedding)))
            .ToList();
        Volatile.Write(ref _corpus, tagged);

        // Refresh saturation + price-percentile calculators in lockstep so
        // per-hit enrichments are always consistent with the current corpus.
        _saturation.Refresh(tagged.Select(c => (c.Item1.Id, c.Item3 ?? string.Empty, c.Item2)));
        _pricePercentile.Refresh(tagged.Select(c =>
            (c.Item1.Id, c.Item3 ?? string.Empty, c.Item1.MarketplaceVersion ?? "v1", c.Item1.PriceUsdc)));

        // Refresh the (agent -> score) snapshot in lockstep with the corpus
        // so the minReputation filter operates on consistent data without
        // per-request DB reads.
        try
        {
            var scores = await _agentScoreRepo.ListAllAgentScoresAsync(DateTime.UtcNow);
            _agentScoreLookup = scores;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[search] reputation snapshot refresh failed; minReputation filter will pass-through");
            _agentScoreLookup = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        _corpusRefreshedAtUtc = DateTime.UtcNow;
        _logger.LogInformation("[search] corpus cache rebuilt with {N} embedded offerings ({R} reputation-known)",
            tagged.Count, _agentScoreLookup.Count);
    }

    public Task<IReadOnlyList<OfferingMatch>> SearchAsync(
        string query, int limit, double minScore, double priceMaxUsdc,
        int? staleAfterDays, bool rerank, string? categoryFilter,
        HashSet<string>? chainFilter, int? minReputation,
        string? marketplaceFilter,
        CancellationToken ct)
        => SearchAsync(query, limit, offset: 0, minScore, priceMaxUsdc,
            staleAfterDays, rerank, categoryFilter, chainFilter, minReputation,
            marketplaceFilter, ct);

    /// <summary>
    /// v1.10 Phase 1 negative-filter helper. Pure-function, applied between
    /// rerank and pagination so the returned top-N stays consistent with
    /// buyer intent. Drops offerings whose agent / chain / price / requirement
    /// schema matches any exclusion entry. Returns the input list unchanged
    /// when filters is null OR all exclusion lists are empty AND MaxPriceUsd
    /// is null.
    /// </summary>
    public static IReadOnlyList<OfferingMatch> ApplyNegativeFilters(
        IReadOnlyList<OfferingMatch> hits,
        SearchFilters filters)
    {
        if (filters is null) return hits;
        HashSet<string>? excludeAgents = null;
        if (filters.ExcludeAgents is { Count: > 0 })
        {
            excludeAgents = new HashSet<string>(
                filters.ExcludeAgents
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim().ToLowerInvariant()),
                StringComparer.Ordinal);
            if (excludeAgents.Count == 0) excludeAgents = null;
        }
        HashSet<string>? excludeChains = null;
        if (filters.ExcludeChains is { Count: > 0 })
        {
            excludeChains = new HashSet<string>(
                filters.ExcludeChains
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim().ToLowerInvariant()),
                StringComparer.Ordinal);
            if (excludeChains.Count == 0) excludeChains = null;
        }
        var excludeReqs = filters.ExcludeRequirements?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToArray() ?? Array.Empty<string>();

        var keep = new List<OfferingMatch>(hits.Count);
        foreach (var h in hits)
        {
            if (excludeAgents is not null
                && excludeAgents.Contains((h.AgentAddress ?? "").ToLowerInvariant()))
                continue;
            if (excludeChains is not null
                && excludeChains.Contains((h.Chain ?? "").ToLowerInvariant()))
                continue;
            if (filters.MaxPriceUsd is double cap && h.PriceUsdc > cap)
                continue;
            if (excludeReqs.Length > 0)
            {
                var reqs = TryGetRequirements(h);
                if (!string.IsNullOrEmpty(reqs))
                {
                    var hit = false;
                    foreach (var token in excludeReqs)
                    {
                        if (reqs.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hit = true; break;
                        }
                    }
                    if (hit) continue;
                }
            }
            keep.Add(h);
        }
        return keep;
    }

    // Best-effort accessor for requirements text. OfferingMatch doesn't
    // currently expose the requirements schema directly; this method
    // centralises the lookup via reflection so the filter logic is testable
    // and forward-compatible — if a future commit adds a Requirements property
    // to OfferingMatch (e.g. v1.10 Phase 2's schema_facets work), it'll be
    // picked up automatically. When no candidate property exists, the filter
    // is a no-op for that hit (acceptable Phase 1 behaviour per the spec).
    private static string? TryGetRequirements(OfferingMatch h)
    {
        var t = h.GetType();
        foreach (var name in new[] { "Requirements", "RequirementSchema", "RequirementsJson", "RequirementSchemaJson" })
        {
            var prop = t.GetProperty(name);
            if (prop is not null) return prop.GetValue(h)?.ToString();
        }
        return null;
    }

    /// <summary>
    /// v1.10 Phase 1 entry point used by HandleSearch. Returns the paginated
    /// hits AND the ExpandedQuery so the endpoint can echo glossary expansion
    /// in the response envelope. Fetches a large candidate pool first
    /// (pre-filter), applies negative filters, then paginates — keeping the
    /// returned top-N consistent with buyer intent. MaxPriceUsd is fused with
    /// the existing priceMaxUsdc cap (we use the tighter of the two so a
    /// caller can't widen an existing limit).
    /// </summary>
    public async Task<(IReadOnlyList<OfferingMatch> Hits, ExpandedQuery Expansion)> SearchWithFiltersAsync(
        string query, int limit, int offset, double minScore, double priceMaxUsdc,
        int? staleAfterDays, bool rerank, string? categoryFilter,
        HashSet<string>? chainFilter, int? minReputation,
        string? marketplaceFilter,
        SearchFilters filters,
        CancellationToken ct)
    {
        var expansion = _expander.Expand(query ?? string.Empty);
        var safeFilters = filters ?? new SearchFilters();

        // v1.10 Phase 3: LLM query rewriter — opt-in via filters.Expand=true.
        // Degraded paths in the rewriter (cap breached, key missing, LLM error)
        // return passthrough + empty synonyms so the glossary-only expanded set
        // is left intact. When synonyms ARE returned, they're merged with the
        // Phase 1 glossary expander's synonym set using the same union pattern
        // Phase 1 T3 used for the BM25 per-term lookup — the inner SearchAsync
        // overload picks them up.
        if (safeFilters.Expand)
        {
            var rewriterResult = await _rewriter.RewriteAsync(query ?? string.Empty, ct);
            if (rewriterResult.Synonyms.Count > 0)
            {
                var merged = new List<string>(expansion.Synonyms);
                foreach (var s in rewriterResult.Synonyms)
                {
                    if (!merged.Contains(s, StringComparer.OrdinalIgnoreCase))
                        merged.Add(s);
                }
                var mergedHits = new List<string>(expansion.GlossaryHits);
                foreach (var s in rewriterResult.Synonyms)
                    mergedHits.Add($"rewriter → {s}");
                expansion = new ExpandedQuery(expansion.Primary, merged, mergedHits);
            }
        }

        var effectivePriceMax = safeFilters.MaxPriceUsd is double mp
            ? Math.Min(priceMaxUsdc, mp)
            : priceMaxUsdc;
        // Fetch a generous candidate pool pre-filter so that negative filters
        // applied below don't starve the paginated window. 200 mirrors
        // DensePoolSize so we never lose ranking signal we already paid for.
        // Pass the pre-computed expansion (with any rewriter synonyms merged in)
        // so the BM25 leg picks them up — avoids re-expanding the query inside
        // SearchAsync and losing the rewriter's contribution.
        var raw = await SearchAsync(query ?? string.Empty, limit: 200, offset: 0,
            minScore, effectivePriceMax, staleAfterDays, rerank, categoryFilter,
            chainFilter, minReputation, marketplaceFilter, expansion, ct);
        IReadOnlyList<OfferingMatch> filtered = ApplyNegativeFilters(raw, safeFilters);

        // v1.10 Phase 2: schema-facet intersection. Drops offerings whose
        // requirement / deliverable schema doesn't declare the named field.
        // Index-backed lookup against schema_facets — O(log n) regardless of
        // corpus size. Runs after negative filters so the candidate pool is
        // already pruned; runs before pagination so the returned top-N
        // stays consistent with buyer intent.
        if (!string.IsNullOrWhiteSpace(safeFilters.RequiresField))
        {
            var allowed = await _repo.GetOfferingIdsByFacetAsync(
                safeFilters.RequiresField, "requirement", ct);
            filtered = filtered.Where(h => allowed.Contains(h.OfferingId)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(safeFilters.ProducesField))
        {
            var allowed = await _repo.GetOfferingIdsByFacetAsync(
                safeFilters.ProducesField, "deliverable", ct);
            filtered = filtered.Where(h => allowed.Contains(h.OfferingId)).ToList();
        }

        var paged = filtered.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToList();

        // v1.10 Phase 3 T6: surface per-hit risk tier when includeRisk=true.
        // GetTierAsync is a cheap cached read — returns null when no cached entry
        // exists (the buyer can trigger a paid /v1/agentRiskCheck call to populate).
        // Batched by unique agent address so M hits across N agents = N cache
        // reads, not M. Per-call failures degrade to null + a warning log so a
        // pathological SQLite hiccup can't take the search endpoint with it.
        //
        // chainId resolution is best-effort. OfferingMatch carries Chain as a
        // string label ("base" / "ethereum" / "polygon"), not the integer
        // chainId; we default to 8453 (Base) and only switch when the caller
        // restricted the search to Ethereum. Per-hit precise chain handling is
        // a Phase 4 concern — see commit message for the follow-up rationale.
        if (filters?.IncludeRisk == true && paged.Count > 0)
        {
            var defaultChainId = 8453;
            if (chainFilter is { Count: > 0 }
                && chainFilter.Contains("ethereum")
                && !chainFilter.Contains("base"))
            {
                defaultChainId = 1;
            }

            // Lazy-resolve AgentRiskScorer (see _services field doc — DI cycle
            // through SearchService prevents constructor injection).
            AgentRiskScorer? riskScorer = null;
            try
            {
                riskScorer = _services.GetService(typeof(AgentRiskScorer)) as AgentRiskScorer;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[search] AgentRiskScorer resolve failed; skipping riskFlag decoration");
            }

            if (riskScorer is not null)
            {
                var uniqueAgents = paged
                    .Select(h => (h.AgentAddress ?? string.Empty).ToLowerInvariant())
                    .Where(a => a.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var tierMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var addr in uniqueAgents)
                {
                    try
                    {
                        tierMap[addr] = await riskScorer.GetTierAsync(addr, defaultChainId, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[search] risk-tier lookup failed for {Addr}", addr);
                        tierMap[addr] = null;
                    }
                }

                paged = paged
                    .Select(h =>
                    {
                        var addr = (h.AgentAddress ?? string.Empty).ToLowerInvariant();
                        if (addr.Length == 0) return h;
                        return tierMap.TryGetValue(addr, out var tier) && tier is not null
                            ? h with { RiskFlag = tier }
                            : h;
                    })
                    .ToList();
            }
        }

        return (paged, expansion);
    }

    /// <summary>
    /// Pagination-aware overload. Skips <paramref name="offset"/> results before
    /// taking <paramref name="limit"/>. The blended-and-reranked ordering is
    /// preserved: pagination operates on the final, sorted candidate list.
    /// </summary>
    public Task<IReadOnlyList<OfferingMatch>> SearchAsync(
        string query, int limit, int offset, double minScore, double priceMaxUsdc,
        int? staleAfterDays, bool rerank, string? categoryFilter,
        HashSet<string>? chainFilter, int? minReputation,
        string? marketplaceFilter,
        CancellationToken ct)
        => SearchAsync(query, limit, offset, minScore, priceMaxUsdc, staleAfterDays,
            rerank, categoryFilter, chainFilter, minReputation, marketplaceFilter,
            preComputedExpansion: null, ct);

    /// <summary>
    /// v1.10 Phase 3 overload — accepts a pre-computed ExpandedQuery so the
    /// caller (typically SearchWithFiltersAsync) can merge LLM-rewriter
    /// synonyms into the BM25 per-term lookup. When null, the expander is
    /// invoked internally exactly as in Phase 1 — preserving backwards
    /// compatibility for the public SearchAsync entry points.
    /// </summary>
    private async Task<IReadOnlyList<OfferingMatch>> SearchAsync(
        string query, int limit, int offset, double minScore, double priceMaxUsdc,
        int? staleAfterDays, bool rerank, string? categoryFilter,
        HashSet<string>? chainFilter, int? minReputation,
        string? marketplaceFilter,
        ExpandedQuery? preComputedExpansion,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<OfferingMatch>();

        // v1.10 Phase 1: glossary expansion. The dense leg still embeds the
        // verbatim user query — synonym-aware embeddings are deferred until
        // the Phase 3 LLM rewriter ships. The BM25 leg below OR-merges the
        // canonical synonyms into the FTS5 MATCH string so jargon/alias
        // queries (e.g. "ape" → "yield farming") surface relevant offerings
        // even when the original term isn't in the offering text.
        // glossaryHits will be surfaced by T5 in the response envelope.
        //
        // v1.10 Phase 3: when SearchWithFiltersAsync supplied a pre-computed
        // expansion (containing rewriter-merged synonyms), use it directly so
        // the BM25 per-term loop picks up the LLM's contribution. Otherwise
        // expand the query in-place as before.
        var expanded = preComputedExpansion ?? _expander.Expand(query);

        var queryEmb = await _embedder.EmbedQueryAsync(query, ct);
        var corpus = Volatile.Read(ref _corpus);
        if (corpus is null)
        {
            // Cold start — first request before the indexer has finished a
            // cycle. Fall back to a direct read so the request still succeeds;
            // category tagging is null for this slow path.
            var raw = await _repo.ListAllWithEmbeddingsAsync(_embedder.ModelId);
            corpus = raw.Select(t => (t.Offering, t.Embedding, (string?)null)).ToList();
        }
        if (corpus.Count == 0)
        {
            _logger.LogWarning("[search] corpus is empty — has the indexer run yet?");
            return Array.Empty<OfferingMatch>();
        }

        // Optional stale-offering filter. Pulls a HashSet of "fresh" ids and
        // skips anything not in the set. Falls back to "any hires at all" when
        // there's no historical snapshot for the cutoff date (early days).
        HashSet<long>? freshIds = null;
        if (staleAfterDays is int days && days > 0)
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-days).ToString("yyyy-MM-dd");
            var ids = await _repo.ListFreshOfferingIdsAsync(cutoff);
            freshIds = new HashSet<long>(ids);
        }

        // Score the filtered corpus with cosine. Reputation blend is applied
        // AFTER fusion so its weight is consistent regardless of which ranker
        // (dense, lexical) surfaced the candidate. Displayed `Score` stays as
        // raw cosine for backwards compat with plugin/skill rendering.
        const double SimilarityWeight = 0.7;
        const double ReputationWeight = 0.3;

        var scored = new List<(Offering O, double Cosine, ReputationSummary? Rep, string? Category)>(corpus.Count);
        foreach (var (offering, embedding, category) in corpus)
        {
            if (offering.PriceUsdc > priceMaxUsdc) continue;
            if (freshIds is not null && !freshIds.Contains(offering.Id)) continue;
            if (categoryFilter is not null
                && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (chainFilter is not null
                && !chainFilter.Contains(offering.Chain.ToLowerInvariant()))
                continue;
            if (marketplaceFilter is not null
                && !string.Equals(offering.MarketplaceVersion, marketplaceFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (minReputation is int repFloor && repFloor > 0)
            {
                // Unindexed agents pass through — we don't penalise an agent
                // whose reputation hasn't been computed yet. Once a row exists,
                // it must clear the floor.
                if (_agentScoreLookup.TryGetValue(offering.AgentAddress.ToLowerInvariant(), out var s)
                    && s < repFloor)
                    continue;
            }
            var cosine = CosineSimilarity(queryEmb, embedding);
            if (cosine < minScore) continue;
            var rep = _reputation.BuildSearchSummary(offering);
            scored.Add((offering, cosine, rep, category));
        }

        // Hybrid ordering — Reciprocal Rank Fusion of dense (cosine) and
        // lexical (BM25) rankings. BM25 catches rare-keyword queries where
        // pure cosine collapses (hex addresses, niche tickers, jargon).
        var denseRanking = scored
            .OrderByDescending(s => s.Cosine)
            .Take(DensePoolSize)
            .Select(s => s.O.Id)
            .ToList();

        IReadOnlyList<long> lexicalRanking;
        try
        {
            // Lexical leg. When the glossary fired, run BM25 once per term
            // (primary + each canonical synonym) and union the rowids,
            // keeping the lowest (best) BM25 score per id. SanitizeFtsQuery
            // wraps multi-token inputs in phrase quotes, so naively
            // concatenating "primary synonym1 synonym2" would collapse the
            // FTS leg into a strict phrase match and regress the dense+BM25
            // hybrid for any glossary-hit query. Per-term BM25 + union
            // gives the OR-merge the brief asks for without that footgun.
            // Cost is N+1 cheap FTS reads (typically N≤3 in Phase 1).
            IReadOnlyList<(long Id, double Bm25)> bm25Raw;
            if (expanded.Synonyms.Count > 0)
            {
                var merged = new Dictionary<long, double>(LexicalPoolSize);
                var terms = new List<string> { query };
                terms.AddRange(expanded.Synonyms);
                foreach (var term in terms)
                {
                    var hits = await _repo.SearchBm25Async(term, LexicalPoolSize);
                    foreach (var (id, score) in hits)
                    {
                        if (!merged.TryGetValue(id, out var prev) || score < prev)
                            merged[id] = score;
                    }
                }
                bm25Raw = merged
                    .OrderBy(kv => kv.Value)
                    .Take(LexicalPoolSize)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
            }
            else
            {
                bm25Raw = await _repo.SearchBm25Async(query, LexicalPoolSize);
            }
            // BM25 results aren't filtered by price/category/freshness — intersect
            // with the already-filtered candidate set so excluded offerings can't
            // sneak back in via the lexical leg.
            var filteredIds = new HashSet<long>(scored.Select(s => s.O.Id));
            lexicalRanking = bm25Raw.Select(b => b.Id).Where(filteredIds.Contains).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[search] BM25 leg failed; falling back to dense-only");
            lexicalRanking = Array.Empty<long>();
        }

        var fusedScores = ReciprocalRankFusion(denseRanking, lexicalRanking, RrfK);
        // Normalise so the dominant signal lands at 1.0; preserves the 70/30
        // SimilarityWeight/ReputationWeight contract regardless of pool size.
        var maxFused = fusedScores.Count > 0 ? fusedScores.Values.Max() : 0.0;

        var ordered = scored
            .Select(s =>
            {
                var rrf = fusedScores.TryGetValue(s.O.Id, out var f) ? f : 0.0;
                var similarity = maxFused > 0 ? rrf / maxFused : 0.0;
                var repBoost = s.Rep is null ? 0.0 : s.Rep.Score / 100.0;
                var blended = s.Rep is null
                    ? similarity
                    : SimilarityWeight * similarity + ReputationWeight * repBoost;
                return (s.O, s.Cosine, Blended: blended, s.Rep, s.Category);
            })
            .OrderByDescending(s => s.Blended)
            .ToList();

        // Optional rerank pass: feed the top-K cosine candidates to Voyage's
        // reranker for a more discerning final ordering. Falls back to pure
        // cosine ordering on any failure so a Voyage outage doesn't take the
        // search endpoint with it.
        if (rerank && ordered.Count > 1)
        {
            var poolSize = Math.Min(RerankPoolSize, ordered.Count);
            var pool = ordered.Take(poolSize).ToList();
            try
            {
                var docs = pool.Select(s => BuildRerankDocument(s.O)).ToArray();
                var sortedIndices = await _reranker.RerankAsync(query, docs, ct);
                // Replace the head of `ordered` with the rerank-sorted pool;
                // the long tail (>poolSize) keeps its cosine order.
                var rerankedHead = sortedIndices.Select(i => pool[i]).ToList();
                ordered = rerankedHead.Concat(ordered.Skip(poolSize)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[search] rerank failed; falling back to cosine ordering");
            }
        }

        return ordered
            .Skip(Math.Max(0, offset))
            .Take(limit)
            .Select(s =>
            {
                var cat = s.Category ?? string.Empty;
                var mv  = s.O.MarketplaceVersion ?? "v1";
                var pp  = _pricePercentile.Compute(s.O.Id, cat, mv, s.O.PriceUsdc);
                return new OfferingMatch(
                    OfferingId: s.O.Id,
                    AgentName: s.O.AgentName,
                    AgentAddress: s.O.AgentAddress,
                    OfferingName: s.O.OfferingName,
                    Description: s.O.Description,
                    PriceUsdc: s.O.PriceUsdc,
                    PriceType: s.O.PriceType,
                    Chain: s.O.Chain,
                    Score: Math.Round(s.Cosine, 4),
                    Reputation: s.Rep,
                    Category: s.Category,
                    MarketplaceVersion: mv,
                    Saturation: new SaturationDto(
                        _saturation.NearDuplicateCount(s.O.Id, cat),
                        _saturation.CategorySize(cat)),
                    PricePercentile: new PricePercentileDto(pp.Value, pp.PeerN, pp.LowN));
            })
            .ToArray();
    }

    // Mirrors MarketplaceIndexerService.BuildEmbeddingText so the reranker
    // sees the same document representation we embedded.
    private static string BuildRerankDocument(Offering o)
    {
        return $"Offering: {o.OfferingName}\nAgent: {o.AgentName}\nPrice: {o.PriceUsdc} USDC\nDescription: {o.Description}";
    }

    /// <summary>
    /// Reciprocal Rank Fusion. Given two ranked id lists, returns
    /// {id -> Σ_r 1 / (k + rank_r(id))}. Parameter-free aside from k; k=60
    /// is the canonical TREC default. Robust to mismatched scales between
    /// rankers (cosine ∈ [-1,1] vs BM25 ∈ [0,∞)) — the rank position is
    /// what's fused, not the raw score.
    /// Internal so the test project can exercise it without DI.
    /// </summary>
    internal static Dictionary<long, double> ReciprocalRankFusion(
        IReadOnlyList<long> denseRanking,
        IReadOnlyList<long> lexicalRanking,
        int k)
    {
        var fused = new Dictionary<long, double>(denseRanking.Count + lexicalRanking.Count);
        for (int i = 0; i < denseRanking.Count; i++)
        {
            // 1-indexed rank; first = 1, second = 2, ...
            var contribution = 1.0 / (k + (i + 1));
            fused[denseRanking[i]] = fused.TryGetValue(denseRanking[i], out var p) ? p + contribution : contribution;
        }
        for (int i = 0; i < lexicalRanking.Count; i++)
        {
            var contribution = 1.0 / (k + (i + 1));
            fused[lexicalRanking[i]] = fused.TryGetValue(lexicalRanking[i], out var p) ? p + contribution : contribution;
        }
        return fused;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}
