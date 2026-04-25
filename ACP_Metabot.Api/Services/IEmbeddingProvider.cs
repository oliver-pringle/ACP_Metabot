namespace ACP_Metabot.Api.Services;

public interface IEmbeddingProvider
{
    /// <summary>
    /// Identifier of the model used (e.g. "voyage-3-large"). Persisted with
    /// the embedding so we can detect when a model swap requires re-embedding.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Output dimension. Persisted with the embedding for shape checks.
    /// </summary>
    int Dimension { get; }

    /// <summary>
    /// Embeds a batch of texts. Returns one vector per input, in order.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
