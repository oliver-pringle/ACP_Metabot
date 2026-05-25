namespace ACP_Metabot.Api.Services;

// marketplaceGap ($0.30) — "where should I build a new ACP bot?"
//
// Repackages the saturationMap that v1.7 today/digest already computes into
// a structured opportunity ranking. The saturationMap leaks through /digest
// for free; this offering's value-add is the opportunity score + per-row
// recommendation tag + the category description (joined from categories.json).
//
// Opportunity score = total * (1 - saturationPct)^2
//   - rewards low-saturation categories (the squared term punishes saturated
//     categories sharply: 50% saturated halves the score, 80% drops it 25x)
//   - requires non-zero total (a category with no offerings collapses to 0
//     regardless of saturation — it's not an "opportunity", it's a void)
//   - units are roughly "offerings of headroom"
//
// recommendationTag is a fixed taxonomy so the deliverable is machine-readable:
//   saturated_avoid          — sat >= 0.70, skip
//   high_volume_low_density  — total >= 100, sat < 0.40 — prime spot
//   medium_volume_emerging   — total >= 30,  sat < 0.50 — solid niche
//   niche_underserved        — total <  30,  sat < 0.40 — small but open
//   balanced                 — anything else
//
// v1.10.1 — accepts a marketplace slice (v1 | v2 | both). Default flipped
// to "v2" per spec Q2 (BC-shift documented in offering description). Same
// taxonomy applied per-slice (Q1 — no separate thresholds).
public class MarketplaceGapService
{
    private readonly SaturationCalculator _saturation;
    private readonly CategoryService _categories;

    public MarketplaceGapService(SaturationCalculator saturation, CategoryService categories)
    {
        _saturation = saturation;
        _categories = categories;
    }

    public MarketplaceGapResponse Analyze(string? category, int limit, string marketplace = "v2")
    {
        // Service-layer coercion. The endpoint already rejects unknowns with
        // 400; this is defensive for any internal caller that bypassed it.
        // Unknown → "v2" (the documented marketplaceGap default per Q2), not
        // "v1" — NormalizeMarketplaceTag's "v1" fallback is for raw corpus
        // tags where legacy V1 is the safe historical default; here the
        // intent is the offering's advertised default.
        var resolvedMarketplace = (marketplace ?? "v2").Trim().ToLowerInvariant();
        if (resolvedMarketplace is not ("v1" or "v2" or "both"))
            resolvedMarketplace = "v2";

        var per = _saturation.PerCategory(resolvedMarketplace);
        var descByName = _categories.Categories.ToDictionary(
            c => c.Name, c => c.Description, StringComparer.Ordinal);

        IEnumerable<MarketplaceGapRow> rows = per.Select(c =>
        {
            var sat = c.SaturationPct;            // 0..1
            var score = c.Total * Math.Pow(1.0 - sat, 2.0);
            return new MarketplaceGapRow(
                Category:          c.Category,
                Description:       descByName.GetValueOrDefault(c.Category, ""),
                Total:             c.Total,
                SaturatedCount:    c.SaturatedCount,
                SaturationPct:     sat,
                OpportunityScore:  Math.Round(score, 2),
                RecommendationTag: Tag(c.Total, sat));
        });

        if (!string.IsNullOrWhiteSpace(category))
        {
            rows = rows.Where(r => r.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            rows = rows.OrderByDescending(r => r.OpportunityScore);
        }

        var capped = Math.Clamp(limit, 1, 20);
        var materialised = rows.Take(capped).ToList();

        // Q4: separate the cold-boot "no corpus" note from the per-slice
        // "no offerings in this marketplace" note. per.Count == 0 means
        // the indexer hasn't run yet; per.All(Total == 0) means the slice
        // is empty (e.g. V2 corpus not yet ingested).
        string? note;
        if (per.Count == 0)
            note = "saturationMap not yet computed — indexer has not run a full embed cycle since boot";
        else if (per.All(r => r.Total == 0))
            note = $"no {resolvedMarketplace} offerings in current corpus snapshot";
        else
            note = null;

        return new MarketplaceGapResponse(
            Opportunities: materialised,
            Filter:        string.IsNullOrWhiteSpace(category) ? null : category,
            Marketplace:   resolvedMarketplace,
            Note:          note,
            ComputedAt:    DateTime.UtcNow);
    }

    private static string Tag(int total, double sat) => (total, sat) switch
    {
        ( _,    >= 0.70) => "saturated_avoid",
        (>= 100, < 0.40) => "high_volume_low_density",
        (>=  30, < 0.50) => "medium_volume_emerging",
        (<   30, < 0.40) => "niche_underserved",
        _                => "balanced",
    };
}

public record MarketplaceGapRequest(string? Category, int? Limit, string? Marketplace);

public record MarketplaceGapRow(
    string Category,
    string Description,
    int Total,
    int SaturatedCount,
    double SaturationPct,
    double OpportunityScore,
    string RecommendationTag);

public record MarketplaceGapResponse(
    IReadOnlyList<MarketplaceGapRow> Opportunities,
    string? Filter,
    string Marketplace,
    string? Note,
    DateTime ComputedAt);
