using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services.MarketplaceSource;

public interface IMarketplaceSource
{
    /// <summary>
    /// Fetches the current set of marketplace offerings.
    /// Implementations may pull from a JSON file, the upstream ACP API,
    /// or any other source. Returns the full snapshot — the indexer
    /// reconciles inserts/updates by content hash.
    /// </summary>
    Task<IReadOnlyList<MarketplaceOfferingDto>> FetchAsync(CancellationToken ct);
}
