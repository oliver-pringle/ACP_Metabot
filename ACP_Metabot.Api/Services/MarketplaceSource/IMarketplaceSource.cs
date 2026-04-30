using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services.MarketplaceSource;

public interface IMarketplaceSource
{
    /// <summary>
    /// Tags every offering this source returns with a marketplace version
    /// ("v1" or "v2"). The indexer copies this onto every UpsertItem so the
    /// repository can key offerings on the v1.3 composite UNIQUE
    /// (marketplace_version, agent_address, offering_name).
    /// </summary>
    string MarketplaceVersion { get; }

    /// <summary>
    /// Fetches the current set of marketplace offerings.
    /// Implementations may pull from a JSON file, the upstream ACP API,
    /// or any other source. Returns the full snapshot — the indexer
    /// reconciles inserts/updates by content hash.
    /// </summary>
    Task<IReadOnlyList<MarketplaceOfferingDto>> FetchAsync(CancellationToken ct);
}
