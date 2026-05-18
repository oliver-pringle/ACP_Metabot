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
public class MarketplaceGapService
{
    private readonly SaturationCalculator _saturation;
    private readonly CategoryService _categories;

    public MarketplaceGapService(SaturationCalculator saturation, CategoryService categories)
    {
        _saturation = saturation;
        _categories = categories;
    }

    public MarketplaceGapResponse Analyze(string? category, int limit)
    {
        var per = _saturation.PerCategory();
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
        return new MarketplaceGapResponse(
            Opportunities: rows.Take(capped).ToList(),
            Filter:        string.IsNullOrWhiteSpace(category) ? null : category,
            Note:          per.Count == 0
                ? "saturationMap not yet computed — indexer has not run a full embed cycle since boot"
                : null,
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

public record MarketplaceGapRequest(string? Category, int? Limit);

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
    string? Note,
    DateTime ComputedAt);
