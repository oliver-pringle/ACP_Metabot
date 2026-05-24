namespace ACP_Metabot.Api.Models;

/// <summary>
/// v1.10 Phase 1: a free Resource hit surfaced alongside paid offerings in
/// the unified <c>/v1/search</c> response. Built by <see cref="ACP_Metabot.Api.Program"/>
/// from <see cref="ACP_Metabot.Api.Data.AgentResourcesRepository.AgentResourceRow"/>
/// hits returned by <c>SearchHybridAsync</c>.
/// </summary>
public sealed record ResourceMatch(
    long Id,
    string AgentAddress,
    string AgentName,
    string Name,
    string Description,
    string Url,
    string MarketplaceVersion);
