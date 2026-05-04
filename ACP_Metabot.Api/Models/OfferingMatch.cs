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
        PricePercentileDto? PricePercentile = null);
