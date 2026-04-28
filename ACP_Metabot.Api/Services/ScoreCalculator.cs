using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

public class ScoreCalculator
{
    private const int CompletionMinTerminal     = 5;
    private const int DisputeMinTerminal        = 5;
    private const int ResponseTimeMinSamples30d = 3;

    // Weight constants — must sum to 1.0.
    private const double CompletionWeight   = 0.30;
    private const double DisputeWeight      = 0.25;
    private const double RecencyWeight      = 0.15;
    private const double Volume30dWeight    = 0.20;
    private const double ResponseTimeWeight = 0.10;

    public record ScoreInputs(
        ChainScanResult Chain,
        DateTime? OffChainLastActiveAt,
        long Volume30dCorpusMax,
        DateTime NowUtc);

    public record ComputedScore(
        SubScoreSet SubScores,
        int Overall,
        bool IsColdStart,
        bool AnyInsufficient);

    public ComputedScore Compute(ScoreInputs input)
    {
        var chain = input.Chain;
        var totalTerminal = chain.Completed + chain.Rejected + chain.Expired;
        var isColdStart = totalTerminal == 0 && chain.TotalJobs == 0;

        var completion = ComputeCompletion(chain, totalTerminal);
        var dispute    = ComputeDispute(chain, totalTerminal);
        var recency    = ComputeRecency(input.OffChainLastActiveAt, chain.LastJobSubmittedAt, input.NowUtc);
        var volume     = ComputeVolume30d(chain.CompletedLast30d, input.Volume30dCorpusMax);
        var response   = ComputeResponseTime(chain.AvgResponseSeconds30d, chain.ResponseTimeSampleCount30d);

        var overall = (int)Math.Round(
            CompletionWeight   * completion.Score +
            DisputeWeight      * dispute.Score +
            RecencyWeight      * recency.Score +
            Volume30dWeight    * volume.Score +
            ResponseTimeWeight * response.Score);

        return new ComputedScore(
            SubScores: new SubScoreSet(completion, dispute, recency, volume, response),
            Overall: overall,
            IsColdStart: isColdStart,
            AnyInsufficient:
                completion.InsufficientData || dispute.InsufficientData || response.InsufficientData);
    }

    private static SubScore ComputeCompletion(ChainScanResult chain, long totalTerminal)
    {
        if (totalTerminal < CompletionMinTerminal)
        {
            return new SubScore(
                Value: 0,
                Score: 50,
                Percentile: 0,
                Evidence: $"Only {totalTerminal} terminal jobs (min {CompletionMinTerminal}); using neutral 50.",
                InsufficientData: true);
        }
        var rate = (double)chain.Completed / totalTerminal;
        return new SubScore(
            Value: rate,
            Score: (int)Math.Round(rate * 100),
            Percentile: 0, // filled in by ReputationService percentile pass
            Evidence: $"{chain.Completed}/{totalTerminal} terminal jobs completed.",
            InsufficientData: false);
    }

    private static SubScore ComputeDispute(ChainScanResult chain, long totalTerminal)
    {
        if (totalTerminal < DisputeMinTerminal)
        {
            return new SubScore(
                Value: 0,
                Score: 50,
                Percentile: 0,
                Evidence: $"Only {totalTerminal} terminal jobs (min {DisputeMinTerminal}); using neutral 50.",
                InsufficientData: true);
        }
        var disputed = chain.Rejected + chain.Expired;
        var rate = (double)disputed / totalTerminal;
        return new SubScore(
            Value: rate,
            Score: (int)Math.Round((1.0 - rate) * 100),
            Percentile: 0,
            Evidence: $"{disputed}/{totalTerminal} terminal jobs rejected or expired (excluding self-rejections).",
            InsufficientData: false);
    }

    private static SubScore ComputeRecency(DateTime? offChain, DateTime? chainFallback, DateTime nowUtc)
    {
        var lastActive = offChain ?? chainFallback;
        if (lastActive is null)
        {
            return new SubScore(
                Value: 0, Score: 0, Percentile: 0,
                Evidence: "No activity recorded.",
                InsufficientData: false);
        }
        var hours = (nowUtc - lastActive.Value).TotalHours;
        int score;
        if (hours <= 72) score = 100;
        else if (hours >= 90 * 24) score = 0;
        else
        {
            var range = 90.0 * 24 - 72;
            var t = (hours - 72) / range;
            score = (int)Math.Round((1.0 - t) * 100);
        }
        return new SubScore(
            Value: hours,
            Score: score,
            Percentile: 0,
            Evidence: $"Last active {hours:F1}h ago ({(offChain != null ? "off-chain" : "chain fallback")}).",
            InsufficientData: false);
    }

    private static SubScore ComputeVolume30d(long completedLast30d, long corpusMax)
    {
        if (corpusMax <= 0)
        {
            return new SubScore(
                Value: completedLast30d,
                Score: 50,
                Percentile: 0,
                Evidence: $"{completedLast30d} jobs completed in last 30d; corpus max not yet known, using neutral 50.",
                InsufficientData: false);
        }
        var raw = 100.0 * Math.Log(1 + completedLast30d) / Math.Log(1 + corpusMax);
        return new SubScore(
            Value: completedLast30d,
            Score: (int)Math.Round(Math.Clamp(raw, 0, 100)),
            Percentile: 0,
            Evidence: $"{completedLast30d} jobs completed in last 30d (corpus max {corpusMax}, log-scaled).",
            InsufficientData: false);
    }

    private static SubScore ComputeResponseTime(double? avgSeconds, long sampleCount)
    {
        if (avgSeconds is null || sampleCount < ResponseTimeMinSamples30d)
        {
            return new SubScore(
                Value: avgSeconds ?? 0,
                Score: 50,
                Percentile: 0,
                Evidence: $"Only {sampleCount} response samples in last 30d (min {ResponseTimeMinSamples30d}); using neutral 50.",
                InsufficientData: true);
        }
        var minutes = avgSeconds.Value / 60.0;
        int score;
        if (minutes <= 5) score = 100;
        else if (minutes >= 60 * 24) score = 0;
        else
        {
            var range = 60.0 * 24 - 5;
            var t = (minutes - 5) / range;
            score = (int)Math.Round((1.0 - t) * 100);
        }
        return new SubScore(
            Value: avgSeconds.Value,
            Score: score,
            Percentile: 0,
            Evidence: $"Avg response time {minutes:F1}min over {sampleCount} samples (last 30d).",
            InsufficientData: false);
    }
}
