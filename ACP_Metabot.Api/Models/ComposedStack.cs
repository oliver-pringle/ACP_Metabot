namespace ACP_Metabot.Api.Models;

public record StackEntry(
    string OfferingName,
    string AgentName,
    string AgentAddress,
    double PriceUsdc,
    string Role);

public record ComposedStack(
    string Rationale,
    IReadOnlyList<StackEntry> Stack,
    double TotalPriceUsdc);
