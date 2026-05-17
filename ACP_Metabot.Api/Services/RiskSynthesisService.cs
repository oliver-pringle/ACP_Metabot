using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

// v1.8 Portfolio Risk Bot — deterministic synthesis layer.
//
// Takes the four peer-bot responses (any of which may be null/unavailable)
// and folds them into a single 0-100 score + grade + summary. No LLM —
// pure weighted blend with documented thresholds. The weights live in
// riskScoreRubric Resource so buyer agents can pre-validate.
//
// Weights (sum to 1.0 when all four are available; renormalised across
// the available subset otherwise):
//   healthFactor 0.30   (lending positions — biggest near-term solvency risk)
//   approvals    0.30   (token approvals — exploit / drain risk)
//   mevExposure  0.20   (sandwich risk on outgoing swaps)
//   reputation   0.20   (on-chain behavioural — soft signal)
//
// Per-component score interpretation: 100 = safest, 0 = worst. Grade
// thresholds: ≥85 A, 70 B, 55 C, 40 D, <40 F.
//
// FALLBACK POLICY: when a peer returns null, that component is marked
// status='unavailable' AND its weight is reallocated proportionally across
// the remaining components — so missing one source doesn't bias the
// composite, just widens the confidence interval (which we don't expose
// numerically in v1).
public sealed class RiskSnapshotResult
{
    public required string Wallet { get; init; }
    public required string Chain { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public required int RiskScore { get; init; }
    public required string RiskGrade { get; init; }
    public required string Summary { get; init; }
    public required Dictionary<string, RiskComponent> Components { get; init; }
    public required List<string> Fallbacks { get; init; }
}

public sealed class RiskComponent
{
    public required int Score { get; init; }
    public required string Source { get; init; }
    public required string Details { get; init; }
    public required string Status { get; init; }
    public int? HighRiskCount { get; init; }
}

public sealed class RiskSynthesisService
{
    public const double WeightHealthFactor = 0.30;
    public const double WeightApprovals    = 0.30;
    public const double WeightMevExposure  = 0.20;
    public const double WeightReputation   = 0.20;

    private readonly IRiskPeerClients _peers;
    private readonly AgentReputationCacheRepository _repCache;
    private readonly ILogger<RiskSynthesisService> _log;

    public RiskSynthesisService(IRiskPeerClients peers,
        AgentReputationCacheRepository repCache,
        ILogger<RiskSynthesisService> log)
    {
        _peers = peers;
        _repCache = repCache;
        _log = log;
    }

    public async Task<RiskSnapshotResult> ComputeAsync(string wallet, string chain, CancellationToken ct)
    {
        var w = wallet.Trim().ToLowerInvariant();

        // Fan out the four peer calls in parallel. None throw — they return
        // null on transport / non-2xx / parse failure, which we translate
        // into a 'unavailable' component status downstream.
        var hfTask  = _peers.GetHealthFactorAsync(w, chain, ct);
        var apprTask = _peers.GetApprovalsQuoteAsync(w, chain, ct);
        var mevTask = _peers.GetMevScoreAsync(w, ct);
        var repTask = _repCache.GetAsync(w, DateTime.UtcNow);
        await Task.WhenAll(hfTask, apprTask, mevTask, repTask);

        var components = new Dictionary<string, RiskComponent>();
        var fallbacks = new List<string>();

        components["healthFactor"] = ParseHealthFactor(hfTask.Result, fallbacks);
        components["approvals"]    = ParseApprovals(apprTask.Result, fallbacks);
        components["mevExposure"]  = ParseMev(mevTask.Result, fallbacks);
        components["reputation"]   = ParseReputation(repTask.Result, fallbacks);

        var score = ComposeScore(components);
        var grade = ToGrade(score);
        var summary = BuildSummary(score, grade, components, fallbacks);

        return new RiskSnapshotResult
        {
            Wallet      = w,
            Chain       = chain,
            GeneratedAt = DateTime.UtcNow,
            RiskScore   = score,
            RiskGrade   = grade,
            Summary     = summary,
            Components  = components,
            Fallbacks   = fallbacks,
        };
    }

    // ── Component parsers — every one is null-safe ──────────────────────────

    private RiskComponent ParseHealthFactor(JsonDocument? doc, List<string> fallbacks)
    {
        // LiquidGuard /v1/internal/hf shape (canonical contract — DTO-drift
        // resistant; we only read fields and treat missing as defaults):
        //   { wallet, chain, healthFactors: [{ protocol, hf, status }], ... }
        if (doc is null)
        {
            fallbacks.Add("healthFactor");
            return new RiskComponent
            {
                Score = 50, Source = "LiquidGuard", Details = "Peer unavailable.",
                Status = "unavailable",
            };
        }
        try
        {
            var root = doc.RootElement;
            double minHf = double.PositiveInfinity;
            int hfCount = 0;
            var lines = new List<string>();
            if (root.TryGetProperty("healthFactors", out var hfs) && hfs.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in hfs.EnumerateArray())
                {
                    if (!p.TryGetProperty("hf", out var hfEl)) continue;
                    if (hfEl.ValueKind != JsonValueKind.Number) continue;
                    var hf = hfEl.GetDouble();
                    if (double.IsInfinity(hf) || double.IsNaN(hf)) continue;
                    var protocol = p.TryGetProperty("protocol", out var prot) ? prot.GetString() ?? "?" : "?";
                    lines.Add($"{protocol} HF {hf:0.00}");
                    if (hf < minHf) minHf = hf;
                    hfCount++;
                }
            }
            if (hfCount == 0)
            {
                return new RiskComponent
                {
                    Score = 80, Source = "LiquidGuard",
                    Details = "No open lending positions found — no liquidation risk.",
                    Status = "fresh",
                };
            }
            // HF buckets: ≥3.0 → 100, ≥2.0 → 90, ≥1.5 → 75, ≥1.25 → 55, ≥1.1 → 35, <1.1 → 10.
            int s = minHf >= 3.0 ? 100
                  : minHf >= 2.0 ? 90
                  : minHf >= 1.5 ? 75
                  : minHf >= 1.25 ? 55
                  : minHf >= 1.1 ? 35
                  : 10;
            return new RiskComponent
            {
                Score = s, Source = "LiquidGuard",
                Details = string.Join(", ", lines) + ".",
                Status = "fresh",
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[risk] healthFactor parse failed");
            fallbacks.Add("healthFactor");
            return new RiskComponent
            {
                Score = 50, Source = "LiquidGuard",
                Details = "Peer response unparseable.",
                Status = "unavailable",
            };
        }
    }

    private RiskComponent ParseApprovals(JsonDocument? doc, List<string> fallbacks)
    {
        // RevokeBot /v1/internal/quote shape:
        //   { wallet, chain, totalApprovals, highRiskCount, summary, approvals[] }
        if (doc is null)
        {
            fallbacks.Add("approvals");
            return new RiskComponent
            {
                Score = 50, Source = "RevokeBot",
                Details = "Peer unavailable.",
                HighRiskCount = 0,
                Status = "unavailable",
            };
        }
        try
        {
            var root = doc.RootElement;
            int high = root.TryGetProperty("highRiskCount", out var hr) && hr.ValueKind == JsonValueKind.Number
                ? hr.GetInt32() : 0;
            int total = root.TryGetProperty("totalApprovals", out var ta) && ta.ValueKind == JsonValueKind.Number
                ? ta.GetInt32() : 0;
            // Buckets: 0 → 100, 1 → 70, 2 → 55, 3 → 40, 4 → 25, ≥5 → 10.
            int s = high switch
            {
                0 => 100,
                1 => 70,
                2 => 55,
                3 => 40,
                4 => 25,
                _ => 10,
            };
            var details = high == 0
                ? $"{total} active approvals, 0 flagged high-risk."
                : $"{high} of {total} approvals flagged high-risk.";
            return new RiskComponent
            {
                Score = s, Source = "RevokeBot",
                Details = details,
                HighRiskCount = high,
                Status = "fresh",
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[risk] approvals parse failed");
            fallbacks.Add("approvals");
            return new RiskComponent
            {
                Score = 50, Source = "RevokeBot",
                Details = "Peer response unparseable.",
                HighRiskCount = 0,
                Status = "unavailable",
            };
        }
    }

    private RiskComponent ParseMev(JsonDocument? doc, List<string> fallbacks)
    {
        // MEVProtect /v1/internal/mev_score shape:
        //   { wallet, mevScore (0..100), sandwichesObserved, windowDays, ... }
        if (doc is null)
        {
            fallbacks.Add("mevExposure");
            return new RiskComponent
            {
                Score = 75, Source = "MEVProtect",
                Details = "Peer unavailable — defaulting to 75 (assume normal exposure).",
                Status = "unavailable",
            };
        }
        try
        {
            var root = doc.RootElement;
            // MEVProtect's mev_score is already 0..100 where 100 = safest;
            // it tracks sandwich-attack incidence directly.
            int rawScore = 75;
            if (root.TryGetProperty("mevScore", out var ms) && ms.ValueKind == JsonValueKind.Number)
                rawScore = Math.Clamp(ms.GetInt32(), 0, 100);
            int sandwiches = root.TryGetProperty("sandwichesObserved", out var sw)
                && sw.ValueKind == JsonValueKind.Number
                ? sw.GetInt32() : 0;
            var details = sandwiches == 0
                ? "No sandwich attempts detected in window."
                : $"{sandwiches} sandwich attempts detected in window.";
            return new RiskComponent
            {
                Score = rawScore, Source = "MEVProtect",
                Details = details,
                Status = "fresh",
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[risk] mev parse failed");
            fallbacks.Add("mevExposure");
            return new RiskComponent
            {
                Score = 75, Source = "MEVProtect",
                Details = "Peer response unparseable.",
                Status = "unavailable",
            };
        }
    }

    private RiskComponent ParseReputation(CachedReputationRow? row, List<string> fallbacks)
    {
        // 50 = neutral baseline when the wallet is not also a registered ACP agent.
        if (row is null)
        {
            return new RiskComponent
            {
                Score = 50, Source = "TheMetaBot",
                Details = "Wallet is not a registered ACP agent — neutral baseline.",
                Status = "fresh",
            };
        }
        return new RiskComponent
        {
            Score = Math.Clamp(row.AgentScore, 0, 100),
            Source = "TheMetaBot",
            Details = $"On-chain behavioural reputation {row.AgentScore}/100 (computed {row.ComputedAt:O}).",
            Status = "fresh",
        };
    }

    // ── Compositor + grade ──────────────────────────────────────────────────

    public static int ComposeScore(Dictionary<string, RiskComponent> components)
    {
        // Renormalise weights over the available subset. An "unavailable"
        // component still has a placeholder score in the dict but gets
        // dropped from the weighted blend.
        double totalWeight = 0;
        double sum = 0;
        void Accumulate(string name, double w)
        {
            if (!components.TryGetValue(name, out var c)) return;
            if (c.Status == "unavailable") return;
            sum += c.Score * w;
            totalWeight += w;
        }
        Accumulate("healthFactor", WeightHealthFactor);
        Accumulate("approvals", WeightApprovals);
        Accumulate("mevExposure", WeightMevExposure);
        Accumulate("reputation", WeightReputation);
        if (totalWeight <= 0) return 50; // all peers unavailable — neutral grade
        return (int)Math.Round(sum / totalWeight);
    }

    public static string ToGrade(int score)
        => score >= 85 ? "A"
         : score >= 70 ? "B"
         : score >= 55 ? "C"
         : score >= 40 ? "D"
         : "F";

    private static string BuildSummary(int score, string grade,
        Dictionary<string, RiskComponent> components, List<string> fallbacks)
    {
        var parts = new List<string>();
        if (components.TryGetValue("healthFactor", out var hf) && hf.Status != "unavailable")
            parts.Add(hf.Details);
        if (components.TryGetValue("approvals", out var apr) && apr.Status != "unavailable")
            parts.Add(apr.Details);
        if (components.TryGetValue("mevExposure", out var mev) && mev.Status != "unavailable")
            parts.Add(mev.Details);
        if (components.TryGetValue("reputation", out var rep) && rep.Status != "unavailable")
            parts.Add(rep.Details);

        var lead = grade switch
        {
            "A" => "Strong overall risk posture",
            "B" => "Healthy overall risk posture",
            "C" => "Mixed risk posture",
            "D" => "Elevated risk — investigate before transacting",
            _   => "High risk — recommend de-risking actions",
        };
        var fallbackNote = fallbacks.Count == 0
            ? ""
            : $" Note: {string.Join(", ", fallbacks)} unavailable; score computed from the remaining sources.";
        return $"{lead} (grade {grade}, {score}). " + string.Join(" ", parts) + fallbackNote;
    }
}
