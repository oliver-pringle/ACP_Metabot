using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

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
        string MarketplaceVersion = "v1");
