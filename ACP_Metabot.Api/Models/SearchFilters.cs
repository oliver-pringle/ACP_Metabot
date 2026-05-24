namespace ACP_Metabot.Api.Models;

/// <summary>
/// v1.10 Phase 1 negative-filter + future-flag bag for /v1/search and
/// /v1/searchAgents. All fields are optional and default to "no filter".
/// Phase 1 implements: ExcludeRequirements, ExcludeAgents, ExcludeChains,
/// MaxPriceUsd, IncludeResources. Expand + IncludeRisk are accepted but
/// no-op in Phase 1; wired to behaviour in Phase 3 (LLM rewriter +
/// AgentRiskScorer).
/// </summary>
public sealed record SearchFilters(
    IReadOnlyList<string>? ExcludeRequirements = null,
    IReadOnlyList<string>? ExcludeAgents = null,
    IReadOnlyList<string>? ExcludeChains = null,
    double? MaxPriceUsd = null,
    bool IncludeResources = true,
    bool Expand = false,
    bool IncludeRisk = false);
