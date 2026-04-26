using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

// Inline reputation field embedded in every /search result. Null while the
// indexer is warming up before the first successful fetch.
public record ReputationSummary(
    [property: JsonPropertyName("score")]          int Score,
    [property: JsonPropertyName("offeringHires")]  long OfferingHires,
    [property: JsonPropertyName("agentTotalJobs")] long AgentTotalJobs);

// Per-offering breakdown returned by /agentReputation.
public record AgentOfferingReputation(
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("score")]      int Score,
    [property: JsonPropertyName("hires")]      long Hires,
    [property: JsonPropertyName("percentile")] double Percentile);

// Top-level /agentReputation response. `Offering` is set when the caller
// passed an offeringName; otherwise `Offerings` contains every offering
// owned by the agent (sorted by hires desc).
public record AgentReputationResult(
    [property: JsonPropertyName("agentAddress")]    string AgentAddress,
    [property: JsonPropertyName("agentName")]       string AgentName,
    [property: JsonPropertyName("agentScore")]      int AgentScore,
    [property: JsonPropertyName("agentTotalJobs")]  long AgentTotalJobs,
    [property: JsonPropertyName("agentPercentile")] double AgentPercentile,
    [property: JsonPropertyName("computedAt")]      string ComputedAt,
    [property: JsonPropertyName("offering"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        AgentOfferingReputation? Offering,
    [property: JsonPropertyName("offerings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<AgentOfferingReputation>? Offerings);
