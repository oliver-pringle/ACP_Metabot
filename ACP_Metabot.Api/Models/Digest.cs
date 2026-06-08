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
        ReputationSummary? Reputation,
    [property: JsonPropertyName("marketplaceVersion")]
        string MarketplaceVersion = "v1",
    [property: JsonPropertyName("security"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OfferingSecurity? Security = null);

public record OfferingGainer(
    [property: JsonPropertyName("offeringId")]   long OfferingId,
    [property: JsonPropertyName("agentName")]    string AgentName,
    [property: JsonPropertyName("agentAddress")] string AgentAddress,
    [property: JsonPropertyName("offeringName")] string OfferingName,
    [property: JsonPropertyName("hiresThen")]    long HiresThen,
    [property: JsonPropertyName("hiresNow")]     long HiresNow,
    [property: JsonPropertyName("delta")]        long Delta,
    [property: JsonPropertyName("marketplaceVersion")]
        string MarketplaceVersion = "v1");

// ── New Agents ────────────────────────────────────────────────────────────────

public record NewAgentRow(
    [property: JsonPropertyName("address")]          string Address,
    [property: JsonPropertyName("name")]             string Name,
    [property: JsonPropertyName("marketplace")]      string Marketplace,
    [property: JsonPropertyName("firstSeenAt")]      string FirstSeenAt,
    [property: JsonPropertyName("offeringCount")]    int OfferingCount);

public record NewAgentsBlock(
    [property: JsonPropertyName("count")]   int Count,
    [property: JsonPropertyName("agents")]  IReadOnlyList<NewAgentRow> Agents);

// ── Churn Rate ────────────────────────────────────────────────────────────────

public record ChurnRate(
    [property: JsonPropertyName("rate")]           double Rate,
    [property: JsonPropertyName("churnedCount")]   int ChurnedCount,
    [property: JsonPropertyName("baselineCount")]  int BaselineCount);

// ── Cohort Survival ───────────────────────────────────────────────────────────

public record CohortSurvivalRow(
    [property: JsonPropertyName("cohortWeek")]     string CohortWeek,
    [property: JsonPropertyName("cohortStart")]    string CohortStart,
    [property: JsonPropertyName("cohortSize")]     int CohortSize,
    [property: JsonPropertyName("surviving")]      int Surviving,
    [property: JsonPropertyName("survivalRate")]   double SurvivalRate);

// ── New Resources (R7-IDEA-C cross-agent Resource index) ─────────────────────

public record NewResource(
    [property: JsonPropertyName("agentName")]     string AgentName,
    [property: JsonPropertyName("agentAddress")]  string AgentAddress,
    [property: JsonPropertyName("name")]          string Name,
    [property: JsonPropertyName("url")]           string Url,
    [property: JsonPropertyName("description")]   string Description,
    [property: JsonPropertyName("firstSeenAt")]   string FirstSeenAt,
    [property: JsonPropertyName("marketplaceVersion")] string MarketplaceVersion);

// ── Saturation Map ────────────────────────────────────────────────────────────

public record SaturationMapRow(
    [property: JsonPropertyName("category")]       string Category,
    [property: JsonPropertyName("total")]          int Total,
    [property: JsonPropertyName("saturatedCount")] int SaturatedCount,
    [property: JsonPropertyName("saturationPct")]  double SaturationPct);

// ── Digest Result ─────────────────────────────────────────────────────────────

public class DigestResult
{
    [JsonPropertyName("windowDays")]
    public int WindowDays { get; set; }

    [JsonPropertyName("windowStart")]
    public string WindowStart { get; set; } = "";

    [JsonPropertyName("snapshotComparison")]
    public string SnapshotComparison { get; set; } = "";

    [JsonPropertyName("partial")]
    public bool Partial { get; set; }

    [JsonPropertyName("newOfferings")]
    public IReadOnlyList<NewOffering> NewOfferings { get; set; } = Array.Empty<NewOffering>();

    // v1.7.4: Resources observed for the first time within the window, across
    // every indexed V2 agent. Backed by agent_resources (R7-IDEA-C).
    [JsonPropertyName("newResources")]
    public IReadOnlyList<NewResource> NewResources { get; set; } = Array.Empty<NewResource>();

    [JsonPropertyName("gainers")]
    public IReadOnlyList<OfferingGainer> Gainers { get; set; } = Array.Empty<OfferingGainer>();

    [JsonPropertyName("newAgents")]
    public NewAgentsBlock NewAgents { get; set; } = new NewAgentsBlock(0, Array.Empty<NewAgentRow>());

    [JsonPropertyName("churnRate")]
    public ChurnRate ChurnRate { get; set; } = new ChurnRate(0.0, 0, 0);

    [JsonPropertyName("cohortSurvival"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CohortSurvivalRow>? CohortSurvival { get; set; }

    [JsonPropertyName("saturationMap")]
    public IReadOnlyList<SaturationMapRow> SaturationMap { get; set; } = Array.Empty<SaturationMapRow>();

    [JsonPropertyName("computedAt")]
    public string ComputedAt { get; set; } = "";
}
