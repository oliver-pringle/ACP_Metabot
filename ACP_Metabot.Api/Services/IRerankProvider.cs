namespace ACP_Metabot.Api.Services;

/// <summary>
/// Abstraction over a relevance reranker (e.g. Voyage /v1/rerank).
/// Returns results as typed records so the service can capture both
/// the original index and the relevance score without depending on a
/// concrete provider.
/// </summary>
public interface IRerankProvider
{
    Task<IReadOnlyList<RerankResult>> RankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken ct);
}

/// <summary>Adapter that wraps <see cref="VoyageRerankProvider"/> as <see cref="IRerankProvider"/>.</summary>
public class VoyageRerankAdapter : IRerankProvider
{
    private readonly VoyageRerankProvider _inner;

    public VoyageRerankAdapter(VoyageRerankProvider inner) => _inner = inner;

    public async Task<IReadOnlyList<RerankResult>> RankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken ct)
    {
        var indices = await _inner.RerankAsync(query, documents, ct);
        // VoyageRerankProvider returns indices already ordered by relevance desc.
        // Assign a synthetic score = 1/(rank+1) so callers have an ordinal value.
        var results = new RerankResult[indices.Length];
        for (int i = 0; i < indices.Length; i++)
            results[i] = new RerankResult(indices[i], 1.0 / (i + 1));
        return results;
    }
}

public record RerankResult(int Index, double Score);
