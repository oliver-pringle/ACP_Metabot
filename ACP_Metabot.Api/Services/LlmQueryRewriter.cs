using System.Text.Json;
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

public sealed record QueryRewriteResult(string Intent, IReadOnlyList<string> Synonyms);

/// <summary>
/// v1.10 Phase 3: Claude Haiku-backed query rewriter. Daily + per-call
/// USD caps enforced via the query_rewrite_spend table. Returns the
/// original query unchanged + intent="passthrough" when (a) ANTHROPIC_API_KEY
/// is unset, (b) daily cap is breached, (c) LLM call errors, (d) response
/// parse fails. Never throws — the search path must remain functional even
/// when the rewriter is degraded.
/// </summary>
public sealed class LlmQueryRewriter
{
    private readonly IClaudeClient _claude;
    private readonly Db _db;
    private readonly ILogger<LlmQueryRewriter> _logger;
    private readonly double _dailyCapUsd;
    private readonly double _perCallCapUsd;
    private readonly string _systemPrompt;
    private const string Model = "claude-haiku-4-5-20251001";
    private const int MaxOutTokens = 150;

    // Haiku 4.5 pricing 2026-05: ~$0.80/Min input, ~$4/Mout output.
    // Estimated per-call: ~250 input + ~150 output → ~$0.0008.
    private const double EstimatedCostPerCallUsd = 0.0008;

    public LlmQueryRewriter(IClaudeClient claude, Db db,
        IConfiguration config, ILogger<LlmQueryRewriter> logger)
    {
        _claude = claude;
        _db = db;
        _logger = logger;
        _dailyCapUsd = config.GetValue<double?>("Search:QueryRewriterDailyUsdCap") ?? 0.50;
        _perCallCapUsd = config.GetValue<double?>("Search:QueryRewriterMaxUsdPerCall") ?? 0.002;
        _systemPrompt = LoadPromptTemplate();
    }

    private static string LoadPromptTemplate()
    {
        var candidates = new List<string>();
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "PromptTemplates", "QueryRewrite.md"));
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            candidates.Add(Path.Combine(root, "PromptTemplates", "QueryRewrite.md"));
            candidates.Add(Path.Combine(root, "ACP_Metabot.Api", "PromptTemplates", "QueryRewrite.md"));
            root = Path.GetDirectoryName(root) ?? root;
        }
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        // Fallback so tests without the on-disk file still produce something parseable.
        return "Return JSON only: {\"intent\":\"other\",\"synonyms\":[]}";
    }

    public async Task<QueryRewriteResult> RewriteAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new QueryRewriteResult("passthrough", Array.Empty<string>());

        double spent;
        try { spent = await GetDailySpendUsdAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[query-rewriter] spend lookup failed; passthrough");
            return new QueryRewriteResult("passthrough", Array.Empty<string>());
        }
        if (spent + _perCallCapUsd > _dailyCapUsd)
        {
            _logger.LogWarning("[query-rewriter] daily cap breached ({Spent:F4}/{Cap:F2} USD); passthrough",
                spent, _dailyCapUsd);
            return new QueryRewriteResult("passthrough", Array.Empty<string>());
        }

        string response;
        try
        {
            response = await _claude.CompleteAsync(_systemPrompt, query, MaxOutTokens, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[query-rewriter] Claude call failed; passthrough");
            return new QueryRewriteResult("passthrough", Array.Empty<string>());
        }

        try { await RecordSpendAsync(query, EstimatedCostPerCallUsd, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[query-rewriter] spend recording failed"); }

        try
        {
            var trimmed = response.Trim();
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start < 0 || end <= start)
                return new QueryRewriteResult("other", Array.Empty<string>());
            var json = trimmed.Substring(start, end - start + 1);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var intent = root.TryGetProperty("intent", out var i)
                ? (i.GetString() ?? "other")
                : "other";
            var synonyms = new List<string>();
            if (root.TryGetProperty("synonyms", out var s) && s.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in s.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var v = item.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(v) && synonyms.Count < 5)
                        synonyms.Add(v);
                }
            }
            return new QueryRewriteResult(intent, synonyms);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[query-rewriter] response parse failed");
            return new QueryRewriteResult("other", Array.Empty<string>());
        }
    }

    public async Task<double> GetDailySpendUsdAsync(CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(usd_amount), 0.0)
            FROM query_rewrite_spend
            WHERE spent_at > datetime('now', '-24 hours');";
        var v = await cmd.ExecuteScalarAsync(ct);
        return v == DBNull.Value || v is null ? 0.0 : Convert.ToDouble(v);
    }

    private async Task RecordSpendAsync(string query, double usd, CancellationToken ct)
    {
        var queryHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(query.Trim().ToLowerInvariant())))
            .ToLowerInvariant()
            .Substring(0, 16);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO query_rewrite_spend(spent_at, usd_amount, query_hash, rewriter_model)
            VALUES (datetime('now'), $u, $h, $m);";
        cmd.Parameters.AddWithValue("$u", usd);
        cmd.Parameters.AddWithValue("$h", queryHash);
        cmd.Parameters.AddWithValue("$m", Model);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
