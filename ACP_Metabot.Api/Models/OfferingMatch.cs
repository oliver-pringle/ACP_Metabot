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
    double Score);
