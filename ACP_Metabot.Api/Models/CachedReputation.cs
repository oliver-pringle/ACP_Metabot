using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

// Wire shape returned by /agentReputation and /v1/agentReputation.
public record AgentReputationResultV2(
    [property: JsonPropertyName("agentAddress")] string AgentAddress,
    [property: JsonPropertyName("agentName")]    string AgentName,
    [property: JsonPropertyName("agentScore")]   int AgentScore,
    [property: JsonPropertyName("computedAt")]   string ComputedAt,
    [property: JsonPropertyName("windowDays")]   int WindowDays,
    [property: JsonPropertyName("subScores")]    SubScoreSet SubScores,
    [property: JsonPropertyName("rawCounts")]    RawCounts RawCounts,
    [property: JsonPropertyName("flags")]        ReputationFlags Flags,
    [property: JsonPropertyName("trajectory"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<HistoryPoint>? Trajectory = null);

// One day's reputation snapshot. Returned inline on agentReputation responses
// (last 30 days) and as the array body on GET /v1/agentReputationHistory.
public record HistoryPoint(
    [property: JsonPropertyName("date")]       string Date,           // YYYY-MM-DD UTC
    [property: JsonPropertyName("agentScore")] int AgentScore,
    [property: JsonPropertyName("subScores")]  SubScoreSet? SubScores);

public record SubScoreSet(
    [property: JsonPropertyName("completion")]   SubScore Completion,
    [property: JsonPropertyName("dispute")]      SubScore Dispute,
    [property: JsonPropertyName("recency")]      SubScore Recency,
    [property: JsonPropertyName("volume30d")]    SubScore Volume30d,
    [property: JsonPropertyName("responseTime")] SubScore ResponseTime);

public record SubScore(
    [property: JsonPropertyName("value")]            double Value,
    [property: JsonPropertyName("score")]            int Score,
    [property: JsonPropertyName("percentile")]       double Percentile,
    [property: JsonPropertyName("evidence")]         string Evidence,
    [property: JsonPropertyName("insufficientData")] bool InsufficientData);

public record RawCounts(
    [property: JsonPropertyName("totalJobs")]        long TotalJobs,
    [property: JsonPropertyName("completed")]        long Completed,
    [property: JsonPropertyName("rejected")]         long Rejected,
    [property: JsonPropertyName("expired")]          long Expired,
    [property: JsonPropertyName("completedLast30d")] long CompletedLast30d,
    [property: JsonPropertyName("lastActiveAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? LastActiveAt);

public record ReputationFlags(
    [property: JsonPropertyName("isColdStart")]      bool IsColdStart,
    [property: JsonPropertyName("insufficientData")] bool InsufficientData,
    [property: JsonPropertyName("warmCacheHit")]     bool WarmCacheHit);

// Internal: what ChainEventScanner returns. NOT serialised.
//
// TotalJobs is the EXACT JobCreated count for the scanned window (step 1). It is
// independent of the bounded status scan (steps 2-3) and of whether that status
// scan completed. ReputationService folds it into a cumulative running total
// (see ComputeEffectiveTotalJobs) so a per-window delta count never clobbers an
// agent's accumulated lifetime total.
//
// Step1Trustworthy is false when the step-1 JobCreated scan had to abandon one
// or more chunks after exhausting RPC retries — meaning TotalJobs may be an
// undercount. The persist path must NOT accumulate or advance the checkpoint on
// an untrustworthy scan (it would freeze an undercount or, with the cumulative
// fold, drop jobs); instead it keeps the prior total + prior checkpoint so the
// next pass re-scans the same window cleanly.
//
// WasColdStartScan is true when this scan started at the cold-start floor
// (fromBlock <= deployBlock) rather than from a delta checkpoint — a cold-start
// re-establishes the baseline total (replace), a delta accumulates (add).
//
// StatusScanComplete is false when the bounded status scan (steps 2-3) failed or
// was partial; it only affects the InsufficientData label, never the count.
public record ChainScanResult(
    string AgentAddress,
    long TotalJobs,
    long Completed,
    long Rejected,
    long Expired,
    long CompletedLast30d,
    DateTime? LastJobSubmittedAt,
    double? AvgResponseSeconds30d,
    long ResponseTimeSampleCount30d,
    long HighestScannedBlock,
    bool Step1Trustworthy = true,
    bool WasColdStartScan = false,
    bool StatusScanComplete = true);

// Internal: what's persisted to agent_reputation_cache.
public record CachedReputationRow(
    string AgentAddress,
    string AgentName,
    int AgentScore,
    string SubScoresJson,
    string RawCountsJson,
    string FlagsJson,
    DateTime ComputedAt,
    long LastScannedBlock,
    string Source);
