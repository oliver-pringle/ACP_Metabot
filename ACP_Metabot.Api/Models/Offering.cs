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
    string MarketplaceVersion = "v1");
