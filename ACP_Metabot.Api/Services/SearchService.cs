using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

public class SearchService
{
    private readonly OfferingRepository _repo;
    private readonly VoyageEmbeddingProvider _embedder;
    private readonly VoyageRerankProvider _reranker;
    private readonly ReputationService _reputation;
    private readonly CategoryService _categories;
    private readonly ILogger<SearchService> _logger;

    // Pool size for the rerank pass. The reranker is much more expensive
    // per-doc than cosine, so we feed it a generous-but-bounded shortlist
    // and let it pick the final top-`limit`.
    private const int RerankPoolSize = 50;

    // Embedded-corpus cache. Each row is pre-tagged with its nearest
    // category at refresh time so search responses can include `category`
    // without an extra cosine pass per request.
    private List<(Offering Offering, float[] Embedding, string? Category)>? _corpus;
    private DateTime _corpusRefreshedAtUtc;

    public DateTime CorpusRefreshedAtUtc => _corpusRefreshedAtUtc;
    public int CorpusCount => Volatile.Read(ref _corpus)?.Count ?? 0;

    public SearchService(OfferingRepository repo, VoyageEmbeddingProvider embedder,
        VoyageRerankProvider reranker, ReputationService reputation,
        CategoryService categories, ILogger<SearchService> logger)
    {
        _repo = repo;
        _embedder = embedder;
        _reranker = reranker;
        _reputation = reputation;
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
        _corpusRefreshedAtUtc = DateTime.UtcNow;
        _logger.LogInformation("[search] corpus cache rebuilt with {N} embedded offerings", tagged.Count);
    }

    public async Task<IReadOnlyList<OfferingMatch>> SearchAsync(
        string query, int limit, double minScore, double priceMaxUsdc,
        int? staleAfterDays, bool rerank, string? categoryFilter, CancellationToken ct)
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

        // In-memory cosine. Acceptable up to ~50K offerings. Final ordering
        // blends reputation in so battle-tested offerings win narrow ties;
        // the displayed `score` stays as raw cosine for backwards compat.
        const double CosineWeight = 0.7;
        const double ReputationWeight = 0.3;

        var scored = new List<(Offering O, double Cosine, double Blended, ReputationSummary? Rep, string? Category)>(corpus.Count);
        foreach (var (offering, embedding, category) in corpus)
        {
            if (offering.PriceUsdc > priceMaxUsdc) continue;
            if (freshIds is not null && !freshIds.Contains(offering.Id)) continue;
            if (categoryFilter is not null
                && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            var cosine = CosineSimilarity(queryEmb, embedding);
            if (cosine < minScore) continue;
            var rep = _reputation.BuildSearchSummary(offering);
            var blended = rep is null
                ? cosine
                : CosineWeight * cosine + ReputationWeight * (rep.Score / 100.0);
            scored.Add((offering, cosine, blended, rep, category));
        }

        var ordered = scored.OrderByDescending(s => s.Blended).ToList();

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
