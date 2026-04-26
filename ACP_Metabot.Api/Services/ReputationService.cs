using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

// Owns the score / percentile math for marketplace reputation. Keeps two
// sorted arrays in memory — usage_count (per-offering) and agent_job_count
// (per-agent, deduped) — refreshed at the end of every successful indexer
// cycle. Sort cost on ~34K rows is ~5ms, so a full rebuild per tick is
// cheaper than maintaining incremental state.
public class ReputationService
{
    private readonly object _lock = new();

    // Sorted ascending. Used for percentile lookups via binary search.
    private long[] _sortedUsageCounts = Array.Empty<long>();
    private long[] _sortedAgentJobCounts = Array.Empty<long>();

    private long _maxUsageCount;
    private long _maxAgentJobCount;
    private DateTime _refreshedAtUtc;

    public bool IsReady => _sortedUsageCounts.Length > 0;
    public DateTime RefreshedAtUtc => _refreshedAtUtc;

    public void RebuildFromCorpus(IReadOnlyList<Offering> offerings)
    {
        if (offerings.Count == 0)
        {
            // Empty corpus = upstream fetch failed or first boot. Don't blow
            // away an existing cache — keep serving the previous values.
            return;
        }

        var usageSorted = new long[offerings.Count];
        for (int i = 0; i < offerings.Count; i++) usageSorted[i] = offerings[i].UsageCount;
        Array.Sort(usageSorted);

        // Per-agent dedupe: agent_job_count is an agent-level metric, so we
        // count each agent once even if they own multiple offerings.
        var agentSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var agentJobs = new List<long>(capacity: offerings.Count);
        foreach (var o in offerings)
        {
            if (agentSeen.Add(o.AgentAddress)) agentJobs.Add(o.AgentJobCount);
        }
        var agentJobsArr = agentJobs.ToArray();
        Array.Sort(agentJobsArr);

        lock (_lock)
        {
            _sortedUsageCounts = usageSorted;
            _sortedAgentJobCounts = agentJobsArr;
            _maxUsageCount = usageSorted.Length > 0 ? usageSorted[^1] : 0;
            _maxAgentJobCount = agentJobsArr.Length > 0 ? agentJobsArr[^1] : 0;
            _refreshedAtUtc = DateTime.UtcNow;
        }
    }

    // Inline reputation surfaced in /search responses. Null only while the
    // first indexer cycle is in flight (corpus empty).
    public ReputationSummary? BuildSearchSummary(Offering o)
    {
        if (!IsReady) return null;
        long maxUsage;
        lock (_lock) maxUsage = _maxUsageCount;
        return new ReputationSummary(
            Score: ScoreLog(o.UsageCount, maxUsage),
            OfferingHires: o.UsageCount,
            AgentTotalJobs: o.AgentJobCount);
    }

    // Full /agentReputation response. Caller passes every offering owned by
    // the agent (already loaded from the repo). offeringName is optional —
    // when supplied, the response contains a single `offering` block;
    // otherwise an `offerings` array sorted by hires descending.
    public AgentReputationResult Build(IReadOnlyList<Offering> agentOfferings, string? offeringName)
    {
        if (agentOfferings.Count == 0)
            throw new ArgumentException("agentOfferings must contain at least one row");

        // Snapshot the caches under the lock so concurrent rebuilds can't
        // tear our percentile / max reads.
        long[] sortedUsage, sortedAgentJobs;
        long maxUsage, maxAgentJobs;
        DateTime refreshedAt;
        lock (_lock)
        {
            sortedUsage = _sortedUsageCounts;
            sortedAgentJobs = _sortedAgentJobCounts;
            maxUsage = _maxUsageCount;
            maxAgentJobs = _maxAgentJobCount;
            refreshedAt = _refreshedAtUtc;
        }

        var first = agentOfferings[0];
        var agentScore = ScoreLog(first.AgentJobCount, maxAgentJobs);
        var agentPct = Percentile(first.AgentJobCount, sortedAgentJobs);

        AgentOfferingReputation? singleOffering = null;
        IReadOnlyList<AgentOfferingReputation>? allOfferings = null;

        if (!string.IsNullOrWhiteSpace(offeringName))
        {
            var match = agentOfferings.FirstOrDefault(o =>
                string.Equals(o.OfferingName, offeringName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new KeyNotFoundException("offering not found for this agent");
            singleOffering = new AgentOfferingReputation(
                Name: match.OfferingName,
                Score: ScoreLog(match.UsageCount, maxUsage),
                Hires: match.UsageCount,
                Percentile: Percentile(match.UsageCount, sortedUsage));
        }
        else
        {
            allOfferings = agentOfferings
                .OrderByDescending(o => o.UsageCount)
                .Select(o => new AgentOfferingReputation(
                    Name: o.OfferingName,
                    Score: ScoreLog(o.UsageCount, maxUsage),
                    Hires: o.UsageCount,
                    Percentile: Percentile(o.UsageCount, sortedUsage)))
                .ToArray();
        }

        return new AgentReputationResult(
            AgentAddress: first.AgentAddress,
            AgentName: first.AgentName,
            AgentScore: agentScore,
            AgentTotalJobs: first.AgentJobCount,
            AgentPercentile: agentPct,
            ComputedAt: refreshedAt.ToString("O"),
            Offering: singleOffering,
            Offerings: allOfferings);
    }

    // Log-scaled because the corpus follows a power law: the top offering
    // has six-digit usage, the long tail is near zero. Linear scaling would
    // collapse the bulk of agents into score=0–1.
    private static int ScoreLog(long count, long max)
    {
        if (max <= 0 || count <= 0) return 0;
        var raw = 100.0 * Math.Log(1.0 + count) / Math.Log(1.0 + max);
        return (int)Math.Round(Math.Clamp(raw, 0.0, 100.0));
    }

    // Linear percentile: percent of corpus with usage <= our value.
    // 1 d.p. precision is plenty given the corpus size.
    private static double Percentile(long count, long[] sortedAsc)
    {
        if (sortedAsc.Length == 0) return 0.0;
        var idx = Array.BinarySearch(sortedAsc, count);
        int rank;
        if (idx < 0)
        {
            rank = ~idx; // insertion point = first index strictly greater
        }
        else
        {
            // Walk forward over duplicates so equal values get the highest rank.
            rank = idx + 1;
            while (rank < sortedAsc.Length && sortedAsc[rank] == count) rank++;
        }
        return Math.Round(100.0 * rank / sortedAsc.Length, 1);
    }
}
