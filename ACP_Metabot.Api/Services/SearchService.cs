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
        string query, int limit, double minScore, double priceMaxUsdc, CancellationToken ct)
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

        // In-memory cosine. Acceptable up to ~50K offerings.
        var scored = new List<(Offering O, double Score)>(corpus.Count);
        foreach (var (offering, embedding) in corpus)
        {
            if (offering.PriceUsdc > priceMaxUsdc) continue;
            var score = CosineSimilarity(queryEmb, embedding);
            if (score >= minScore) scored.Add((offering, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
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
                Score: Math.Round(s.Score, 4),
                Reputation: _reputation.BuildSearchSummary(s.O)))
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
