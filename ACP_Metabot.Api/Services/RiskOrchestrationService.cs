using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// v1.8 Portfolio Risk Bot — request-time orchestration for the 4 one-shot
// offerings + subscription creation for daily_risk_watch.
//
// Each method below maps 1:1 to a POST endpoint mapped in Program.cs:
//
//   risk_snapshot     → POST /v1/risk/snapshot
//   risk_deep_dive    → POST /v1/risk/deep-dive
//   risk_compare      → POST /v1/risk/compare
//   risk_attestation  → POST /v1/risk/attestation
//   daily_risk_watch  → POST /v1/risk/watch (creates a row; pushes happen later via RiskWatchWorker)
public sealed class RiskOrchestrationService
{
    private readonly RiskSynthesisService _synth;
    private readonly IRiskPeerClients _peers;
    private readonly RiskSubscriptionRepository _subs;
    private readonly ILogger<RiskOrchestrationService> _log;

    public RiskOrchestrationService(
        RiskSynthesisService synth,
        IRiskPeerClients peers,
        RiskSubscriptionRepository subs,
        ILogger<RiskOrchestrationService> log)
    {
        _synth = synth;
        _peers = peers;
        _subs = subs;
        _log = log;
    }

    public static string NormalizeChain(string? chain)
        => string.IsNullOrWhiteSpace(chain) ? "base" : chain.Trim().ToLowerInvariant();

    // ── risk_snapshot ($0.30) ─────────────────────────────────────────────────
    public async Task<object> SnapshotAsync(string wallet, string? chain, CancellationToken ct)
    {
        var chainNorm = NormalizeChain(chain);
        var snap = await _synth.ComputeAsync(wallet, chainNorm, ct);
        return SerialiseSnapshot(snap);
    }

    // ── risk_deep_dive ($1.00) ────────────────────────────────────────────────
    public async Task<object> DeepDiveAsync(string wallet, string? chain, CancellationToken ct)
    {
        var chainNorm = NormalizeChain(chain);
        var snap = await _synth.ComputeAsync(wallet, chainNorm, ct);

        // For each high-risk approval surfaced by RevokeBot, fetch the
        // revoke calldata. Stays best-effort — failed calldata fetches are
        // skipped, not thrown.
        var actions = new List<object>();
        if (snap.Components.TryGetValue("approvals", out var apr)
            && apr.Status == "fresh" && (apr.HighRiskCount ?? 0) > 0)
        {
            // RevokeBot's quote response (returned by GetApprovalsQuoteAsync)
            // includes an `approvals` array. We re-fetch here so the deep-dive
            // can pull the spender/token pairs needed for revoke_calldata.
            var quote = await _peers.GetApprovalsQuoteAsync(wallet, chainNorm, ct);
            if (quote is not null)
            {
                try
                {
                    var root = quote.RootElement;
                    if (root.TryGetProperty("approvals", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ap in arr.EnumerateArray())
                        {
                            if (!ap.TryGetProperty("riskTier", out var tier)) continue;
                            var t = tier.GetString();
                            if (t != "high") continue;
                            var spender = ap.TryGetProperty("spender", out var sp) ? sp.GetString() : null;
                            var token   = ap.TryGetProperty("token",   out var tk) ? tk.GetString() : null;
                            if (spender is null || token is null) continue;
                            var call = await _peers.GetRevokeCalldataAsync(wallet, chainNorm, spender, token, ct);
                            if (call is null) continue;
                            var cd = call.RootElement;
                            var to    = cd.TryGetProperty("to",   out var t1) ? t1.GetString()   ?? token  : token;
                            var data  = cd.TryGetProperty("data", out var d1) ? d1.GetString()   ?? ""     : "";
                            var value = cd.TryGetProperty("value",out var v1) ? v1.GetString()   ?? "0"    : "0";
                            actions.Add(new
                            {
                                type = "revoke",
                                priority = "high",
                                calldataHint = new
                                {
                                    to,
                                    data,
                                    value,
                                    description = $"Revoke {token} approval to {spender} (flagged high-risk by RevokeBot)."
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "[risk] deep-dive: revoke calldata enumeration failed");
                }
            }
        }

        // LiquidGuard rebalance hints — emit a manual-review action when the
        // health-factor component scored < 55 (i.e. below the 'C' boundary).
        if (snap.Components.TryGetValue("healthFactor", out var hf)
            && hf.Status == "fresh" && hf.Score < 55)
        {
            actions.Add(new
            {
                type = "rebalance",
                priority = "high",
                calldataHint = new
                {
                    to = "0x0000000000000000000000000000000000000000",
                    data = "",
                    value = "0",
                    description = "Health factor below safe threshold — add collateral or repay debt. v1 returns this as a manual-review hint; v1.1 will bundle deposit/repay calldata."
                }
            });
        }

        var snapJson = SerialiseSnapshot(snap);
        return MergeWithActions(snapJson, actions);
    }

    // ── risk_compare ($0.20) ──────────────────────────────────────────────────
    public async Task<object> CompareAsync(string[] wallets, string? chain, CancellationToken ct)
    {
        var chainNorm = NormalizeChain(chain);
        var unique = wallets.Select(w => w.ToLowerInvariant()).Distinct().ToArray();
        var snaps = await Task.WhenAll(unique.Select(w => _synth.ComputeAsync(w, chainNorm, ct)));

        var ranked = snaps
            .OrderByDescending(s => s.RiskScore)
            .Select(s => new { wallet = s.Wallet, score = s.RiskScore, grade = s.RiskGrade })
            .ToArray();

        var top = ranked.FirstOrDefault();
        var conclusion = top is null
            ? "No wallets to compare."
            : $"Wallet {Shorten(top.wallet)} is safest (grade {top.grade}, {top.score}).";

        string BestBy(Func<RiskSnapshotResult, int> sel)
        {
            var winner = snaps.OrderByDescending(sel).First();
            return winner.Wallet;
        }

        return new
        {
            wallets = unique,
            chain = chainNorm,
            ranked,
            conclusion,
            diffs = new
            {
                healthFactorBest = BestBy(s => s.Components.TryGetValue("healthFactor", out var c) ? c.Score : 0),
                approvalsBest    = BestBy(s => s.Components.TryGetValue("approvals", out var c)    ? c.Score : 0),
                mevExposureBest  = BestBy(s => s.Components.TryGetValue("mevExposure", out var c)  ? c.Score : 0),
            }
        };
    }

    // ── risk_attestation ($0.50) ──────────────────────────────────────────────
    //
    // Best-effort: compute the snapshot, ask EASIssuer to sign + publish on
    // Base. When EASIssuer is unreachable we still return the snapshot with
    // attestation fields nulled.
    public async Task<object> AttestationAsync(string wallet, string? chain, CancellationToken ct)
    {
        var chainNorm = NormalizeChain(chain);
        var snap = await _synth.ComputeAsync(wallet, chainNorm, ct);

        // Schema UID — fixed for v1. WalletRiskSnapshot v1 schema registered
        // out-of-band; v1.1 may auto-register on first call.
        const string canonicalSchemaUid =
            "0xdf208286c7c0b8a5d8f9e2a3b4c5d6e7f8901234567890abcdef0114f";

        // 2026-05-31 — wire shape now matches EASIssuer's EasPublishRequest.
        // Pre-fix this posted to a path that didn't exist (/v1/internal/attest)
        // with a payload shape EASIssuer didn't recognise, so EVERY paid
        // risk_attestation call silently degraded to easAttestationUid=null.
        var snapAnon = SerialiseSnapshot(snap);
        var snapJsonString = JsonSerializer.Serialize(snapAnon);
        var resultHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(snapJsonString));
        var resultHashHex = "0x" + Convert.ToHexString(resultHashBytes).ToLowerInvariant();
        // jobId is bytes32 — use the resultHash bytes directly so it's
        // deterministic + content-bound for the snapshot.
        var jobIdHex = resultHashHex;
        const string schemaUri = "https://api.acp-metabot.dev/easissuer/schemas/walletRiskSnapshot-v1.json";

        var attestationRequest = new
        {
            jobId = jobIdHex,
            seller = wallet,
            resultHash = resultHashHex,
            schemaUri,
            metadata = new
            {
                wallet,
                chain = chainNorm,
                generatedAt = snap.GeneratedAt,
                resultType = "walletRiskSnapshot-v1",
            }
        };

        string? signature = null;
        string? easAttestationUid = null;
        string? easTxHash = null;
        string? baseScanUrl = null;
        long blockNumber = 0;

        var resp = await _peers.PublishAttestationAsync(attestationRequest, ct);
        if (resp is not null)
        {
            try
            {
                var root = resp.RootElement;
                signature         = TryString(root, "signature");
                easAttestationUid = TryString(root, "attestationUid") ?? TryString(root, "easAttestationUid");
                easTxHash         = TryString(root, "txHash") ?? TryString(root, "easTxHash");
                baseScanUrl       = TryString(root, "baseScanUrl")
                    ?? (string.IsNullOrEmpty(easTxHash) ? null : $"https://basescan.org/tx/{easTxHash}");
                if (root.TryGetProperty("blockNumber", out var bn))
                {
                    if (bn.ValueKind == JsonValueKind.Number) blockNumber = bn.GetInt64();
                    else if (bn.ValueKind == JsonValueKind.String && long.TryParse(bn.GetString(), out var bnParsed))
                        blockNumber = bnParsed;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[risk] attestation response parse failed");
            }
        }

        // Only mark "attestation" as fallback if we genuinely got no UID back —
        // peer-reachable-but-EAS-misconfigured returns a 502/503 envelope
        // (PublishAttestationAsync swallows that to null), in which case
        // easAttestationUid stays null and the fallback is honest.
        if (string.IsNullOrEmpty(easAttestationUid))
        {
            snap.Fallbacks.Add("attestation");
        }

        var snapJson = SerialiseSnapshot(snap);
        var merged = MergeWithKeyed(snapJson, "attestation", new
        {
            signature,
            schemaUid = canonicalSchemaUid,
            easAttestationUid,
            easTxHash,
            baseScanUrl,
            blockNumber,
        });
        return merged;
    }

    // ── daily_risk_watch ($5.00 / 30d) — subscription row creation ────────────
    //
    // The orchestrator side returns the receipt; the actual daily pushes
    // happen in RiskWatchWorker. The webhook secret is generated here and
    // shipped on the receipt — the buyer reads the X-Subscription-Signature
    // header on every push and verifies HMAC-SHA256.
    public async Task<object> CreateWatchAsync(
        long jobId, string buyerAddress, string wallet, string webhookUrl, string? chain)
    {
        var chainNorm = NormalizeChain(chain);
        var id = "riskwatch_" + Guid.NewGuid().ToString("N");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTime.UtcNow;
        const int TicksPurchased = 30;
        // First tick fires within ~24h; align to 02:00 UTC the next day so
        // pushes settle into a predictable window.
        var firstTickAt = now.AddDays(1).Date.AddHours(2);
        var expiresAt = now.AddDays(TicksPurchased);
        var nextRunAt = firstTickAt;

        var sub = new RiskSubscription(
            Id: id,
            JobId: jobId,
            BuyerAddress: buyerAddress,
            WalletAddress: wallet,
            Chain: chainNorm,
            WebhookUrl: webhookUrl,
            WebhookSecret: secret,
            Cadence: "daily",
            TicksPurchased: TicksPurchased,
            TicksDelivered: 0,
            ConsecutiveFailures: 0,
            Status: "active",
            CreatedAt: now,
            ExpiresAt: expiresAt,
            FirstTickAt: firstTickAt,
            LastRunAt: null,
            NextRunAt: nextRunAt,
            LastSnapshotJson: null,
            LastScore: null
        );
        await _subs.InsertAsync(sub);

        return new
        {
            subscriptionId = id,
            // Field name `webhookSecret` matches marketplacePulseSub for buyer
            // verifier code-reuse across portfolio subscription tiers. Buyer
            // gets it on the receipt only — never serialised to readout
            // endpoints (SubscriptionView omits it).
            webhookSecret = secret,
            walletAddress = wallet.ToLowerInvariant(),
            cadence = "daily",
            chain = chainNorm,
            firstTickAt = firstTickAt.ToString("O"),
            ticksPurchased = TicksPurchased,
            expiresAt = expiresAt.ToString("O"),
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    public static object SerialiseSnapshot(RiskSnapshotResult s)
    {
        return new
        {
            wallet      = s.Wallet,
            chain       = s.Chain,
            generatedAt = s.GeneratedAt.ToString("O"),
            riskScore   = s.RiskScore,
            riskGrade   = s.RiskGrade,
            summary     = s.Summary,
            components = new
            {
                healthFactor = SerialiseComponent(s.Components["healthFactor"]),
                approvals    = SerialiseApproval(s.Components["approvals"]),
                mevExposure  = SerialiseComponent(s.Components["mevExposure"]),
                reputation   = SerialiseComponent(s.Components["reputation"]),
            },
            fallbacks = s.Fallbacks.ToArray(),
        };
    }

    private static object SerialiseComponent(RiskComponent c) => new
    {
        score = c.Score,
        source = c.Source,
        details = c.Details,
        status = c.Status,
    };

    private static object SerialiseApproval(RiskComponent c) => new
    {
        score = c.Score,
        source = c.Source,
        highRiskCount = c.HighRiskCount ?? 0,
        details = c.Details,
        status = c.Status,
    };

    private static object MergeWithActions(object snapshot, List<object> actions)
    {
        // Re-serialise into a dictionary so we can add the `actions` key
        // alongside the snapshot fields without redefining the schema here.
        var json = JsonSerializer.Serialize(snapshot);
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();
        foreach (var p in doc.RootElement.EnumerateObject())
            dict[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
        dict["actions"] = actions;
        return dict;
    }

    private static object MergeWithKeyed(object snapshot, string key, object value)
    {
        var json = JsonSerializer.Serialize(snapshot);
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();
        foreach (var p in doc.RootElement.EnumerateObject())
            dict[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
        dict[key] = value;
        return dict;
    }

    private static string? TryString(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Shorten(string addr)
        => addr.Length < 10 ? addr : addr.Substring(0, 6) + "…" + addr.Substring(addr.Length - 4);
}
