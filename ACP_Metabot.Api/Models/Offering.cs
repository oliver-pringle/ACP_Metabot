namespace ACP_Metabot.Api.Models;

public record Offering(
    long Id,
    string AgentAddress,
    string AgentName,
    string OfferingName,
    string Description,
    string? RequirementSchemaJson,
    double PriceUsdc,
    string PriceType,
    bool IsPrivate,
    string Chain,
    string ContentHash,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    long UsageCount,
    long AgentJobCount,
    string MarketplaceVersion = "v1",
    // v1.5 tombstone state. IsRemoved=true means the offering has been
    // missing from upstream fetches for longer than the marketplace's
    // tombstone threshold. Reactivates automatically on reappearance.
    bool IsRemoved = false,
    DateTime? RemovedAt = null);
