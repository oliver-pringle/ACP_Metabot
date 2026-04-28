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
    [property: JsonPropertyName("flags")]        ReputationFlags Flags);

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
    long HighestScannedBlock);

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
