namespace ACP_Metabot.Api.Models;

public record Offering(
    long Id,
    string AgentAddress,
    string AgentName,
    string OfferingName,
    string Description,
    string? RequirementSchemaJson,
    double PriceUsdc,
    string PriceType,
    bool IsPrivate,
    string Chain,
    string ContentHash,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    long UsageCount,
    long AgentJobCount,
    string MarketplaceVersion = "v1",
    // v1.5 tombstone state. IsRemoved=true means the offering has been
    // missing from upstream fetches for longer than the marketplace's
    // tombstone threshold. Reactivates automatically on reappearance.
    bool IsRemoved = false,
    DateTime? RemovedAt = null,
    // v1.10 Phase 2 T3a: deliverable schema persisted from the V2
    // marketplace's AcpAgentOffering.deliverable shape (object | string).
    // NULL for V1-source rows — the upstream /api/metrics/skills endpoint
    // doesn't expose the deliverable schema, and never will until V1 is
    // sunset. Consumers must handle both populated and null forms.
    string? DeliverableSchemaJson = null);
