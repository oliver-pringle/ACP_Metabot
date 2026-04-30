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

    public SearchService(OfferingRepository repo, VoyageEmbeddingProvider embedder,
        VoyageRerankProvider reranker, ReputationService reputation,
        AgentReputationCacheRepository agentScoreRepo,
        CategoryService categories, ILogger<SearchService> logger)
    {
        _repo = repo;
        _embedder = embedder;
        _reranker = reranker;
        _reputation = reputation;
        _agentScoreRepo = agentScoreRepo;
        _categories = categories;
        _logger = logger;
    }

    public async Task RefreshCorpusAsync()
    {
        var fresh = await _repo.ListAllWithEmbeddingsAsync(_embedder.ModelId);
        var tagged = fresh
            .Select(t => (t.Offering, t.Embedding, _categories.Classify(t.Embedding)))
            .ToList();
        Volatile.Write(ref _corpus, tagged);

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

    public async Task<IReadOnlyList<OfferingMatch>> SearchAsync(
        string query, int limit, double minScore, double priceMaxUsdc,
        int? staleAfterDays, bool rerank, string? categoryFilter,
        HashSet<string>? chainFilter, int? minReputation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<OfferingMatch>();

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
            var bm25Raw = await _repo.SearchBm25Async(query, LexicalPoolSize);
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
            .Take(limit)
            .Select(s => new OfferingMatch(
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
                Category: s.Category))
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
