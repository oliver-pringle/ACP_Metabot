namespace ACP_Metabot.Api.Models;

/// <summary>
/// v1.10 Phase 1 negative-filter + future-flag bag for /v1/search and
/// /v1/searchAgents. All fields are optional and default to "no filter".
/// Phase 1 implements: ExcludeRequirements, ExcludeAgents, ExcludeChains,
/// MaxPriceUsd, IncludeResources. Expand + IncludeRisk are accepted but
/// no-op in Phase 1; wired to behaviour in Phase 3 (LLM rewriter +
/// AgentRiskScorer).
///
/// Phase 2 T4 adds RequiresField + ProducesField — sub-offering schema
/// filters backed by the schema_facets table. When non-null, the search
/// service intersects post-rerank candidates with the set of offering_ids
/// whose requirement / deliverable schema declares that field name. Field
/// names are matched case-insensitively (rows are stored lowercased).
/// </summary>
public sealed record SearchFilters(
    IReadOnlyList<string>? ExcludeRequirements = null,
    IReadOnlyList<string>? ExcludeAgents = null,
    IReadOnlyList<string>? ExcludeChains = null,
    double? MaxPriceUsd = null,
    bool IncludeResources = true,
    bool Expand = false,
    bool IncludeRisk = false,
    // v1.10 Phase 2 sub-offering filters
    string? RequiresField = null,
    string? ProducesField = null);
