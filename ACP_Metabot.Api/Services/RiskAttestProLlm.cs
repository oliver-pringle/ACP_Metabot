// riskAttestPro v1.0 Task 5 — Haiku narration with budget cap + deterministic cache.
//
// Generates a 3-5 sentence executive summary of the 7 sub-component signals via
// Anthropic Haiku. Caps daily spend at $0.50 USD (env-overridable via
// RISK_ATTEST_PRO_LLM_DAILY_CAP_USD) and caches narrations deterministically
// keyed by componentsHash so identical input never re-bills.
//
// Cache hit path: read response_json from risk_attest_pro_cache where
// wallet_chain = "narr:" + componentsHash. Same table is dual-purpose —
// wallet-keyed rows store the full $10 deliverable, narr:-prefixed rows store
// just the LLM narration text. Narration cache rows last as long as the row
// sits; the informational expires_at is not enforced for narrations.
//
// Budget-cap path: when SUM(llm_cost_usd) >= cap for today, return a
// deterministic fallback template (verdict + score). Do NOT increment spend on
// cap-hit. Spend writes are atomic via INSERT ... ON CONFLICT UPDATE on the
// risk_attest_pro_spend (day_utc) row.
//
// Live wiring (v1.0.1): IClaudeClient is the shared Anthropic Messages client
// also used by SearchNarrator + LlmQueryRewriter + StackComposerService.
// Reads ANTHROPIC_API_KEY + Claude:Model from config. Tests still inject a
// counting stub via the injector ctor param (injector takes precedence).

using ACP_Metabot.Api.Data;
using Microsoft.Extensions.Logging;

namespace ACP_Metabot.Api.Services;

public sealed class RiskAttestProLlm
{
    static readonly decimal DailyCapUsd = decimal.TryParse(
        Environment.GetEnvironmentVariable("RISK_ATTEST_PRO_LLM_DAILY_CAP_USD"),
        out var v) ? v : 0.50m;

    const string SystemPrompt =
        "You are a defensive-finance risk analyst. Given a JSON object of " +
        "sub-component risk signals plus a final verdict and composite score, " +
        "write a 3-5 sentence executive summary that (1) restates the verdict " +
        "and score, (2) names the strongest 1-2 drivers from the components, " +
        "and (3) calls out any source marked 'unavailable'. Be specific, cite " +
        "component names verbatim, do not invent numbers, and output prose only " +
        "— no JSON, no headers, no preamble.";

    const int MaxOutTokens = 400;

    // Conservative upper bound for the configured Claude:Model with ~1100 input
    // tokens (system prompt + components JSON) and ~300 output tokens. Sonnet 4.6
    // pricing ($3/1M in, $15/1M out) lands ~$0.0078/call; Haiku 4.5 ($1/$5) lands
    // ~$0.0026. Using $0.01 makes the per-call charge conservative across either
    // model so the daily cap engages slightly early rather than late.
    const decimal EstimatedCostPerCallUsd = 0.01m;

    readonly Db _db;
    readonly ILogger<RiskAttestProLlm> _log;
    readonly IClaudeClient? _claude;
    readonly Func<string, Task<(string text, decimal costUsd)>>? _injector;

    public RiskAttestProLlm(
        Db db,
        ILogger<RiskAttestProLlm> log,
        Func<string, Task<(string text, decimal costUsd)>>? injector = null,
        IClaudeClient? claude = null)
    {
        _db = db;
        _log = log;
        _injector = injector;
        _claude = claude;
    }

    public async Task<string> NarrateAsync(
        string componentsHash,
        string componentsJson,
        string verdict,
        int scorePro,
        CancellationToken ct = default)
    {
        var cached = await ReadCacheAsync(componentsHash, ct);
        if (cached is not null) return cached;

        if (await SpendExceededAsync(ct))
        {
            _log.LogInformation(
                "RiskAttestProLlm budget cap hit; returning fallback for {Hash}",
                componentsHash);
            return FallbackTemplate(verdict, scorePro);
        }

        string text;
        decimal costUsd;
        try
        {
            var prompt = BuildPrompt(componentsJson, verdict, scorePro);
            var (t, c) = _injector is not null
                ? await _injector(prompt)
                : await LiveAnthropicCallAsync(prompt, ct);
            text = t;
            costUsd = c;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Anthropic narration failed; using fallback");
            return FallbackTemplate(verdict, scorePro);
        }

        await WriteCacheAsync(componentsHash, text, ct);
        await IncrementSpendAsync(costUsd, ct);
        return text;
    }

    static string BuildPrompt(string componentsJson, string verdict, int scorePro) =>
        $"Verdict: {verdict}. Composite score: {scorePro}/100.\n\nComponents:\n{componentsJson}";

    async Task<(string text, decimal costUsd)> LiveAnthropicCallAsync(string userPrompt, CancellationToken ct)
    {
        if (_claude is null)
            throw new InvalidOperationException(
                "IClaudeClient not available (DI missing or test injector not provided)");
        var text = await _claude.CompleteAsync(SystemPrompt, userPrompt, MaxOutTokens, ct);
        return (text.Trim(), EstimatedCostPerCallUsd);
    }

    static string FallbackTemplate(string verdict, int scorePro) =>
        $"Verdict: {verdict} (score {scorePro}/100). Composite drivers spanned 7 cross-bot signals; review the rich JSON for per-source details. (LLM narration unavailable: daily budget cap hit. Engage operator if narration is required.)";

    async Task<bool> SpendExceededAsync(CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(llm_cost_usd), 0) FROM risk_attest_pro_spend WHERE day_utc = strftime('%Y-%m-%d', 'now');";
        var raw = await cmd.ExecuteScalarAsync(ct);
        var spent = raw is null || raw is DBNull ? 0m : Convert.ToDecimal(raw);
        return spent >= DailyCapUsd;
    }

    async Task IncrementSpendAsync(decimal costUsd, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO risk_attest_pro_spend (day_utc, llm_calls, llm_cost_usd)
            VALUES (strftime('%Y-%m-%d','now'), 1, $c)
            ON CONFLICT(day_utc) DO UPDATE SET
                llm_calls    = llm_calls + 1,
                llm_cost_usd = llm_cost_usd + excluded.llm_cost_usd;";
        cmd.Parameters.AddWithValue("$c", costUsd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    async Task<string?> ReadCacheAsync(string componentsHash, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT response_json FROM risk_attest_pro_cache WHERE wallet_chain = $k;";
        cmd.Parameters.AddWithValue("$k", "narr:" + componentsHash);
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    async Task WriteCacheAsync(string componentsHash, string text, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO risk_attest_pro_cache (wallet_chain, response_json, attestation_uid, generated_at, expires_at)
            VALUES ($k, $t, '', strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now','+1 hour'));";
        cmd.Parameters.AddWithValue("$k", "narr:" + componentsHash);
        cmd.Parameters.AddWithValue("$t", text);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
