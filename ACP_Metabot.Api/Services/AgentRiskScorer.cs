using System.Globalization;
using System.Text.Json;
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// v1.10 Phase 3 T5: individual signal contributing to an agent's risk score.
/// Each signal is bounded 0-25; four signals sum to the 0-100 total. The Detail
/// string is buyer-facing — it's surfaced verbatim in the /v1/agentRiskCheck
/// response and (via T6) in per-hit decoration on /v1/search.
/// </summary>
public sealed record RiskSignal(string Name, int Score, string Detail);

/// <summary>
/// v1.10 Phase 3 T5: full risk envelope for a single agent on a single chain.
/// Cached in <c>agent_risk_cache</c> for <c>Search:AgentRiskCacheTtlSeconds</c>
/// (default 21 600 s = 6 h). Tier bins are LOW (0-25), MEDIUM (26-50),
/// HIGH (51-75), CRITICAL (76-100).
/// </summary>
public sealed record AgentRiskResult(
    string AgentAddress,
    int ChainId,
    int RiskScore,
    string RiskTier,
    IReadOnlyList<RiskSignal> Signals,
    DateTime EvaluatedAt,
    int CacheTtlSeconds);

/// <summary>
/// v1.10 Phase 3 T5: defensive scam-risk scoring per agent. Four signals each
/// capped at 25 (total 0-100). Bins to low / medium / high / critical tiers.
/// Caches per (agent_address, chain_id) for 6 h.
///
/// NEVER throws on signal evaluation — any per-signal exception is logged and
/// returns a Score=0 row with a "lookup failed" detail. The envelope itself
/// always returns; only callers passing a syntactically invalid address see
/// an ArgumentException.
///
/// Suspicious-funder matching is exact-string. The on-disk
/// <c>Data/SuspiciousFunderPatterns.json</c> seeds the production set; tests
/// inject via the <c>suspiciousFundersOverride</c> ctor parameter.
/// </summary>
public sealed class AgentRiskScorer
{
    private readonly Db _db;
    private readonly OfferingRepository _offerings;
    private readonly AgentReputationCacheRepository _reputation;
    private readonly PricePercentileCalculator _pricePercentile;
    private readonly SearchService? _search;
    private readonly ILogger<AgentRiskScorer> _logger;
    private readonly int _cacheTtlSeconds;
    private readonly HashSet<string> _suspiciousFunders;

    public AgentRiskScorer(
        Db db,
        OfferingRepository offerings,
        AgentReputationCacheRepository reputation,
        PricePercentileCalculator pricePercentile,
        SearchService search,
        IConfiguration config,
        ILogger<AgentRiskScorer> logger)
        : this(db, offerings, reputation, pricePercentile, search, config, logger,
            suspiciousFundersOverride: null)
    {
    }

    // Test seam: tests pass an explicit suspicious-funder set so they don't
    // rely on the on-disk file being copied to the test bin directory. Also
    // accepts a null SearchService when the test fixture doesn't need
    // category-aware pricing (the pricing-outlier signal degrades to score 0
    // when no category can be resolved, matching the "no offerings" path).
    internal AgentRiskScorer(
        Db db,
        OfferingRepository offerings,
        AgentReputationCacheRepository reputation,
        PricePercentileCalculator pricePercentile,
        SearchService? search,
        IConfiguration config,
        ILogger<AgentRiskScorer> logger,
        IEnumerable<string>? suspiciousFundersOverride)
    {
        _db = db;
        _offerings = offerings;
        _reputation = reputation;
        _pricePercentile = pricePercentile;
        _search = search;
        _logger = logger;
        _cacheTtlSeconds = Math.Max(60,
            config.GetValue<int?>("Search:AgentRiskCacheTtlSeconds") ?? 21_600);
        _suspiciousFunders = suspiciousFundersOverride is not null
            ? new HashSet<string>(
                suspiciousFundersOverride.Select(s => (s ?? string.Empty).Trim().ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase)
            : LoadSuspiciousFundersFromDisk();
    }

    private HashSet<string> LoadSuspiciousFundersFromDisk()
    {
        // The csproj Content entry copies Data/SuspiciousFunderPatterns.json
        // into the publish output (PreserveNewest), so AppContext.BaseDirectory
        // is the first hit in production. The walk-up fallback covers `dotnet
        // run` invocations whose BaseDirectory diverges from the project
        // layout, and the test-bin-relative path (bin/Debug/.../Data/...).
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "SuspiciousFunderPatterns.json"),
        };
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            candidates.Add(Path.Combine(root, "Data", "SuspiciousFunderPatterns.json"));
            candidates.Add(Path.Combine(root, "ACP_Metabot.Api", "Data", "SuspiciousFunderPatterns.json"));
            root = Path.GetDirectoryName(root) ?? root;
        }
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("address", out var a)) continue;
                    var addr = a.GetString()?.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(addr)) set.Add(addr);
                }
                _logger?.LogInformation(
                    "[risk-scorer] loaded {Count} suspicious-funder addresses from {Path}",
                    set.Count, path);
                return set;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[risk-scorer] failed to load suspicious-funders from {Path}", path);
            }
        }
        _logger?.LogWarning(
            "[risk-scorer] no suspicious-funders file found; walletProvenance signal will always score 0");
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compute or fetch-from-cache the risk envelope for an agent on a chain.
    /// Throws only on syntactically invalid <paramref name="agentAddress"/>.
    /// </summary>
    public async Task<AgentRiskResult> ScoreAsync(
        string agentAddress, int chainId, CancellationToken ct)
    {
        var addr = (agentAddress ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(addr) ||
            !System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        {
            throw new ArgumentException("invalid agent address", nameof(agentAddress));
        }

        // Cache lookup. Best-effort — a corrupt cache row must not block a
        // fresh score.
        try
        {
            var cached = await TryReadCacheAsync(addr, chainId, ct);
            if (cached is not null) return cached;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[risk-scorer] cache read failed");
        }

        var signals = new List<RiskSignal>
        {
            await ScoreReputationDepthAsync(addr, ct),
            await ScorePricingOutlierAsync(addr, ct),
            ScoreWalletProvenance(addr),
            await ScoreFootprintAnomalyAsync(addr, ct),
        };

        var total = signals.Sum(s => s.Score);
        var tier = total switch
        {
            <= 25 => "low",
            <= 50 => "medium",
            <= 75 => "high",
            _     => "critical",
        };
        var result = new AgentRiskResult(
            AgentAddress: addr,
            ChainId: chainId,
            RiskScore: total,
            RiskTier: tier,
            Signals: signals,
            EvaluatedAt: DateTime.UtcNow,
            CacheTtlSeconds: _cacheTtlSeconds);

        // Best-effort write — a cache failure must not mask the result.
        try { await WriteCacheAsync(result, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[risk-scorer] cache write failed");
        }

        return result;
    }

    /// <summary>
    /// Cheap read-only tier lookup. Returns null when no cached entry exists —
    /// the T6 caller (per-hit search decoration) decides whether to score on
    /// demand or skip the row. Never triggers fresh compute.
    /// </summary>
    public async Task<string?> GetTierAsync(
        string agentAddress, int chainId, CancellationToken ct)
    {
        try
        {
            var addr = (agentAddress ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(addr)) return null;
            var cached = await TryReadCacheAsync(addr, chainId, ct);
            return cached?.RiskTier;
        }
        catch
        {
            return null;
        }
    }

    // ── Signal evaluators ─────────────────────────────────────────────────

    private async Task<RiskSignal> ScoreReputationDepthAsync(
        string addr, CancellationToken ct)
    {
        _ = ct;
        try
        {
            var jobCount = await _reputation.GetCachedTotalJobsAsync(addr) ?? 0L;
            // 25 - min(25, jobCount × 2) — new agent (0 jobs) = 25; 12+ jobs = 0.
            var score = Math.Max(0, 25 - (int)Math.Min(25L, jobCount * 2L));
            return new RiskSignal(
                "reputationDepth", score,
                $"{jobCount} completed jobs (lower = higher risk)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[risk-scorer] reputation lookup failed for {Addr}", addr);
            return new RiskSignal(
                "reputationDepth", 0,
                "lookup failed; treating as low risk");
        }
    }

    private async Task<RiskSignal> ScorePricingOutlierAsync(
        string addr, CancellationToken ct)
    {
        _ = ct;
        try
        {
            var offerings = await _offerings.ListByAgentAsync(addr);
            if (offerings.Count == 0)
            {
                return new RiskSignal(
                    "pricingOutlier", 0,
                    "no offerings; cannot evaluate pricing");
            }

            // PricePercentileCalculator.Compute returns the offering's percentile
            // rank within its (category, marketplace) cohort (0 = cheapest,
            // 100 = priciest, null = LowN / cohort missing).
            //
            // Risk semantics: only the TOP HALF of the cohort matters — agents
            // priced below the cohort median aren't outliers. We linearly
            // remap percentile 50→0, 100→25 so a corpus-wide pricier-than-
            // median agent contributes up to the full 25 points. Below 50 or
            // null contributes 0. Average across all categorised offerings.
            double sumScore = 0;
            int evaluated = 0;
            foreach (var o in offerings)
            {
                var category = _search?.GetCategoryForOffering(o.Id);
                if (string.IsNullOrEmpty(category)) continue;
                var mv = o.MarketplaceVersion ?? "v1";
                var pp = _pricePercentile.Compute(o.Id, category, mv, o.PriceUsdc);
                if (pp.Value is not int pct) continue;
                var s = pct <= 50 ? 0.0 : (pct - 50) / 50.0 * 25.0;
                sumScore += s;
                evaluated++;
            }
            if (evaluated == 0)
            {
                return new RiskSignal(
                    "pricingOutlier", 0,
                    "no categorised offerings or insufficient peer pool");
            }
            var avg = sumScore / evaluated;
            return new RiskSignal(
                "pricingOutlier", (int)Math.Round(avg),
                $"avg pricing-outlier across {evaluated} offering(s) = {avg:F1}/25");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[risk-scorer] pricing-outlier failed for {Addr}", addr);
            return new RiskSignal(
                "pricingOutlier", 0,
                "evaluation failed");
        }
    }

    private RiskSignal ScoreWalletProvenance(string addr)
    {
        if (_suspiciousFunders.Contains(addr))
        {
            return new RiskSignal(
                "walletProvenance", 25,
                "address matches a curated suspicious-funder entry");
        }
        return new RiskSignal(
            "walletProvenance", 0,
            "no suspicious-funder match");
    }

    private async Task<RiskSignal> ScoreFootprintAnomalyAsync(
        string addr, CancellationToken ct)
    {
        try
        {
            var offerings = await _offerings.ListByAgentAsync(addr);
            if (offerings.Count == 0)
            {
                return new RiskSignal(
                    "footprintAnomaly", 0,
                    "no offerings to compare");
            }

            // Impersonation heuristic: does the agent ship an offering whose
            // name collides with a V1 agent (legacy / higher-reputation
            // marketplace) at a different address? CrossPresenceBuilder is
            // overkill for the binary question we need here; a single SQL
            // join per offering is sufficient and stays inside SQLite's
            // own indexes.
            await using var conn = _db.OpenConnection();
            foreach (var o in offerings)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*) FROM offerings
                    WHERE offering_name = $n
                      AND LOWER(agent_address) != $a
                      AND marketplace_version = 'v1'
                      AND is_removed = 0;";
                cmd.Parameters.AddWithValue("$n", o.OfferingName);
                cmd.Parameters.AddWithValue("$a", addr);
                var v = await cmd.ExecuteScalarAsync(ct);
                var count = v is null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
                if (count > 0)
                {
                    return new RiskSignal(
                        "footprintAnomaly", 25,
                        $"offering '{o.OfferingName}' name-matches {count} V1 agent(s) (possible impersonation)");
                }
            }
            return new RiskSignal(
                "footprintAnomaly", 0,
                "no V1 name-collision found across offerings");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[risk-scorer] footprint-anomaly failed for {Addr}", addr);
            return new RiskSignal(
                "footprintAnomaly", 0,
                "evaluation failed");
        }
    }

    // ── Cache I/O ─────────────────────────────────────────────────────────

    private async Task<AgentRiskResult?> TryReadCacheAsync(
        string addr, int chainId, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        // SQLite stores text timestamps; the TTL window is enforced inline so
        // the read path doesn't need to materialise the row before deciding
        // to discard a stale one. evaluated_at is written via datetime('now')
        // which yields UTC text in 'YYYY-MM-DD HH:MM:SS' shape — both compare
        // string-wise with datetime() math.
        cmd.CommandText = @"
            SELECT risk_score, risk_tier, signals_json, evaluated_at
            FROM agent_risk_cache
            WHERE agent_address = $a
              AND chain_id = $c
              AND evaluated_at > datetime('now', '-' || $ttl || ' seconds');";
        cmd.Parameters.AddWithValue("$a", addr);
        cmd.Parameters.AddWithValue("$c", chainId);
        cmd.Parameters.AddWithValue("$ttl", _cacheTtlSeconds);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;

        var score = rdr.GetInt32(0);
        var tier = rdr.GetString(1);
        var signalsJson = rdr.GetString(2);
        var evaluatedRaw = rdr.GetString(3);
        var evaluated = DateTime.Parse(
            evaluatedRaw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        List<RiskSignal> signals;
        try
        {
            signals = JsonSerializer.Deserialize<List<RiskSignal>>(
                signalsJson, SignalJsonOptions) ?? new List<RiskSignal>();
        }
        catch (JsonException)
        {
            signals = new List<RiskSignal>();
        }
        return new AgentRiskResult(addr, chainId, score, tier, signals, evaluated, _cacheTtlSeconds);
    }

    private async Task WriteCacheAsync(AgentRiskResult result, CancellationToken ct)
    {
        var signalsJson = JsonSerializer.Serialize(result.Signals, SignalJsonOptions);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_risk_cache(
                agent_address, chain_id, risk_score, risk_tier, signals_json, evaluated_at)
            VALUES ($a, $c, $s, $t, $j, datetime('now'))
            ON CONFLICT(agent_address, chain_id) DO UPDATE SET
                risk_score   = excluded.risk_score,
                risk_tier    = excluded.risk_tier,
                signals_json = excluded.signals_json,
                evaluated_at = excluded.evaluated_at;";
        cmd.Parameters.AddWithValue("$a", result.AgentAddress);
        cmd.Parameters.AddWithValue("$c", result.ChainId);
        cmd.Parameters.AddWithValue("$s", result.RiskScore);
        cmd.Parameters.AddWithValue("$t", result.RiskTier);
        cmd.Parameters.AddWithValue("$j", signalsJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static readonly JsonSerializerOptions SignalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
