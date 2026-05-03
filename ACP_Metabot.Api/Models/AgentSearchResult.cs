using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

// Aggregated agent-level result for /v1/searchAgents. Built by grouping
// offering-level BM25 hits by agent and surfacing each agent's best score
// + top three offering names for context.
public record AgentSearchHit(
    [property: JsonPropertyName("agentAddress")] string AgentAddress,
    [property: JsonPropertyName("agentName")]    string AgentName,
    [property: JsonPropertyName("score")]        double Score,
    [property: JsonPropertyName("totalOfferings")] int TotalOfferings,
    [property: JsonPropertyName("topOfferings")] IReadOnlyList<string> TopOfferings,
    [property: JsonPropertyName("totalJobs")]    long TotalJobs);

// Single on-chain job event for an agent. Surfaced by /v1/agentRecentJobs.
public record AgentJobRecord(
    [property: JsonPropertyName("jobId")]        string JobId,
    [property: JsonPropertyName("createdAt")]    string CreatedAt,
    [property: JsonPropertyName("status")]       string Status,
    [property: JsonPropertyName("counterparty")] string Counterparty,
    [property: JsonPropertyName("amountUsdc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        decimal? AmountUsdc);
