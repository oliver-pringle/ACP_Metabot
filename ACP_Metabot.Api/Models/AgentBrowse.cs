using System.Text.Json;
using System.Text.Json.Serialization;
using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Models;

public record AgentBrowseOffering(
    [property: JsonPropertyName("offeringId")]   long OfferingId,
    [property: JsonPropertyName("offeringName")] string OfferingName,
    [property: JsonPropertyName("description")]  string Description,
    [property: JsonPropertyName("priceUsdc")]    double PriceUsdc,
    [property: JsonPropertyName("priceType")]    string PriceType,
    [property: JsonPropertyName("chain")]        string Chain,
    [property: JsonPropertyName("isPrivate")]    bool IsPrivate,
    [property: JsonPropertyName("requirementSchema"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? RequirementSchema,
    [property: JsonPropertyName("firstSeenAt")]  string FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")]   string LastSeenAt,
    [property: JsonPropertyName("reputation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ReputationSummary? Reputation,
    [property: JsonPropertyName("marketplaceVersion")]
        string MarketplaceVersion = "v1",
    [property: JsonPropertyName("pricePercentile"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        PricePercentileDto? PricePercentile = null);

public record AgentBrowseResult(
    [property: JsonPropertyName("agentAddress")] string AgentAddress,
    [property: JsonPropertyName("agentName")]    string AgentName,
    [property: JsonPropertyName("reputation")]   AgentReputationResult Reputation,
    [property: JsonPropertyName("offerings")]    IReadOnlyList<AgentBrowseOffering> Offerings,
    [property: JsonPropertyName("crossPresence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CrossPresence? CrossPresence = null);
