using System.Diagnostics;
using System.Text.Json;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

// Records every request into the request_log SQLite table via the
// MetricsChannel (fire-and-forget; the writer is MetricsWriterService).
//
// Pipeline position: AFTER UseRateLimiter (so 429s are recorded) and
// BEFORE the X-API-Key middleware (so 401s on internal paths are
// recorded — auth failures are themselves operationally important).
//
// Body / route capture is gated to the known fact-bearing endpoints:
//   POST /search, /v1/search                 -> body.query     -> query_text
//   POST /composeStack, /v1/composeStack     -> body.useCase   -> query_text
//   POST /agentReputation                    -> body.agentAddress -> agent_address
//   GET  /v1/agentReputation                 -> ?agent=        -> agent_address
//   GET  /agent/{address}, /v1/agent/{address} -> route segment -> agent_address
//
// Skips body capture for /metrics/* paths (still records the request).
public sealed class RequestMetricsMiddleware
{
    private const int BodyReadCapBytes = 2048;

    private readonly RequestDelegate _next;
    private readonly MetricsChannel  _channel;
    private readonly ILogger<RequestMetricsMiddleware> _logger;

    public RequestMetricsMiddleware(RequestDelegate next, MetricsChannel channel,
        ILogger<RequestMetricsMiddleware> logger)
    {
        _next = next;
        _channel = channel;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var sw     = Stopwatch.StartNew();
        var path   = ctx.Request.Path.Value ?? "";
        var method = ctx.Request.Method;

        var (source, callerId) = RequestSourceClassifier.Classify(ctx);
        var userAgent = ctx.Request.Headers.UserAgent.ToString();
        var remoteIp  = ctx.Connection.RemoteIpAddress?.ToString();

        var skipBodyCapture = path.StartsWith("/metrics/", StringComparison.OrdinalIgnoreCase);

        string? queryText    = null;
        string? agentAddress = null;

        if (!skipBodyCapture)
        {
            (queryText, agentAddress) = await TryCaptureFromRequestAsync(ctx, path, method);
        }

        string? providerError = null;
        bool    unhandled     = false;

        try
        {
            await _next(ctx);
        }
        catch (VoyageApiException ex)
        {
            providerError = $"voyage_{ex.StatusCode}";
            unhandled = true;
            throw;
        }
        catch (ClaudeApiException ex)
        {
            providerError = $"claude_{ex.StatusCode}";
            unhandled = true;
            throw;
        }
        catch
        {
            unhandled = true;
            throw;
        }
        finally
        {
            sw.Stop();

            // Path-based route capture (only for /agent/* and /v1/agent/* GETs)
            // is cheap enough to do unconditionally as a fallback.
            agentAddress ??= TryExtractAgentFromPath(path);

            var statusCode = ctx.Response.StatusCode;
            // If an unhandled exception bubbled out, ASP.NET's outer pipeline
            // will set the response to 500 AFTER our finally runs. Reflect
            // that in the recorded row.
            if (unhandled && statusCode < 400) statusCode = 500;

            var evt = new RequestMetricEvent(
                TimestampUtc:  DateTime.UtcNow,
                Endpoint:      path,
                Method:        method,
                StatusCode:    statusCode,
                DurationMs:    (int)sw.ElapsedMilliseconds,
                Source:        source,
                UserAgent:     string.IsNullOrEmpty(userAgent) ? null : userAgent,
                CallerId:      callerId,
                RemoteIp:      remoteIp,
                QueryText:     queryText,
                AgentAddress:  agentAddress,
                ProviderError: providerError);

            try { _channel.TryWrite(evt); }
            catch (Exception ex)
            {
                // The metrics path must never bubble its own failures to the
                // request response. Log and move on.
                _logger.LogWarning(ex, "[metrics] failed to enqueue event for {path}", path);
            }
        }
    }

    // Returns (queryText, agentAddress). Either may be null when not applicable.
    private static async Task<(string? QueryText, string? AgentAddress)> TryCaptureFromRequestAsync(
        HttpContext ctx, string path, string method)
    {
        // GET /v1/agentReputation?agent=0x...
        if (HttpMethods.IsGet(method) &&
            (path.Equals("/v1/agentReputation", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/agentReputation",    StringComparison.OrdinalIgnoreCase)))
        {
            var agent = ctx.Request.Query["agent"].ToString();
            return (null, string.IsNullOrEmpty(agent) ? null : agent.ToLowerInvariant());
        }

        // POST endpoints with bodies we want to peek at.
        if (!HttpMethods.IsPost(method)) return (null, null);

        var isSearch     = PathEqualsOneOf(path, "/search",          "/v1/search");
        var isCompose    = PathEqualsOneOf(path, "/composeStack",    "/v1/composeStack");
        var isReputation = PathEqualsOneOf(path, "/agentReputation"); // POST flavour, sidecar-only

        if (!isSearch && !isCompose && !isReputation) return (null, null);

        // Buffer the body so the downstream handler can re-read it.
        ctx.Request.EnableBuffering();
        var stream = ctx.Request.Body;
        var buffer = new byte[BodyReadCapBytes];
        int totalRead = 0;
        try
        {
            while (totalRead < BodyReadCapBytes)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(totalRead, BodyReadCapBytes - totalRead));
                if (n == 0) break;
                totalRead += n;
            }
        }
        catch
        {
            // Read failure shouldn't block the request — reset and bail.
            stream.Position = 0;
            return (null, null);
        }
        stream.Position = 0;

        if (totalRead == 0) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, totalRead));
            var root = doc.RootElement;

            if (isSearch && root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                return (q.GetString(), null);

            if (isCompose && root.TryGetProperty("useCase", out var uc) && uc.ValueKind == JsonValueKind.String)
                return (uc.GetString(), null);

            if (isReputation && root.TryGetProperty("agentAddress", out var a) && a.ValueKind == JsonValueKind.String)
                return (null, a.GetString()?.ToLowerInvariant());
        }
        catch
        {
            // Malformed JSON — let the handler return its own 400. We don't
            // need to crash the metrics path.
        }

        return (null, null);
    }

    // Extracts the wallet address from /agent/{address} or /v1/agent/{address}.
    // Returns null for any other path. Lowercased to match how the handlers
    // canonicalise addresses.
    private static string? TryExtractAgentFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Trim leading "/v1" if present, then expect "/agent/<addr>".
        var p = path;
        if (p.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)) p = p[3..];
        if (!p.StartsWith("/agent/", StringComparison.OrdinalIgnoreCase)) return null;

        var addr = p["/agent/".Length..];
        // Reject empty, query-string contamination, or further path segments.
        var slash = addr.IndexOf('/');
        if (slash >= 0) addr = addr[..slash];
        if (string.IsNullOrEmpty(addr)) return null;
        return addr.ToLowerInvariant();
    }

    private static bool PathEqualsOneOf(string path, params string[] options)
    {
        foreach (var o in options)
            if (path.Equals(o, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
