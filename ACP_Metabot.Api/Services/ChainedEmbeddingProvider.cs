namespace ACP_Metabot.Api.Services;

// IEmbeddingProvider decorator: tries primary, then each fallback in order on
// transient failure. ModelId / Dimension always reflect the FIRST (primary)
// provider — callers persisting `model` alongside the vector should be aware
// the actual model used may be a fallback. The indexer (write path) currently
// records ModelId once per row; if the fallback fires that row will be tagged
// with primary's ModelId. The v1.5 re-embed lifecycle won't see those as
// "needs re-embed" — which is fine when both models share dimension, since
// the search read path stays on Voyage anyway. If you want true model-aware
// tagging, switch to recording the IEmbeddingProvider.ModelId at the call
// site of the fallback rather than the chain's ModelId; left for a future
// refactor when a third provider with a different dim joins the chain.
//
// What counts as "transient": anything except OperationCanceledException
// (which means the caller cancelled the request — propagate, don't retry).
// In practice this catches HttpRequestException (network), TaskCanceledException
// (timeout), and our own *ApiException with non-2xx status. A 400 BadRequest
// flowing into the fallback usually fails the same way there — that's fine;
// the caller sees the fallback's error and knows the input is bad.
public class ChainedEmbeddingProvider : IEmbeddingProvider
{
    private readonly IReadOnlyList<IEmbeddingProvider> _providers;
    private readonly ILogger<ChainedEmbeddingProvider> _logger;

    public string ModelId => _providers[0].ModelId;
    public int Dimension => _providers[0].Dimension;

    public ChainedEmbeddingProvider(
        IReadOnlyList<IEmbeddingProvider> providers,
        ILogger<ChainedEmbeddingProvider> logger)
    {
        if (providers.Count == 0)
            throw new ArgumentException("At least one provider required", nameof(providers));
        _providers = providers;
        _logger = logger;

        // Fail fast at construction if a fallback has a different dim than
        // primary — mixing would produce nonsense search results.
        var dim = providers[0].Dimension;
        for (var i = 1; i < providers.Count; i++)
        {
            if (providers[i].Dimension != dim)
            {
                throw new InvalidOperationException(
                    $"Embedding provider dim mismatch: {providers[0].ModelId}={dim} " +
                    $"vs {providers[i].ModelId}={providers[i].Dimension}. " +
                    "Fallback providers must agree on dimension to share the same vector column.");
            }
        }
        if (providers.Count > 1)
        {
            _logger.LogInformation(
                "[embed] chain ready: primary={Primary} (+{N} fallback(s): {Fallbacks})",
                providers[0].ModelId, providers.Count - 1,
                string.Join(", ", providers.Skip(1).Select(p => p.ModelId)));
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        Exception? last = null;
        for (var i = 0; i < _providers.Count; i++)
        {
            var p = _providers[i];
            try
            {
                var result = await p.EmbedAsync(texts, ct);
                if (i > 0)
                {
                    _logger.LogWarning(
                        "[embed] primary {Primary} failed; fellback to {Fallback} ({N} texts, {Dim} dim)",
                        _providers[0].ModelId, p.ModelId, texts.Count, Dimension);
                }
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (i < _providers.Count - 1)
                {
                    _logger.LogWarning(
                        "[embed] provider {Provider} failed ({Error}); trying next",
                        p.ModelId, ex.Message);
                }
            }
        }
        throw last ?? new InvalidOperationException("[embed] no providers attempted");
    }
}
