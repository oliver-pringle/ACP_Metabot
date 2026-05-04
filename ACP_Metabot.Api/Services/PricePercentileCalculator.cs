namespace ACP_Metabot.Api.Services;

public record PricePercentile(int? Value, int PeerN, bool LowN);

public class PricePercentileCalculator
{
    private readonly int _lowNThreshold;
    private Dictionary<(string Category, string Marketplace), List<double>> _peerPrices = new();

    public PricePercentileCalculator(int lowNThreshold = 5)
    {
        _lowNThreshold = lowNThreshold;
    }

    public void Refresh(IEnumerable<(long id, string category, string marketplace, double price)> corpus)
    {
        var dict = new Dictionary<(string, string), List<double>>();
        foreach (var (_, cat, mv, price) in corpus)
        {
            var key = (cat, mv);
            if (!dict.TryGetValue(key, out var list))
                dict[key] = list = new List<double>();
            list.Add(price);
        }
        foreach (var list in dict.Values) list.Sort();
        _peerPrices = dict;
    }

    public PricePercentile Compute(long offeringId, string category, string marketplace, double price)
    {
        var key = (category, marketplace);
        if (!_peerPrices.TryGetValue(key, out var sorted))
            return new PricePercentile(null, 0, true);

        var peerCount = sorted.Count - 1;
        if (peerCount < _lowNThreshold)
            return new PricePercentile(null, peerCount, true);

        int strictlyLess = 0;
        bool selfRemoved = false;
        foreach (var p in sorted)
        {
            if (!selfRemoved && p == price) { selfRemoved = true; continue; }
            if (p < price) strictlyLess++;
        }

        var pct = (int)Math.Round(100.0 * strictlyLess / peerCount);
        return new PricePercentile(pct, peerCount, false);
    }
}
