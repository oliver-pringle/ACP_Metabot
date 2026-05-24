using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

public sealed record NarrativeResult(
    string Summary,
    IReadOnlyList<PerResultReason> PerResultReason,
    IReadOnlyList<string> CitedOfferings,
    bool CacheHit,
    string Status);  // "ok" | "degraded_llm_unavailable" | "degraded_parse" | "degraded_no_results"

public sealed record PerResultReason(string Offering, string Reason);

/// <summary>
/// v1.10 Phase 3: Claude Haiku-backed search narrator. Wraps the top-5
/// offerings in a 3-5 sentence summary + per-result reasoning. Caches by
/// (query_canonical, corpus_version) for `Search:NarrativeCacheTtlSeconds`
/// (default 3600). NEVER throws — returns degraded envelope on any failure.
///
/// `corpus_version` is computed as `unixepoch() / TTL` — gives TTL-aligned
/// cache windows (default 1h) without needing a real corpus-mutation
/// counter. Two identical queries in the same hour return the same
/// narration; cache misses naturally on hour rollover.
/// </summary>
public sealed class SearchNarrator
{
    private readonly IClaudeClient _claude;
    private readonly Db _db;
    private readonly ILogger<SearchNarrator> _logger;
    private readonly int _cacheTtlSeconds;
    private readonly string _systemPrompt;
    private const string Model = "claude-haiku-4-5-20251001";
    private const int MaxOutTokens = 600;

    public SearchNarrator(IClaudeClient claude, Db db,
        IConfiguration config, ILogger<SearchNarrator> logger)
    {
        _claude = claude;
        _db = db;
        _logger = logger;
        _cacheTtlSeconds = Math.Max(60,
            config.GetValue<int?>("Search:NarrativeCacheTtlSeconds") ?? 3600);
        _systemPrompt = LoadPromptTemplate();
    }

    private static string LoadPromptTemplate()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "PromptTemplates", "SearchNarrate.md")
        };
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            candidates.Add(Path.Combine(root, "PromptTemplates", "SearchNarrate.md"));
            candidates.Add(Path.Combine(root, "ACP_Metabot.Api", "PromptTemplates", "SearchNarrate.md"));
            root = Path.GetDirectoryName(root) ?? root;
        }
        foreach (var p in candidates)
            if (File.Exists(p)) return File.ReadAllText(p);
        // Fallback so tests without the on-disk file still get something parseable.
        return "Return JSON only: {\"summary\":\"(no narrator template loaded)\",\"perResultReason\":[]}";
    }

    public async Task<NarrativeResult> NarrateAsync(
        string query,
        IReadOnlyList<OfferingMatch> hits,
        IReadOnlyList<string>? previousQueries,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || hits.Count == 0)
            return new NarrativeResult(
                "No results to narrate.",
                Array.Empty<PerResultReason>(),
                Array.Empty<string>(),
                CacheHit: false,
                Status: "degraded_no_results");

        var canonical = query.Trim().ToLowerInvariant();
        var corpusVersion = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / _cacheTtlSeconds;

        // Cache lookup. Best-effort — any read failure falls through to an
        // LLM call so a transient DB hiccup doesn't break narration.
        try
        {
            var cached = await TryReadCacheAsync(canonical, corpusVersion, ct);
            if (cached is not null) return cached with { CacheHit = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[search-narrator] cache read failed");
        }

        var userPrompt = BuildUserPrompt(query, hits, previousQueries);

        string llmResponse;
        try
        {
            llmResponse = await _claude.CompleteAsync(_systemPrompt, userPrompt, MaxOutTokens, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[search-narrator] Claude call failed");
            return Degraded("degraded_llm_unavailable", hits);
        }

        NarrativeResult parsed;
        try { parsed = ParseLlmResponse(llmResponse, hits); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[search-narrator] parse failed");
            return Degraded("degraded_parse", hits);
        }

        // Best-effort write — a cache failure must not mask the successful
        // narration we already have in hand.
        try { await WriteCacheAsync(canonical, corpusVersion, parsed, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[search-narrator] cache write failed"); }

        return parsed;
    }

    private static string BuildUserPrompt(
        string query, IReadOnlyList<OfferingMatch> hits, IReadOnlyList<string>? previousQueries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Query: {query}");
        if (previousQueries is { Count: > 0 })
        {
            sb.AppendLine("Previous queries:");
            foreach (var q in previousQueries.Take(5))
                sb.AppendLine($"  - {q}");
        }
        sb.AppendLine($"Top {hits.Count} offerings:");
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            var shortAgent = ShortAgent(h.AgentAddress);
            sb.AppendLine($"  {i + 1}. [{h.OfferingName}@{shortAgent}] - ${h.PriceUsdc:F2}, category={h.Category ?? "(none)"}, score={h.Score:F2}");
            if (!string.IsNullOrWhiteSpace(h.Description))
            {
                var desc = h.Description.Length > 200 ? h.Description.Substring(0, 200) + "..." : h.Description;
                sb.AppendLine($"     {desc}");
            }
        }
        return sb.ToString();
    }

    private static string ShortAgent(string? address)
    {
        if (string.IsNullOrEmpty(address)) return "";
        var stripped = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? address.Substring(2)
            : address;
        return stripped.Length <= 6 ? stripped : stripped.Substring(0, 6);
    }

    private static NarrativeResult ParseLlmResponse(string response, IReadOnlyList<OfferingMatch> hits)
    {
        var trimmed = response.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) throw new JsonException("no JSON object");
        var json = trimmed.Substring(start, end - start + 1);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var summary = root.TryGetProperty("summary", out var s) ? (s.GetString() ?? "") : "";
        var reasons = new List<PerResultReason>();
        if (root.TryGetProperty("perResultReason", out var pr) && pr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var off = item.TryGetProperty("offering", out var o) ? (o.GetString() ?? "") : "";
                var reason = item.TryGetProperty("reason", out var r) ? (r.GetString() ?? "") : "";
                reasons.Add(new PerResultReason(off, reason));
            }
        }
        var cited = hits.Select(h => $"{h.OfferingName}@{h.AgentAddress}").ToList();
        return new NarrativeResult(summary, reasons, cited, CacheHit: false, Status: "ok");
    }

    private static NarrativeResult Degraded(string status, IReadOnlyList<OfferingMatch> hits)
    {
        var cited = hits.Select(h => $"{h.OfferingName}@{h.AgentAddress}").ToList();
        return new NarrativeResult(
            "Narration unavailable; returning offerings without summary.",
            Array.Empty<PerResultReason>(),
            cited, CacheHit: false, Status: status);
    }

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task<NarrativeResult?> TryReadCacheAsync(
        string canonical, long corpusVersion, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT narrative_json FROM search_narratives_cache
            WHERE query_canonical = $q AND corpus_version = $v;";
        cmd.Parameters.AddWithValue("$q", canonical);
        cmd.Parameters.AddWithValue("$v", corpusVersion);
        var v = await cmd.ExecuteScalarAsync(ct);
        if (v is null || v == DBNull.Value) return null;
        var json = (string)v;
        try
        {
            return JsonSerializer.Deserialize<NarrativeResult>(json, CacheJsonOptions);
        }
        catch { return null; }
    }

    private async Task WriteCacheAsync(
        string canonical, long corpusVersion, NarrativeResult result, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(result, CacheJsonOptions);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO search_narratives_cache(query_canonical, corpus_version, narrative_json, generated_at)
            VALUES ($q, $v, $j, datetime('now'))
            ON CONFLICT(query_canonical, corpus_version) DO UPDATE SET
                narrative_json = excluded.narrative_json,
                generated_at = excluded.generated_at;";
        cmd.Parameters.AddWithValue("$q", canonical);
        cmd.Parameters.AddWithValue("$v", corpusVersion);
        cmd.Parameters.AddWithValue("$j", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
