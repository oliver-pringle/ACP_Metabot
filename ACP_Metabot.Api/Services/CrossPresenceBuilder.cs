using System.Text.Json.Serialization;
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

public record CrossPresenceMarketplace(
    [property: JsonPropertyName("offeringCount")] int OfferingCount,
    [property: JsonPropertyName("firstSeenAt")]   string FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")]    string LastSeenAt);

public record CrossPresence(
    [property: JsonPropertyName("v1")]       CrossPresenceMarketplace? V1,
    [property: JsonPropertyName("v2")]       CrossPresenceMarketplace? V2,
    [property: JsonPropertyName("inBoth")]   bool InBoth,
    [property: JsonPropertyName("dominant")] string Dominant);   // "v1" | "v2" | "tied" | "none"

/// <summary>
/// Computes V1↔V2 cross-presence for an agent address by inspecting the
/// non-tombstoned (is_removed = 0) rows in the offerings table, grouped by
/// marketplace_version. Called by the agent profile endpoint (Task 3.2).
/// </summary>
public class CrossPresenceBuilder
{
    private readonly OfferingRepository _offerings;

    public CrossPresenceBuilder(OfferingRepository offerings)
    {
        _offerings = offerings;
    }

    public async Task<CrossPresence> BuildAsync(string agentAddress)
    {
        // ListByAgentAsync already filters is_removed = 0.
        var rows = await _offerings.ListByAgentAsync(agentAddress.ToLowerInvariant());

        // Group by marketplace_version; compute (count, min first_seen_at, max last_seen_at).
        CrossPresenceMarketplace? v1 = null;
        CrossPresenceMarketplace? v2 = null;

        var v1Rows = rows.Where(o => o.MarketplaceVersion == "v1").ToList();
        var v2Rows = rows.Where(o => o.MarketplaceVersion == "v2").ToList();

        if (v1Rows.Count > 0)
        {
            v1 = new CrossPresenceMarketplace(
                OfferingCount: v1Rows.Count,
                FirstSeenAt: v1Rows.Min(o => o.FirstSeenAt).ToString("O"),
                LastSeenAt: v1Rows.Max(o => o.LastSeenAt).ToString("O"));
        }

        if (v2Rows.Count > 0)
        {
            v2 = new CrossPresenceMarketplace(
                OfferingCount: v2Rows.Count,
                FirstSeenAt: v2Rows.Min(o => o.FirstSeenAt).ToString("O"),
                LastSeenAt: v2Rows.Max(o => o.LastSeenAt).ToString("O"));
        }

        bool inBoth = v1 is not null && v2 is not null;

        string dominant = (v1, v2) switch
        {
            (null, null) => "none",
            (not null, null) => "v1",
            (null, not null) => "v2",
            _ when v1!.OfferingCount > v2!.OfferingCount => "v1",
            _ when v2!.OfferingCount > v1!.OfferingCount => "v2",
            _ => "tied"
        };

        return new CrossPresence(V1: v1, V2: v2, InBoth: inBoth, Dominant: dominant);
    }
}
