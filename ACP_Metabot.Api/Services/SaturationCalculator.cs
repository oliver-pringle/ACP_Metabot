namespace ACP_Metabot.Api.Services;

public class SaturationCalculator
{
    private readonly double _threshold;
    private Dictionary<string, List<(long Id, float[] Emb)>> _byCategory = new();
    private Dictionary<long, int> _nearDup = new();
    private List<CategorySaturation> _rollup = new();

    public SaturationCalculator(double threshold) { _threshold = threshold; }

    public void Refresh(IEnumerable<(long id, string category, float[] embedding)> corpus)
    {
        _byCategory = new Dictionary<string, List<(long, float[])>>(StringComparer.Ordinal);
        foreach (var (id, cat, emb) in corpus)
        {
            if (!_byCategory.TryGetValue(cat, out var list))
                _byCategory[cat] = list = new List<(long, float[])>();
            list.Add((id, emb));
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

        _rollup = _byCategory
            .Select(kv => new CategorySaturation(
                kv.Key,
                kv.Value.Count,
                kv.Value.Count(o => _nearDup.GetValueOrDefault(o.Id) > 0),
                kv.Value.Count == 0
                    ? 0.0
                    : (double)kv.Value.Count(o => _nearDup.GetValueOrDefault(o.Id) > 0) / kv.Value.Count))
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ToList();
    }

    public int NearDuplicateCount(long offeringId, string category)
        => _nearDup.GetValueOrDefault(offeringId);

    public int CategorySize(string category)
        => _byCategory.TryGetValue(category, out var list) ? list.Count : 0;

    public IReadOnlyList<CategorySaturation> PerCategory() => _rollup;

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
