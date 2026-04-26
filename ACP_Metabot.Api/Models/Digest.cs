using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

public record NewOffering(
    [property: JsonPropertyName("offeringId")]    long OfferingId,
    [property: JsonPropertyName("agentName")]     string AgentName,
    [property: JsonPropertyName("agentAddress")]  string AgentAddress,
    [property: JsonPropertyName("offeringName")]  string OfferingName,
    [property: JsonPropertyName("description")]   string Description,
    [property: JsonPropertyName("priceUsdc")]     double PriceUsdc,
    [property: JsonPropertyName("priceType")]     string PriceType,
    [property: JsonPropertyName("chain")]         string Chain,
    [property: JsonPropertyName("firstSeenAt")]   string FirstSeenAt,
    [property: JsonPropertyName("reputation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ReputationSummary? Reputation);

public record OfferingGainer(
    [property: JsonPropertyName("offeringId")]   long OfferingId,
    [property: JsonPropertyName("agentName")]    string AgentName,
    [property: JsonPropertyName("agentAddress")] string AgentAddress,
    [property: JsonPropertyName("offeringName")] string OfferingName,
    [property: JsonPropertyName("hiresThen")]    long HiresThen,
    [property: JsonPropertyName("hiresNow")]     long HiresNow,
    [property: JsonPropertyName("delta")]        long Delta);

public record DigestResult(
    [property: JsonPropertyName("windowDays")]         int WindowDays,
    [property: JsonPropertyName("snapshotComparison")] string SnapshotComparison,
    [property: JsonPropertyName("newOfferings")]       IReadOnlyList<NewOffering> NewOfferings,
    [property: JsonPropertyName("gainers")]            IReadOnlyList<OfferingGainer> Gainers,
    [property: JsonPropertyName("computedAt")]         string ComputedAt);
