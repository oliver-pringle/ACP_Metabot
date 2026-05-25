namespace ACP_Metabot.Api.Services;

// v1.10.1 marketplaceGap V1/V2 split.
//
// Refresh now takes a marketplace tag per offering. Three rollups are
// precomputed at Refresh time so PerCategory(marketplace) is an O(1)
// dictionary lookup — no per-call recomputation.
//
// Q3 resolution (cross-marketplace near-dup edges): the inner O(N²) cosine
// pass still runs once per category, across the FULL category bucket
// (V1+V2 mixed). A V1 offering near-duped to a V2 offering bumps both ids
// in the same flat _nearDup dict. The marketplace dimension only changes
// the inclusion mask when each rollup is built — not which edges are
// detected. This keeps the "both" numbers identical to pre-v1.10.1
// behaviour while enabling the new v1/v2 slices.
//
// PerCategory default flipped to "v2" per Q2 — but the only PerCategory
// callers today (MarketplaceGapService, DigestService) pass marketplace
// explicitly, so the default is a safety net for future callers, not a
// silent BC shift.
public class SaturationCalculator
{
    private readonly double _threshold;
    private Dictionary<string, List<(long Id, float[] Emb)>> _byCategory = new();
    private Dictionary<long, string> _marketplaceById = new();
    private Dictionary<long, int> _nearDup = new();
    private List<CategorySaturation> _rollupV1 = new();
    private List<CategorySaturation> _rollupV2 = new();
    private List<CategorySaturation> _rollupBoth = new();

    public SaturationCalculator(double threshold) { _threshold = threshold; }

    public void Refresh(IEnumerable<(long id, string category, string marketplace, float[] embedding)> corpus)
    {
        _byCategory = new Dictionary<string, List<(long, float[])>>(StringComparer.Ordinal);
        _marketplaceById = new Dictionary<long, string>();
        foreach (var (id, cat, mkt, emb) in corpus)
        {
            if (!_byCategory.TryGetValue(cat, out var list))
                _byCategory[cat] = list = new List<(long, float[])>();
            list.Add((id, emb));
            _marketplaceById[id] = NormalizeMarketplaceTag(mkt);
        }

        _nearDup = new Dictionary<long, int>();
        foreach (var (cat, list) in _byCategory)
        {
            for (int i = 0; i < list.Count; i++)
            {
                int count = 0;
                for (int j = 0; j < list.Count; j++)
                {
                    if (i == j) continue;
                    if (Cosine(list[i].Emb, list[j].Emb) >= _threshold) count++;
                }
                _nearDup[list[i].Id] = count;
            }
        }

        _rollupV1   = BuildRollup("v1");
        _rollupV2   = BuildRollup("v2");
        _rollupBoth = BuildRollup("both");
    }

    private List<CategorySaturation> BuildRollup(string slice)
    {
        return _byCategory
            .Select(kv =>
            {
                var included = slice == "both"
                    ? kv.Value
                    : kv.Value.Where(o => _marketplaceById.GetValueOrDefault(o.Id) == slice).ToList();
                var saturated = included.Count(o => _nearDup.GetValueOrDefault(o.Id) > 0);
                return new CategorySaturation(
                    kv.Key,
                    included.Count,
                    saturated,
                    included.Count == 0 ? 0.0 : (double)saturated / included.Count);
            })
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ToList();
    }

    public int NearDuplicateCount(long offeringId, string category)
        => _nearDup.GetValueOrDefault(offeringId);

    public int CategorySize(string category)
        => _byCategory.TryGetValue(category, out var list) ? list.Count : 0;

    public IReadOnlyList<CategorySaturation> PerCategory(string marketplace = "v2")
    {
        return NormalizeMarketplaceTag(marketplace) switch
        {
            "v1"   => _rollupV1,
            "both" => _rollupBoth,
            _      => _rollupV2,
        };
    }

    public static string NormalizeMarketplaceTag(string? mkt)
    {
        if (string.IsNullOrWhiteSpace(mkt)) return "v1";
        var trimmed = mkt.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "v1"   => "v1",
            "v2"   => "v2",
            "both" => "both",
            _      => "v1",
        };
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}

public record CategorySaturation(string Category, int Total, int SaturatedCount, double SaturationPct);
