// riskAttestPro v1.0 Task 6 — KEYSTONE 7-lane orchestrator.
//
// Fans out the seven cross-bot signals in parallel, maps each peer response to
// a unified `components` dictionary shape, enforces a 4-of-7 fresh-signal floor,
// composites the available scores as an arithmetic mean, then produces:
//
//   - verdict (STRONG_BUY ≥85 / OK ≥70 / CAUTION ≥55 / AVOID ≥40 / INSUFFICIENT_DATA <40)
//   - grade   (A/B/C/D/F same thresholds)
//   - componentsHash (SHA256 of canonical sorted-keys JSON; LLM cache key)
//   - executiveSummary (Haiku narration via RiskAttestProLlm, deterministic-cached)
//   - markdownReport (base64 of RiskAttestProMarkdown.Generate)
//   - recommendations (deterministic from approvals.highRiskCount + HF score
//                      + witness.verifyVerdict)
//
// LANES (per spec table):
//   1. healthFactor  ← IRiskPeerClients.GetHealthFactorAsync         (LiquidGuard)
//   2. approvals     ← IRiskPeerClients.GetApprovalsQuoteAsync       (RevokeBot)
//   3. mev           ← IRiskPeerClients.GetMevScoreAsync             (MEVProtect)
//   4. reputation    ← IRiskReputationLookup.GetAsync                (TheMetaBot internal)
//   5. arena         ← IRiskArenaLookup.GetParticipantAsync          (TheArenaBot)
//   6. witness       ← IWitnessBotClient.ManifestByAgentAsync        (TheWitnessBot)
//   7. trajectory    ← RiskTrajectoryStore.LookupStrideAsync × {-7,-14,-21d}
//
// FLOOR: if fewer than 4 of 7 lanes return Status=="fresh", throw
// InsufficientSignalsException — the endpoint handler maps that to a 502
// INSUFFICIENT_SIGNALS envelope (per spec).

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.Extensions.Logging;

namespace ACP_Metabot.Api.Services;

// ── Public types ────────────────────────────────────────────────────────────

public sealed record RiskAttestProResult(
    string Verdict,
    int ScorePro,
    string Grade,
    JsonElement Components,
    string ExecutiveSummary,
    JsonElement Recommendations,
    string MarkdownReportBase64,
    string Wallet,
    string Chain,
    string GeneratedAt,
    string ExpiresAt,
    string[] SourcesQueried,
    string[] SourcesUnavailable,
    string ComponentsHash,
    // v1.0.3 — on-chain EAS attestation. Null when publish lane is unreachable
    // or returns 502 (graceful degradation per the contract). Cache-miss only:
    // a cached result returned by the endpoint reads these from the persisted
    // row. Schema UID is canonical seller-result schema (the new
    // riskAttestPro AgentRisk schema 0xb7038e6b... is registered but not yet
    // ABI-encoded — slated for v1.0.4).
    RiskAttestPublishedAttestation? Attestation = null);

public sealed record RiskAttestPublishedAttestation(
    string AttestationUid,
    string TxHash,
    long BlockNumber,
    string SchemaUid,
    string SchemaUri,
    string BaseScanUrl);

public sealed class InsufficientSignalsException : Exception
{
    public int FreshCount { get; }
    public InsufficientSignalsException(int got)
        : base($"riskAttestPro requires at least 4 of 7 fresh sources; got {got}")
    {
        FreshCount = got;
    }
}

/// <summary>
/// Test-shaped seam over <see cref="AgentReputationCacheRepository.GetAsync"/>.
/// Tests register a fake; production registers a delegating adapter
/// (<see cref="RiskReputationLookup"/>) that calls the real repo.
/// </summary>
public interface IRiskReputationLookup
{
    Task<CachedReputationRow?> GetAsync(string wallet, CancellationToken ct);
}

public sealed class RiskReputationLookup : IRiskReputationLookup
{
    private readonly AgentReputationCacheRepository _repo;
    public RiskReputationLookup(AgentReputationCacheRepository repo) => _repo = repo;
    public Task<CachedReputationRow?> GetAsync(string wallet, CancellationToken ct)
        => _repo.GetAsync(wallet.ToLowerInvariant(), DateTime.UtcNow);
}

/// <summary>
/// Test-shaped seam over <see cref="TheArenaBotClient"/>. v1.0 reads the
/// shared `arenaLeaderboardStatus` Resource and lets callers map the response
/// to a per-wallet IsParticipant flag (full per-wallet drill-down lands when
/// ArenaBot exposes a wallet-keyed Resource — v1.1 work).
/// </summary>
public interface IRiskArenaLookup
{
    Task<JsonDocument?> GetParticipantAsync(string wallet, CancellationToken ct);
}

public sealed class RiskArenaLookup : IRiskArenaLookup
{
    private readonly TheArenaBotClient _arena;
    public RiskArenaLookup(TheArenaBotClient arena) => _arena = arena;
    public Task<JsonDocument?> GetParticipantAsync(string wallet, CancellationToken ct)
        => _arena.GetLeaderboardStatusAsync(ct);
}

// ── Orchestrator ────────────────────────────────────────────────────────────

public sealed class RiskAttestProService
{
    private static readonly string[] _sourcesQueried = new[]
    {
        "LiquidGuard", "RevokeBot", "MEVProtect", "TheMetaBot",
        "TheArenaBot", "TheWitnessBot", "history",
    };

    // Component key → source name (the "components" dict uses short lane
    // keys, the "sourcesUnavailable" array uses the long source-name labels
    // from sourcesQueried). Maintaining the map here keeps the two surfaces
    // aligned without hard-coding the order somewhere callers can drift.
    private static readonly (string Key, string Source)[] _laneMap = new[]
    {
        ("healthFactor", "LiquidGuard"),
        ("approvals",    "RevokeBot"),
        ("mev",          "MEVProtect"),
        ("reputation",   "TheMetaBot"),
        ("arena",        "TheArenaBot"),
        ("witness",      "TheWitnessBot"),
        ("trajectory",   "history"),
    };

    private readonly IRiskPeerClients _peers;
    private readonly IWitnessBotClient _witness;
    private readonly IRiskReputationLookup _rep;
    private readonly IRiskArenaLookup _arena;
    private readonly RiskTrajectoryStore _traj;
    private readonly RiskAttestProLlm _llm;
    private readonly ILogger<RiskAttestProService> _log;

    public RiskAttestProService(
        IRiskPeerClients peers,
        IWitnessBotClient witness,
        IRiskReputationLookup rep,
        IRiskArenaLookup arena,
        RiskTrajectoryStore traj,
        RiskAttestProLlm llm,
        ILogger<RiskAttestProService> log)
    {
        _peers = peers;
        _witness = witness;
        _rep = rep;
        _arena = arena;
        _traj = traj;
        _llm = llm;
        _log = log;
    }

    public async Task<RiskAttestProResult> GenerateAsync(
        string wallet, string chain, CancellationToken ct = default)
    {
        var w = wallet.Trim().ToLowerInvariant();
        var c = string.IsNullOrWhiteSpace(chain) ? "base" : chain.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        // Fan out the 7 lanes in parallel. None throw — each returns either
        // its peer's typed payload or null (transport / non-2xx / parse fail).
        var hfTask  = _peers.GetHealthFactorAsync(w, c, ct);
        var apTask  = _peers.GetApprovalsQuoteAsync(w, c, ct);
        var mvTask  = _peers.GetMevScoreAsync(w, ct);
        var rpTask  = _rep.GetAsync(w, ct);
        var arTask  = _arena.GetParticipantAsync(w, ct);
        var wtTask  = _witness.ManifestByAgentAsync(w, ct);
        var trTask  = BuildTrajectoryStridesAsync(w, c, now, ct);

        await Task.WhenAll(hfTask, apTask, mvTask, rpTask, arTask, wtTask, trTask);

        // Map every lane to a component dictionary (status="fresh" or
        // "unavailable"). The mapping is mechanical — see Build*Component
        // helpers below.
        var componentsBuilder = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var unavailable = new List<string>();
        var sourcesQueried = _sourcesQueried;

        componentsBuilder["healthFactor"] = BuildHealthFactorComponent(hfTask.Result);
        componentsBuilder["approvals"]    = BuildApprovalsComponent(apTask.Result);
        componentsBuilder["mev"]          = BuildMevComponent(mvTask.Result);
        componentsBuilder["reputation"]   = BuildReputationComponent(rpTask.Result);
        componentsBuilder["arena"]        = BuildArenaComponent(arTask.Result, w);
        componentsBuilder["witness"]      = BuildWitnessComponent(wtTask.Result);
        componentsBuilder["trajectory"]   = BuildTrajectoryComponent(trTask.Result);

        // Sources-unavailable bookkeeping in canonical lane order.
        foreach (var (key, source) in _laneMap)
        {
            if (componentsBuilder.TryGetValue(key, out var comp)
                && comp["status"]?.GetValue<string>() == "unavailable")
            {
                unavailable.Add(source);
            }
        }

        var freshCount = 7 - unavailable.Count;
        if (freshCount < 4)
            throw new InsufficientSignalsException(freshCount);

        // Composite: arithmetic mean of available component scores (skip
        // unavailable lanes — they contributed a neutral 50 placeholder that
        // would bias the mean towards mid-range).
        double scoreSum = 0;
        int scoreCount = 0;
        foreach (var (key, source) in _laneMap)
        {
            if (unavailable.Contains(source)) continue;
            if (!componentsBuilder.TryGetValue(key, out var comp)) continue;
            if (comp["score"] is JsonNode sc) { scoreSum += sc.GetValue<int>(); scoreCount++; }
        }
        var scorePro = scoreCount == 0 ? 50 : (int)Math.Round(scoreSum / scoreCount);
        var grade = ToGrade(scorePro);
        var verdict = ToVerdict(scorePro);

        // Canonical components JSON for the hash + the LLM prompt + the
        // markdown generator. SortedDictionary at every nesting level so
        // identical sub-component payloads hash identically across calls.
        var canonicalJson = ToCanonicalJson(componentsBuilder);
        var componentsHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
        var componentsElement = JsonDocument.Parse(canonicalJson).RootElement.Clone();

        var summary = await _llm.NarrateAsync(componentsHash, canonicalJson, verdict, scorePro, ct);
        var recommendations = BuildRecommendations(componentsBuilder);
        var markdown = RiskAttestProMarkdown.Generate(
            w, c, scorePro, verdict, grade, componentsElement, summary);
        var markdownB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown));

        // Persist a trajectory row so subsequent calls within the 30-day
        // window can derive direction without a re-snap. Best-effort: write
        // failure is logged but never bubbles up (trajectory persistence is
        // an enrichment, not a contract).
        var rowJson = JsonSerializer.Serialize(new
        {
            scorePro,
            verdict,
            grade,
            componentsHash,
        });
        try
        {
            await _traj.WriteAsync(w, c, now, scorePro, rowJson, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[riskAttestPro] trajectory persistence failed for {Wallet}", w);
        }

        // v1.0.3 — on-chain EAS attestation. Best-effort: any failure (peer
        // unreachable, EAS not configured, gas exhausted) returns the result
        // with Attestation=null. Cache-miss-only by design — the endpoint
        // already short-circuits cache hits with the persisted UID, so this
        // path only fires when there's no row for (wallet,chain) within 1h.
        // Per-call gas cost on Base mainnet is ~$0.04 (well under the $10
        // offering price). Schema UID = canonical seller-result schema
        // 0xdf208286…0114f; the riskAttestPro AgentRisk schema 0xb7038e6b…
        // is registered but its custom ABI encoding is slated for v1.0.4.
        RiskAttestPublishedAttestation? published = null;
        try
        {
            published = await PublishAttestationAsync(
                wallet: w,
                chain: c,
                componentsHash: componentsHash,
                componentsJson: canonicalJson,
                generatedAt: now,
                ct: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[riskAttestPro] EAS publish failed for {Wallet} — returning result with Attestation=null", w);
        }

        return new RiskAttestProResult(
            Verdict: verdict,
            ScorePro: scorePro,
            Grade: grade,
            Components: componentsElement,
            ExecutiveSummary: summary,
            Recommendations: JsonSerializer.SerializeToDocument(recommendations).RootElement.Clone(),
            MarkdownReportBase64: markdownB64,
            Wallet: w,
            Chain: c,
            GeneratedAt: now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ExpiresAt: now.AddHours(24).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            SourcesQueried: sourcesQueried,
            SourcesUnavailable: unavailable.ToArray(),
            ComponentsHash: componentsHash,
            Attestation: published);
    }

    // ── Verdict / grade thresholds ──────────────────────────────────────────

    public static string ToGrade(int score)
        => score >= 85 ? "A"
         : score >= 70 ? "B"
         : score >= 55 ? "C"
         : score >= 40 ? "D"
         : "F";

    public static string ToVerdict(int score)
        => score >= 85 ? "STRONG_BUY"
         : score >= 70 ? "OK"
         : score >= 55 ? "CAUTION"
         : score >= 40 ? "AVOID"
         : "INSUFFICIENT_DATA";

    // ── Per-lane component builders ─────────────────────────────────────────
    //
    // Each builder returns a JsonObject with at least: score / source / status
    // / details. Status is "fresh" when the peer returned interpretable data,
    // "unavailable" otherwise. Score is 50 (neutral) on unavailable so the
    // markdown rendering has a number to fall back to — but the composite
    // skips unavailable lanes entirely (see GenerateAsync mean math).

    private static JsonObject BuildHealthFactorComponent(JsonDocument? doc)
    {
        if (doc is null)
            return Unavailable("LiquidGuard", "Peer unavailable.");
        try
        {
            var root = doc.RootElement;
            double minHf = double.PositiveInfinity;
            int hfCount = 0;
            var positions = new JsonArray();
            if (root.TryGetProperty("healthFactors", out var hfs)
                && hfs.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in hfs.EnumerateArray())
                {
                    if (!p.TryGetProperty("hf", out var hfEl)) continue;
                    if (hfEl.ValueKind != JsonValueKind.Number) continue;
                    var hf = hfEl.GetDouble();
                    if (double.IsInfinity(hf) || double.IsNaN(hf)) continue;
                    var protocol = p.TryGetProperty("protocol", out var prot) ? prot.GetString() ?? "?" : "?";
                    positions.Add(new JsonObject
                    {
                        ["protocol"] = protocol,
                        ["hf"] = hf,
                    });
                    if (hf < minHf) minHf = hf;
                    hfCount++;
                }
            }
            if (hfCount == 0)
            {
                return new JsonObject
                {
                    ["score"] = 80,
                    ["source"] = "LiquidGuard",
                    ["status"] = "fresh",
                    ["details"] = "No open lending positions — no liquidation risk.",
                    ["perProtocolPositions"] = positions,
                };
            }
            int s = minHf >= 3.0 ? 100
                  : minHf >= 2.0 ? 90
                  : minHf >= 1.5 ? 75
                  : minHf >= 1.25 ? 55
                  : minHf >= 1.1 ? 35
                  : 10;
            return new JsonObject
            {
                ["score"] = s,
                ["source"] = "LiquidGuard",
                ["status"] = "fresh",
                ["details"] = $"min HF {minHf.ToString("0.00", CultureInfo.InvariantCulture)} across {hfCount} position(s).",
                ["perProtocolPositions"] = positions,
            };
        }
        catch (Exception)
        {
            return Unavailable("LiquidGuard", "Peer response unparseable.");
        }
    }

    private static JsonObject BuildApprovalsComponent(JsonDocument? doc)
    {
        if (doc is null)
            return Unavailable("RevokeBot", "Peer unavailable.");
        try
        {
            var root = doc.RootElement;
            int high = root.TryGetProperty("highRiskCount", out var hr)
                && hr.ValueKind == JsonValueKind.Number ? hr.GetInt32() : 0;
            int total = root.TryGetProperty("totalApprovals", out var ta)
                && ta.ValueKind == JsonValueKind.Number ? ta.GetInt32() : 0;
            int score = high switch { 0 => 100, 1 => 70, 2 => 55, 3 => 40, 4 => 25, _ => 10 };

            var perSpender = new JsonArray();
            if (root.TryGetProperty("approvals", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ap in arr.EnumerateArray())
                {
                    var spender = ap.TryGetProperty("spender", out var sp) ? sp.GetString() : null;
                    var token   = ap.TryGetProperty("token",   out var tk) ? tk.GetString() : null;
                    var tier    = ap.TryGetProperty("riskTier", out var rt) ? rt.GetString() : null;
                    if (spender is null || token is null) continue;
                    perSpender.Add(new JsonObject
                    {
                        ["spender"] = spender,
                        ["token"] = token,
                        ["riskTier"] = tier ?? "unknown",
                    });
                }
            }

            return new JsonObject
            {
                ["score"] = score,
                ["source"] = "RevokeBot",
                ["status"] = "fresh",
                ["details"] = high == 0
                    ? $"{total} active approvals, 0 flagged high-risk."
                    : $"{high} of {total} approvals flagged high-risk.",
                ["highRiskCount"] = high,
                ["perSpender"] = perSpender,
            };
        }
        catch (Exception)
        {
            return Unavailable("RevokeBot", "Peer response unparseable.");
        }
    }

    private static JsonObject BuildMevComponent(JsonDocument? doc)
    {
        if (doc is null)
            return Unavailable("MEVProtect", "Peer unavailable.");
        try
        {
            var root = doc.RootElement;
            int raw = root.TryGetProperty("mevScore", out var ms) && ms.ValueKind == JsonValueKind.Number
                ? Math.Clamp(ms.GetInt32(), 0, 100) : 75;
            int sandwiches = root.TryGetProperty("sandwichesObserved", out var sw)
                && sw.ValueKind == JsonValueKind.Number ? sw.GetInt32() : 0;
            int windowDays = root.TryGetProperty("windowDays", out var wd)
                && wd.ValueKind == JsonValueKind.Number ? wd.GetInt32() : 30;
            return new JsonObject
            {
                ["score"] = raw,
                ["source"] = "MEVProtect",
                ["status"] = "fresh",
                ["details"] = sandwiches == 0
                    ? $"No sandwich attempts in last {windowDays}d."
                    : $"{sandwiches} sandwich attempts in last {windowDays}d.",
                ["sandwichesObserved"] = sandwiches,
                ["thirtyDayRate"] = sandwiches,
            };
        }
        catch (Exception)
        {
            return Unavailable("MEVProtect", "Peer response unparseable.");
        }
    }

    private static JsonObject BuildReputationComponent(CachedReputationRow? row)
    {
        // 50 = neutral baseline when the wallet is NOT a registered ACP agent.
        // This is distinct from "unavailable" — a wallet that has never been
        // ACP-active legitimately has no reputation, which is a real signal
        // (no positive evidence). Status stays "fresh" so the floor count is
        // unaffected.
        if (row is null)
        {
            return new JsonObject
            {
                ["score"] = 50,
                ["source"] = "TheMetaBot",
                ["status"] = "fresh",
                ["details"] = "Wallet is not a registered ACP agent — neutral baseline.",
                ["composite"] = null,
            };
        }
        return new JsonObject
        {
            ["score"] = Math.Clamp(row.AgentScore, 0, 100),
            ["source"] = "TheMetaBot",
            ["status"] = "fresh",
            ["details"] = $"On-chain behavioural reputation {row.AgentScore}/100 (computed {row.ComputedAt.ToString("O", CultureInfo.InvariantCulture)}).",
            ["composite"] = row.AgentScore,
        };
    }

    private static JsonObject BuildArenaComponent(JsonDocument? doc, string wallet)
    {
        // ArenaBot exposes a leaderboard-status Resource, not a wallet-keyed
        // participant API (yet — that's v1.1 work). If we can read the Resource
        // at all, mark the lane fresh with isParticipant=false and a neutral
        // 50 score. If the request itself failed, mark unavailable.
        if (doc is null)
            return Unavailable("TheArenaBot", "Peer unavailable.");
        try
        {
            var root = doc.RootElement;
            bool isParticipant = root.TryGetProperty("isParticipant", out var ip)
                && ip.ValueKind == JsonValueKind.True;
            return new JsonObject
            {
                ["score"] = 50,
                ["source"] = "TheArenaBot",
                ["status"] = "fresh",
                ["details"] = isParticipant
                    ? "Wallet is an active Arena participant."
                    : "Wallet is not an Arena participant — neutral baseline.",
                ["isParticipant"] = isParticipant,
            };
        }
        catch (Exception)
        {
            return Unavailable("TheArenaBot", "Peer response unparseable.");
        }
    }

    private static JsonObject BuildWitnessComponent(WitnessManifest m)
    {
        if (m.Status != "fresh")
            return Unavailable("TheWitnessBot", m.Details);
        return new JsonObject
        {
            ["score"] = m.IsAcpAgent ? 80 : 50,
            ["source"] = "TheWitnessBot",
            ["status"] = "fresh",
            ["details"] = m.IsAcpAgent
                ? "ACP catalogue manifest signed."
                : "Wallet is not a registered ACP agent — no manifest expected.",
            ["isAcpAgent"] = m.IsAcpAgent,
            ["manifestSigned"] = m.IsAcpAgent,
            ["catalogueHash"] = m.CatalogueHash,
            ["signerEoa"] = m.SignerEoa,
            ["signedAt"] = m.SignedAt,
        };
    }

    // ── Trajectory: stride lookups + direction derivation ───────────────────

    private async Task<TrajectoryLookups> BuildTrajectoryStridesAsync(
        string wallet, string chain, DateTimeOffset now, CancellationToken ct)
    {
        var at7  = await _traj.LookupStrideAsync(wallet, chain, now, 7, ct);
        var at14 = await _traj.LookupStrideAsync(wallet, chain, now, 14, ct);
        var at21 = await _traj.LookupStrideAsync(wallet, chain, now, 21, ct);
        return new TrajectoryLookups(at7, at14, at21);
    }

    private static JsonObject BuildTrajectoryComponent(TrajectoryLookups lookups)
    {
        var s7 = lookups.At7?.Score;
        var s14 = lookups.At14?.Score;
        var s21 = lookups.At21?.Score;
        int present = (s7.HasValue ? 1 : 0) + (s14.HasValue ? 1 : 0) + (s21.HasValue ? 1 : 0);

        string direction;
        if (present < 3)
        {
            direction = "insufficient_data";
            return new JsonObject
            {
                ["score"] = 50,
                ["source"] = "history",
                ["status"] = "unavailable",
                ["details"] = $"Only {present}/3 stride rows present in last 21 days.",
                ["sevenDayPriorScore"] = s7,
                ["fourteenDayPriorScore"] = s14,
                ["twentyOneDayPriorScore"] = s21,
                ["direction"] = direction,
            };
        }

        // Older → newer monotonic comparison. Stride values are scoreAt21,
        // scoreAt14, scoreAt7 in chronological order; "improving" means each
        // newer reading is strictly greater than the prior. "declining" is
        // the inverse. Anything in between (mixed direction OR flat) →
        // "stable" so buyer agents have a clean three-state signal.
        int a = s21!.Value, b = s14!.Value, c = s7!.Value;
        if (c > b && b > a) direction = "improving";
        else if (c < b && b < a) direction = "declining";
        else direction = "stable";

        int score = direction switch
        {
            "improving" => 75,
            "stable"    => 60,
            _           => 35,
        };

        return new JsonObject
        {
            ["score"] = score,
            ["source"] = "history",
            ["status"] = "fresh",
            ["details"] = $"30-day trajectory {direction} ({a} → {b} → {c}).",
            ["sevenDayPriorScore"] = s7,
            ["fourteenDayPriorScore"] = s14,
            ["twentyOneDayPriorScore"] = s21,
            ["direction"] = direction,
        };
    }

    private sealed record TrajectoryLookups(
        TrajectoryRow? At7, TrajectoryRow? At14, TrajectoryRow? At21);

    // ── Recommendations ─────────────────────────────────────────────────────

    private static List<object> BuildRecommendations(
        Dictionary<string, JsonObject> components)
    {
        var recs = new List<object>();

        // 1. Revoke high-risk approvals — emit one rec per perSpender entry
        // when highRiskCount > 0. Each rec carries the spender/token so a
        // downstream caller can hit RevokeBot's revoke_calldata to bundle
        // the actual transaction data.
        if (components.TryGetValue("approvals", out var ap)
            && ap["status"]?.GetValue<string>() == "fresh"
            && (ap["highRiskCount"]?.GetValue<int>() ?? 0) > 0)
        {
            if (ap["perSpender"] is JsonArray spenders)
            {
                foreach (var spender in spenders)
                {
                    if (spender is not JsonObject s) continue;
                    if (s["riskTier"]?.GetValue<string>() != "high") continue;
                    recs.Add(new
                    {
                        priority = "high",
                        action = "revoke",
                        spender = s["spender"]?.GetValue<string>(),
                        token = s["token"]?.GetValue<string>(),
                        rationale = "Flagged high-risk by RevokeBot — revoke to limit drain blast radius.",
                    });
                }
            }
        }

        // 2. Raise HF when health-factor component score is dangerously low.
        if (components.TryGetValue("healthFactor", out var hf)
            && hf["status"]?.GetValue<string>() == "fresh"
            && (hf["score"]?.GetValue<int>() ?? 100) < 50)
        {
            recs.Add(new
            {
                priority = "high",
                action = "raise_hf",
                rationale = "Health factor approaching liquidation — add collateral or repay debt.",
            });
        }

        // 3. Verify witness when manifest signature is stale / tampered.
        if (components.TryGetValue("witness", out var wt)
            && wt["status"]?.GetValue<string>() == "fresh"
            && (wt["isAcpAgent"]?.GetValue<bool>() ?? false))
        {
            // verifyVerdict surfaces once Task 3.5 wires ManifestVerifyAsync;
            // until then we treat any non-current verdict as actionable.
            var verifyVerdict = wt["verifyVerdict"]?.GetValue<string>();
            if (verifyVerdict is not null and not "current")
            {
                recs.Add(new
                {
                    priority = "medium",
                    action = "verify_witness",
                    rationale = $"WitnessBot verifyVerdict = {verifyVerdict} — re-sign the manifest.",
                });
            }
        }

        return recs;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static JsonObject Unavailable(string source, string details) => new()
    {
        ["score"] = 50,
        ["source"] = source,
        ["status"] = "unavailable",
        ["details"] = details,
    };

    /// <summary>
    /// Serialise the components dict with keys ordered ordinally so the same
    /// payload hashes the same regardless of insertion order. Nested objects
    /// retain their builder-order keys (the builders are deterministic).
    /// </summary>
    private static string ToCanonicalJson(Dictionary<string, JsonObject> components)
    {
        var sorted = new JsonObject();
        foreach (var key in components.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            // DeepClone so the JsonObject isn't double-parented.
            sorted[key] = components[key].DeepClone();
        }
        return sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    // v1.0.3 — call EASIssuer's /v1/eas-publish via the shared peer client
    // and return a typed Attestation record on success. Returns null on any
    // class of failure (peer unreachable, response shape unexpected,
    // EAS_NOT_CONFIGURED, EAS_PUBLISH_FAILED). The buyer still gets the
    // verdict + components + summary + markdown — only the on-chain anchor
    // degrades.
    private async Task<RiskAttestPublishedAttestation?> PublishAttestationAsync(
        string wallet,
        string chain,
        string componentsHash,
        string componentsJson,
        DateTimeOffset generatedAt,
        CancellationToken ct)
    {
        // Hash the canonical components JSON to a deterministic resultHash.
        // The componentsHash from ToCanonicalJson is the SAME value but kept
        // separate so the contract is unambiguous: resultHash = SHA256(canonicalJson).
        var resultHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(componentsJson));
        var resultHashHex = "0x" + Convert.ToHexString(resultHashBytes).ToLowerInvariant();

        // SchemaUri must use https:// or ipfs:// per EASIssuer's
        // IsAcceptableUri validator (added in the 2026-05-28 audit).
        const string schemaUri = "https://api.acp-metabot.dev/easissuer/schemas/riskAttestPro-v1.json";

        // JobId is bytes32 — use the componentsHash (already 32-byte hex).
        var jobId = componentsHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? componentsHash
            : "0x" + componentsHash;

        var payload = new
        {
            jobId,
            seller = wallet,
            resultHash = resultHashHex,
            schemaUri,
            metadata = new
            {
                wallet,
                chain,
                generatedAt = generatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                componentsHash,
                resultType = "riskAttestPro-v1",
            }
        };

        JsonDocument? resp;
        try { resp = await _peers.PublishAttestationAsync(payload, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[riskAttestPro] PublishAttestationAsync threw");
            return null;
        }

        if (resp is null) return null;

        try
        {
            using (resp)
            {
                var root = resp.RootElement;
                if (!root.TryGetProperty("attestationUid", out var uidEl) || uidEl.ValueKind != JsonValueKind.String)
                    return null;
                var uid = uidEl.GetString() ?? "";
                var txHash = root.TryGetProperty("txHash", out var txEl) && txEl.ValueKind == JsonValueKind.String
                    ? (txEl.GetString() ?? "") : "";
                long blockNumber = 0L;
                if (root.TryGetProperty("blockNumber", out var bnEl))
                {
                    if (bnEl.ValueKind == JsonValueKind.Number)
                        blockNumber = bnEl.GetInt64();
                    else if (bnEl.ValueKind == JsonValueKind.String && long.TryParse(bnEl.GetString(), out var bnParsed))
                        blockNumber = bnParsed;
                }
                var schemaUid = root.TryGetProperty("schemaUid", out var sEl) && sEl.ValueKind == JsonValueKind.String
                    ? (sEl.GetString() ?? "") : "";

                var baseScan = !string.IsNullOrEmpty(txHash)
                    ? $"https://basescan.org/tx/{txHash}"
                    : "";

                return new RiskAttestPublishedAttestation(
                    AttestationUid: uid,
                    TxHash: txHash,
                    BlockNumber: blockNumber,
                    SchemaUid: schemaUid,
                    SchemaUri: schemaUri,
                    BaseScanUrl: baseScan);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[riskAttestPro] EAS publish response parse failed");
            return null;
        }
    }
}
