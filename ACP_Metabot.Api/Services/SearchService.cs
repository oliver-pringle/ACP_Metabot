using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

public class SearchService
{
    private readonly OfferingRepository _repo;
    private readonly VoyageEmbeddingProvider _embedder;
    private readonly ReputationService _reputation;
    private readonly ILogger<SearchService> _logger;

    public SearchService(OfferingRepository repo, VoyageEmbeddingProvider embedder,
        ReputationService reputation, ILogger<SearchService> logger)
    {
        _repo = repo;
        _embedder = embedder;
        _reputation = reputation;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OfferingMatch>> SearchAsync(
        string query, int limit, double minScore, double priceMaxUsdc,
        int? staleAfterDays, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<OfferingMatch>();

        var queryEmb = await _embedder.EmbedQueryAsync(query, ct);
        var corpus = await _repo.ListAllWithEmbeddingsAsync(_embedder.ModelId);
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

        var scored = new List<(Offering O, double Cosine, double Blended, ReputationSummary? Rep)>(corpus.Count);
        foreach (var (offering, embedding) in corpus)
        {
            if (offering.PriceUsdc > priceMaxUsdc) continue;
            if (freshIds is not null && !freshIds.Contains(offering.Id)) continue;
            var cosine = CosineSimilarity(queryEmb, embedding);
            if (cosine < minScore) continue;
            var rep = _reputation.BuildSearchSummary(offering);
            var blended = rep is null
                ? cosine
                : CosineWeight * cosine + ReputationWeight * (rep.Score / 100.0);
            scored.Add((offering, cosine, blended, rep));
        }

        return scored
            .OrderByDescending(s => s.Blended)
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
                Reputation: s.Rep))
            .ToArray();
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
