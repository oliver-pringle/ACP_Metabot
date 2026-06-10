using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.Extensions.Logging;

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

    // New behavioural-mode state. Layers on top of legacy hire-count math
    // without removing it (legacy is still used by /search inline summaries
    // and /agent/{address} browse).
    private readonly AgentReputationCacheRepository _cacheRepo;
    private readonly AgentReputationHistoryRepository _historyRepo;
    private readonly LifetimeSnapshotRepository    _snapshotRepo;
    private readonly ChainEventScanner             _scanner;
    private readonly AcpOffChainClient             _offChain;
    private readonly ScoreCalculator               _calculator;
    private readonly OfferingRepository            _offeringRepo;
    private readonly ILogger<ReputationService>    _logger;
    // Trajectory length attached to agentReputation responses (paid hires +
    // public cache reads). 30 is the canonical "trend" window; longer
    // trajectories are available via GET /v1/agentReputationHistory.
    private const int TrajectoryDays = 30;

    // One semaphore per agent, lazily created. Prevents two concurrent paid
    // hires for the same agent triggering two chain scans.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _computeLocks = new();

    // Per-metric sorted arrays (by absolute score) for percentile lookups in
    // the behavioural response. Rebuilt at the end of each warmer pass and
    // every lazy compute.
    private readonly object _percentileLock = new();
    private int[] _completionScoreSorted = Array.Empty<int>();
    private int[] _disputeScoreSorted    = Array.Empty<int>();
    private int[] _recencyScoreSorted    = Array.Empty<int>();
    private int[] _volume30dScoreSorted  = Array.Empty<int>();
    private int[] _responseScoreSorted   = Array.Empty<int>();

    public ReputationService(
        AgentReputationCacheRepository cacheRepo,
        AgentReputationHistoryRepository historyRepo,
        LifetimeSnapshotRepository snapshotRepo,
        ChainEventScanner scanner,
        AcpOffChainClient offChain,
        ScoreCalculator calculator,
        OfferingRepository offeringRepo,
        ILogger<ReputationService> logger)
    {
        _cacheRepo    = cacheRepo;
        _historyRepo  = historyRepo;
        _snapshotRepo = snapshotRepo;
        _scanner      = scanner;
        _offChain     = offChain;
        _calculator   = calculator;
        _offeringRepo = offeringRepo;
        _logger       = logger;
    }

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

    /// <summary>
    /// Agent-level summary used by /v1/searchAgents. We only have the
    /// agent's total jobs (no offering-level usage_count), so the score
    /// is keyed off the agent's own throughput rather than a single
    /// offering. <c>OfferingHires</c> is left at 0 because the response
    /// is per-agent, not per-offering.
    /// </summary>
    public ReputationSummary? BuildSearchSummary(string agentAddress, long agentTotalJobs)
    {
        if (!IsReady) return null;
        long maxAgentJobs;
        lock (_lock) maxAgentJobs = _maxAgentJobCount;
        return new ReputationSummary(
            Score: ScoreLog(agentTotalJobs, maxAgentJobs),
            OfferingHires: 0,
            AgentTotalJobs: agentTotalJobs);
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

    // -------------------------------------------------------------------------
    // New behavioural-mode methods
    // -------------------------------------------------------------------------

    public async Task<AgentReputationResultV2> GetOrComputeAsync(
        string agentAddress, CancellationToken ct)
    {
        var addr = agentAddress.ToLowerInvariant();
        var nowUtc = DateTime.UtcNow;
        var cached = await _cacheRepo.GetAsync(addr, nowUtc);
        if (cached is not null)
        {
            return await DeserializeWithTrajectoryAsync(cached, warmHit: cached.Source == "warmer");
        }

        var sem = _computeLocks.GetOrAdd(addr, _ => new SemaphoreSlim(1, 1));
        bool acquired = await sem.WaitAsync(TimeSpan.FromSeconds(5), ct);
        if (!acquired)
        {
            // Another thread is computing; re-read after timeout.
            var late = await _cacheRepo.GetAsync(addr, DateTime.UtcNow);
            if (late is not null) return await DeserializeWithTrajectoryAsync(late, warmHit: false);
            throw new InvalidOperationException("concurrent compute timed out");
        }
        try
        {
            // Re-check cache under the lock (another thread may have just written).
            cached = await _cacheRepo.GetAsync(addr, DateTime.UtcNow);
            if (cached is not null)
                return await DeserializeWithTrajectoryAsync(cached, warmHit: cached.Source == "warmer");

            return await ComputeAsync(addr, source: "lazy", ct);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Cache-only read used by the public GET /v1/agentReputation. Returns
    /// null on miss so the handler can issue 404 + hint without triggering
    /// compute. Attaches trajectory on hit so the public surface gets the
    /// same shape as the paid SKU.
    /// </summary>
    public async Task<AgentReputationResultV2?> GetCachedAsync(string agentAddress)
    {
        var addr = agentAddress.ToLowerInvariant();
        var cached = await _cacheRepo.GetAsync(addr, DateTime.UtcNow);
        if (cached is null) return null;
        return await DeserializeWithTrajectoryAsync(cached, warmHit: cached.Source == "warmer");
    }

    private async Task<AgentReputationResultV2> DeserializeWithTrajectoryAsync(
        CachedReputationRow row, bool warmHit)
    {
        var result = Deserialize(row, warmHit);
        var trajectory = await _historyRepo.GetTrajectoryAsync(row.AgentAddress, TrajectoryDays);
        return result with { Trajectory = trajectory.Count > 0 ? trajectory : null };
    }

    /// <summary>
    /// Pure: fold a single scan's per-window JobCreated count into the agent's
    /// cumulative running total. The scanner returns the count for the scanned
    /// block window only (a delta scan sees just the new jobs); persisting that
    /// raw value would clobber an agent's accumulated total on every warmer pass.
    ///
    /// Case table (windowCount, priorTotal, wasColdStart, step1Trustworthy):
    ///  • untrustworthy (step-1 dropped chunks): keep prior (or the partial window
    ///    when there's no prior) — the scanner won't advance the checkpoint, so the
    ///    next clean pass re-scans the same window and supersedes this value.
    ///  • cold-start scan (started at the deploy-block floor): the window is a fresh
    ///    baseline, but it is CAPPED to the last MaxFirstScanDays — so it can be a
    ///    subset of lifetime when a prior total survives (e.g. an operator zeroes
    ///    last_scanned_block without deleting the row). Take the monotonic FLOOR
    ///    (max of prior and window) so a 90d-capped re-scan never clobbers an
    ///    accumulated total downward, and never adds (which would double-count the
    ///    overlap). A genuine reset is a full-row DELETE -> priorTotal null -> window.
    ///  • delta scan: accumulate — priorTotal + the new window's jobs.
    /// Monotonic by construction on a finalized chain (jobs never un-happen); both
    /// the window count and any prior are <= true lifetime, so max/accumulate can
    /// never exceed it (given no double-count, which the checkpoint policy ensures).
    /// </summary>
    public static long ComputeEffectiveTotalJobs(
        long windowCount, long? priorTotal, bool wasColdStartScan, bool step1Trustworthy)
    {
        if (!step1Trustworthy)
            return priorTotal ?? windowCount;
        if (wasColdStartScan)
            return Math.Max(priorTotal ?? 0, windowCount);
        return (priorTotal ?? 0) + windowCount;
    }

    // Computes a fresh reputation, persists it, and returns the V2 wire object.
    public async Task<AgentReputationResultV2> ComputeAsync(string addr, string source, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var fromBlock = (await _cacheRepo.GetLastScannedBlockAsync(addr) ?? 0) + 1;
        var chain = await _scanner.ScanAgentAsync(addr, fromBlock, nowUtc, ct);

        // Fold the per-window JobCreated count into a cumulative running total so a
        // delta scan (which only sees jobs created since last_scanned_block) can
        // never clobber an agent's accumulated lifetime total down to a day's worth.
        // GetCachedTotalJobsAsync bypasses the 24h TTL and is null only on a true
        // from-scratch cold start. An untrustworthy step-1 (dropped chunks) keeps the
        // prior total — paired with the scanner leaving the checkpoint unadvanced, so
        // the next pass re-scans the same window and accumulates cleanly (no double
        // count). See ComputeEffectiveTotalJobs for the full case table.
        var priorTotal = await _cacheRepo.GetCachedTotalJobsAsync(addr);
        var effectiveTotal = ComputeEffectiveTotalJobs(
            chain.TotalJobs, priorTotal, chain.WasColdStartScan, chain.Step1Trustworthy);
        if (effectiveTotal != chain.TotalJobs)
            _logger.LogInformation(
                "[reputation] {addr} totalJobs window={Window} prior={Prior} -> cumulative={Eff} (coldStart={CS} step1Trust={T})",
                addr, chain.TotalJobs, priorTotal, effectiveTotal,
                chain.WasColdStartScan, chain.Step1Trustworthy);
        // The score must see the cumulative total (isColdStart + volume), not the delta.
        var chainForScore = chain with { TotalJobs = effectiveTotal };

        AcpOffChainAgent? offChain = null;
        try { offChain = await _offChain.GetAgentAsync(addr, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[reputation] off-chain fetch failed for {addr}", addr); }

        long volumeCorpusMax = ComputeVolume30dCorpusMax();

        var inputs = new ScoreCalculator.ScoreInputs(chainForScore, offChain?.LastActiveAt, volumeCorpusMax, nowUtc);
        var score = _calculator.Compute(inputs);

        var rawCounts = new RawCounts(
            TotalJobs:        effectiveTotal,
            Completed:        chain.Completed,
            Rejected:         chain.Rejected,
            Expired:          chain.Expired,
            CompletedLast30d: chain.CompletedLast30d,
            LastActiveAt:     (offChain?.LastActiveAt ?? chain.LastJobSubmittedAt)?.ToString("O"));

        var flags = new ReputationFlags(
            IsColdStart:      score.IsColdStart,
            InsufficientData: score.AnyInsufficient || !chain.StatusScanComplete,
            WarmCacheHit:     source == "warmer");

        var subScoresJson = System.Text.Json.JsonSerializer.Serialize(score.SubScores);
        var rawCountsJson = System.Text.Json.JsonSerializer.Serialize(rawCounts);
        var flagsJson     = System.Text.Json.JsonSerializer.Serialize(flags);

        var name = offChain?.Name ?? "";
        if (string.IsNullOrEmpty(name))
        {
            var byAgent = await _offeringRepo.ListByAgentAsync(addr);
            if (byAgent.Count > 0) name = byAgent[0].AgentName;
        }

        await _cacheRepo.UpsertAsync(new CachedReputationRow(
            AgentAddress:     addr,
            AgentName:        name,
            AgentScore:       score.Overall,
            SubScoresJson:    subScoresJson,
            RawCountsJson:    rawCountsJson,
            FlagsJson:        flagsJson,
            ComputedAt:       nowUtc,
            LastScannedBlock: chain.HighestScannedBlock,
            Source:           source));

        // Persist daily history snapshot so trajectory is queryable. Same-day
        // re-computes overwrite — the day's value is the most recent compute.
        await _historyRepo.UpsertAsync(addr, DateOnly.FromDateTime(nowUtc),
            score.Overall, subScoresJson, rawCountsJson);

        // Refresh percentile arrays after each compute so the next caller sees
        // the up-to-date corpus.
        await RebuildPercentilesFromCacheAsync(nowUtc);

        var trajectory = await _historyRepo.GetTrajectoryAsync(addr, TrajectoryDays);
        return new AgentReputationResultV2(
            AgentAddress: addr,
            AgentName:    name,
            AgentScore:   score.Overall,
            ComputedAt:   nowUtc.ToString("O"),
            WindowDays:   90,
            SubScores:    AttachPercentiles(score.SubScores),
            RawCounts:    rawCounts,
            Flags:        flags,
            Trajectory:   trajectory.Count > 0 ? trajectory : null);
    }

    public async Task RebuildPercentilesFromCacheAsync(DateTime nowUtc)
    {
        var rows = await _cacheRepo.ListAllForPercentilesAsync(nowUtc);
        if (rows.Count == 0) return;

        var completion = new List<int>(rows.Count);
        var dispute    = new List<int>(rows.Count);
        var recency    = new List<int>(rows.Count);
        var volume     = new List<int>(rows.Count);
        var response   = new List<int>(rows.Count);

        foreach (var row in rows)
        {
            SubScoreSet? set = null;
            try { set = System.Text.Json.JsonSerializer.Deserialize<SubScoreSet>(row.SubScoresJson); }
            catch { continue; }
            if (set is null) continue;
            if (!set.Completion.InsufficientData)   completion.Add(set.Completion.Score);
            if (!set.Dispute.InsufficientData)      dispute.Add(set.Dispute.Score);
            if (!set.Recency.InsufficientData)      recency.Add(set.Recency.Score);
            if (!set.Volume30d.InsufficientData)    volume.Add(set.Volume30d.Score);
            if (!set.ResponseTime.InsufficientData) response.Add(set.ResponseTime.Score);
        }

        var c = completion.ToArray(); Array.Sort(c);
        var d = dispute.ToArray();    Array.Sort(d);
        var r = recency.ToArray();    Array.Sort(r);
        var v = volume.ToArray();     Array.Sort(v);
        var p = response.ToArray();   Array.Sort(p);

        lock (_percentileLock)
        {
            _completionScoreSorted = c;
            _disputeScoreSorted    = d;
            _recencyScoreSorted    = r;
            _volume30dScoreSorted  = v;
            _responseScoreSorted   = p;
        }
    }

    private SubScoreSet AttachPercentiles(SubScoreSet src)
    {
        int[] c, d, r, v, p;
        lock (_percentileLock)
        {
            c = _completionScoreSorted;
            d = _disputeScoreSorted;
            r = _recencyScoreSorted;
            v = _volume30dScoreSorted;
            p = _responseScoreSorted;
        }
        return new SubScoreSet(
            Completion:   src.Completion   with { Percentile = Pct(c, src.Completion.Score) },
            Dispute:      src.Dispute      with { Percentile = Pct(d, src.Dispute.Score) },
            Recency:      src.Recency      with { Percentile = Pct(r, src.Recency.Score) },
            Volume30d:    src.Volume30d    with { Percentile = Pct(v, src.Volume30d.Score) },
            ResponseTime: src.ResponseTime with { Percentile = Pct(p, src.ResponseTime.Score) });
    }

    private static double Pct(int[] sortedAsc, int score)
    {
        if (sortedAsc.Length == 0) return 0;
        var idx = Array.BinarySearch(sortedAsc, score);
        int rank = idx < 0 ? ~idx : idx + 1;
        while (rank < sortedAsc.Length && sortedAsc[rank] == score) rank++;
        return Math.Round(100.0 * rank / sortedAsc.Length, 1);
    }

    private long ComputeVolume30dCorpusMax()
    {
        int[] v;
        lock (_percentileLock) v = _volume30dScoreSorted;
        if (v.Length == 0) return 0; // first-boot path; calculator returns neutral 50
        // Approximate corpus max from the top score's log-inverse position.
        return (long)Math.Max(100, v[^1] * 10L);
    }

    private AgentReputationResultV2 Deserialize(CachedReputationRow row, bool warmHit)
    {
        var subScores = System.Text.Json.JsonSerializer.Deserialize<SubScoreSet>(row.SubScoresJson)!;
        var rawCounts = System.Text.Json.JsonSerializer.Deserialize<RawCounts>(row.RawCountsJson)!;
        var flagsRaw  = System.Text.Json.JsonSerializer.Deserialize<ReputationFlags>(row.FlagsJson)!;
        var flags     = flagsRaw with { WarmCacheHit = warmHit };
        return new AgentReputationResultV2(
            AgentAddress: row.AgentAddress,
            AgentName:    row.AgentName,
            AgentScore:   row.AgentScore,
            ComputedAt:   row.ComputedAt.ToString("O"),
            WindowDays:   90,
            SubScores:    AttachPercentiles(subScores),
            RawCounts:    rawCounts,
            Flags:        flags);
    }
}
