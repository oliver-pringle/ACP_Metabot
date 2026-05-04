using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

public record AgentSearchHitOffering(
    [property: JsonPropertyName("offeringName")] string OfferingName,
    [property: JsonPropertyName("priceUsdc")]    double PriceUsdc,
    [property: JsonPropertyName("marketplaceVersion")] string MarketplaceVersion);

public record AgentSearchHit(
    [property: JsonPropertyName("agentAddress")]   string AgentAddress,
    [property: JsonPropertyName("agentName")]      string AgentName,
    // v1.7: was BM25 (lower=better) in v1.6; now post-rerank cosine score
    // (opaque higher=better). Callers should sort by it, not interpret.
    [property: JsonPropertyName("score")]          double Score,
    [property: JsonPropertyName("totalOfferings")] int TotalOfferings,
    // v1.7: shape changed from string[] to records.
    [property: JsonPropertyName("topOfferings")]   IReadOnlyList<AgentSearchHitOffering> TopOfferings,
    [property: JsonPropertyName("totalJobs")]      long TotalJobs,
    // v1.7 NEW — backward-compat mirror for old string-array consumers.
    [property: JsonPropertyName("topOfferingNames")] IReadOnlyList<string> TopOfferingNames,
    // v1.7 NEW — sorted subset of {"v1","v2"} where the agent has ≥1 active offering.
    [property: JsonPropertyName("marketplaces")]   IReadOnlyList<string> Marketplaces,
    // v1.7 NEW — "v1" | "v2" | "tied" | "none".
    [property: JsonPropertyName("dominantMarketplace")] string DominantMarketplace,
    // v1.7 NEW — from agent_reputation_cache; null when uncached.
    [property: JsonPropertyName("agentScore"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? AgentScore);

// Single on-chain job event for an agent. Surfaced by /v1/agentRecentJobs.
public record AgentJobRecord(
    [property: JsonPropertyName("jobId")]        string JobId,
    [property: JsonPropertyName("createdAt")]    string CreatedAt,
    [property: JsonPropertyName("status")]       string Status,
    [property: JsonPropertyName("counterparty")] string Counterparty,
    [property: JsonPropertyName("amountUsdc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        decimal? AmountUsdc);
