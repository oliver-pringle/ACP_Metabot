using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// Cross-bot contract for ACP_SecurityBot's internal scan endpoint. Extracted so
/// SecurityScanWorker tests can substitute a fake without an HttpClient.
/// </summary>
public interface ITheSecurityBotClient
{
    /// <summary>
    /// Scan the target agent over acp-shared. Returns a ScanResult: the latest-cache
    /// verdict PLUS the raw findings JSON + raw verdict discriminator for the history
    /// append. NEVER throws — a non-2xx / transport / parse failure maps to
    /// status=error so the worker loop continues.
    /// </summary>
    Task<ScanResult> ScanAsync(string agentAddress, CancellationToken ct = default);
}

/// <summary>
/// One scan's outcome: the latest-cache verdict PLUS the raw findings JSON +
/// raw verdict discriminator needed to append a full security_scan_history row.
/// RawFindingsJson is the verbatim findings[] array (null for not_auditable /
/// error / empty). RawVerdict is SecurityBot's verdict discriminator string.
/// </summary>
public sealed record ScanResult(
    SecurityVerdict Verdict,
    string? RawFindingsJson,
    string? RawVerdict);

/// <summary>
/// HTTP client for ACP_SecurityBot's <c>POST /v1/internal/scan</c> over the
/// <c>acp-shared</c> bridge. Auth: <c>X-API-Key</c> = SecurityBot's
/// <c>INTERNAL_API_KEY</c>, exposed in Metabot's env as
/// <c>THESECURITYBOT_API_KEY</c> (mapped to config <c>TheSecurityBot:ApiKey</c>
/// in docker-compose). The scan is FREE — no ACP escrow.
/// </summary>
public sealed class TheSecurityBotClient : ITheSecurityBotClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<TheSecurityBotClient> _log;

    public TheSecurityBotClient(IHttpClientFactory httpFactory, IConfiguration config,
        IHostEnvironment env, ILogger<TheSecurityBotClient> log)
    {
        _httpFactory = httpFactory;
        _apiKey = config["TheSecurityBot:ApiKey"] ?? "";
        _log = log;

        // P17: fail-fast in non-Development when BaseUrl is set but the key is
        // empty (silent-401 closer), matching TheChainlinkBotClient.
        var baseUrl = config["TheSecurityBot:BaseUrl"] ?? "";
        var integrationEnabled = !string.IsNullOrEmpty(baseUrl);
        if (integrationEnabled && string.IsNullOrEmpty(_apiKey) && !env.IsDevelopment())
        {
            throw new InvalidOperationException(
                "TheSecurityBot:ApiKey is required in non-Development when " +
                "TheSecurityBot:BaseUrl is set. Set both env vars in lock-step, " +
                "or unset BaseUrl to disable the cross-bot integration.");
        }
    }

    public async Task<ScanResult> ScanAsync(string agentAddress, CancellationToken ct = default)
    {
        var addr = agentAddress.ToLowerInvariant();
        var nowIso = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        try
        {
            var http = _httpFactory.CreateClient("thesecuritybot");
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/internal/scan")
            {
                Content = JsonContent.Create(new { agentAddress })
            };
            req.Headers.Add("X-API-Key", _apiKey);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Log the status only — never the raw upstream body (P30/P63).
                _log.LogWarning("[thesecuritybot] /v1/internal/scan {Addr} -> {Status}",
                    addr, (int)resp.StatusCode);
                return new ScanResult(Error(addr, nowIso, $"HTTP {(int)resp.StatusCode}"), null, null);
            }

            var dto = await resp.Content.ReadFromJsonAsync<ScanResponseDto>(cancellationToken: ct);
            if (dto is null)
                return new ScanResult(Error(addr, nowIso, "empty response"), null, null);

            if (string.Equals(dto.Verdict, "NOT_AUDITABLE", StringComparison.OrdinalIgnoreCase))
            {
                var na = new SecurityVerdict(addr, SecurityStatus.NotAuditable,
                    null, null, null, null, null, nowIso, null, null);
                return new ScanResult(na, null, dto.Verdict);
            }

            // Walk the raw findings[] element once: count + severity histogram +
            // keep the verbatim JSON for the history row. Defensive: a missing /
            // non-array findings element degrades to 0 findings + null raw json.
            int findingCount = 0;
            var sevCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? rawFindingsJson = null;
            if (dto.Findings is JsonElement fe && fe.ValueKind == JsonValueKind.Array)
            {
                rawFindingsJson = fe.GetRawText(); // verbatim array, all fields preserved
                foreach (var f in fe.EnumerateArray())
                {
                    findingCount++;
                    if (f.ValueKind == JsonValueKind.Object &&
                        f.TryGetProperty("severity", out var sev) &&
                        sev.ValueKind == JsonValueKind.String)
                    {
                        var s = sev.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            sevCounts[s] = sevCounts.TryGetValue(s, out var c) ? c + 1 : 1;
                    }
                }
            }

            var verdict = new SecurityVerdict(
                AgentAddress:       addr,
                Status:             SecurityStatus.Scanned,
                Score:              dto.Score,
                Grade:              dto.Grade,
                ObservableCount:    dto.ObservableCount,
                FindingCount:       findingCount,
                SeverityCountsJson: JsonSerializer.Serialize(sevCounts),
                ScannedAt:          NormalizeIso(dto.ScannedAt, nowIso),
                CorpusVersion:      null, // the internal scan response doesn't expose corpusVersion
                LastError:          null);
            return new ScanResult(verdict, rawFindingsJson, dto.Verdict);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // honour shutdown
        }
        catch (Exception ex)
        {
            _log.LogWarning("[thesecuritybot] scan {Addr} failed: {Type}", addr, ex.GetType().Name);
            return new ScanResult(Error(addr, nowIso, ex.GetType().Name), null, null);
        }
    }

    // Normalize an upstream timestamp to UTC ISO-8601 "O" (…Z) so the
    // security_scan_history string ORDER BY scanned_at is always valid; fall back
    // to nowIso on parse failure. dto.UtcDateTime (Kind=Utc) renders with the "Z"
    // suffix, matching DateTime.UtcNow.ToString("O").
    private static string NormalizeIso(string? raw, string fallback)
    {
        if (string.IsNullOrEmpty(raw)) return fallback;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : fallback;
    }

    private static SecurityVerdict Error(string addr, string nowIso, string reason) =>
        new(addr, SecurityStatus.Error, null, null, null, null, null, nowIso, null, reason);

    private sealed record ScanResponseDto(
        [property: JsonPropertyName("verdict")]         string? Verdict,
        [property: JsonPropertyName("score")]           int? Score,
        [property: JsonPropertyName("grade")]           string? Grade,
        [property: JsonPropertyName("observableCount")] int? ObservableCount,
        [property: JsonPropertyName("totalPatterns")]   int? TotalPatterns,
        [property: JsonPropertyName("scannedAt")]       string? ScannedAt,
        [property: JsonPropertyName("findings")]        JsonElement? Findings);
}
