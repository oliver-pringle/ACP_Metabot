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

    [JsonPropertyName("priceUsdc")]
    public double PriceUsdc { get; set; }

    [JsonPropertyName("priceType")]
    public string PriceType { get; set; } = "fixed";

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("chain")]
    public string Chain { get; set; } = "base";
}
