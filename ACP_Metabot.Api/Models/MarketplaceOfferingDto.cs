using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

// Source format consumed by IMarketplaceSource implementations.
// The JSON-file source maps directly; a future HTTP-API source would
// translate from upstream schema into this DTO.
public class MarketplaceOfferingDto
{
    [JsonPropertyName("agentAddress")]
    public string AgentAddress { get; set; } = "";

    [JsonPropertyName("agentName")]
    public string AgentName { get; set; } = "";

    [JsonPropertyName("offeringName")]
    public string OfferingName { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("requirementSchema")]
    public JsonElement? RequirementSchema { get; set; }

    // v1.10 Phase 2 T3a: deliverable schema. V2 marketplace exposes this
    // on AcpAgentOffering.deliverable (Record<string, unknown> | string —
    // the same dual shape as requirements). V1's list endpoint does NOT
    // expose this, so V1 sources leave it null and V1-sourced rows persist
    // with deliverable_schema_json IS NULL.
    [JsonPropertyName("deliverableSchema")]
    public JsonElement? DeliverableSchema { get; set; }

    [JsonPropertyName("priceUsdc")]
    public double PriceUsdc { get; set; }

    [JsonPropertyName("priceType")]
    public string PriceType { get; set; } = "fixed";

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("chain")]
    public string Chain { get; set; } = "base";

    // Lifetime job counts pulled straight from the upstream API. Used to
    // derive the agentReputation score in v1; aggregated into daily
    // snapshots for v2 time-window deltas.
    [JsonPropertyName("usageCount")]
    public long UsageCount { get; set; }

    [JsonPropertyName("agentJobCount")]
    public long AgentJobCount { get; set; }
}
