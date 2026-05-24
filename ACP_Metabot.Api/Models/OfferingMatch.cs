using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

public record SaturationDto(
    [property: JsonPropertyName("nearDuplicateCount")] int NearDuplicateCount,
    [property: JsonPropertyName("categorySize")] int CategorySize);

public record PricePercentileDto(
    [property: JsonPropertyName("value")] int? Value,
    [property: JsonPropertyName("peerN")] int PeerN,
    [property: JsonPropertyName("lowN")] bool LowN);

public record OfferingMatch(
    long OfferingId,
    string AgentName,
    string AgentAddress,
    string OfferingName,
    string Description,
    double PriceUsdc,
    string PriceType,
    string Chain,
    double Score,
    [property: JsonPropertyName("reputation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ReputationSummary? Reputation = null,
    [property: JsonPropertyName("category"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Category = null,
    [property: JsonPropertyName("marketplaceVersion")]
        string MarketplaceVersion = "v1",
    [property: JsonPropertyName("saturation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SaturationDto? Saturation = null,
    [property: JsonPropertyName("pricePercentile"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        PricePercentileDto? PricePercentile = null,
    // v1.10 Phase 3 T6: per-hit risk tier surfaced when filters.IncludeRisk=true.
    // Populated by SearchService.SearchWithFiltersAsync via AgentRiskScorer.GetTierAsync
    // (cheap cached read; null when no cached entry — buyer can trigger a paid
    // /v1/agentRiskCheck call to populate). Values: "low" / "medium" / "high" /
    // "critical" matching the AgentRiskResult.RiskTier bins.
    [property: JsonPropertyName("riskFlag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? RiskFlag = null);
