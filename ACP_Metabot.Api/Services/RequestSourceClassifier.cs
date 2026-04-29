namespace ACP_Metabot.Api.Services;

// Classify each incoming request into one of three traffic sources for
// the request_log.source column. Pure helper, evaluated once per request
// by RequestMetricsMiddleware.
//
//   - "mcp_plugin"   = /v1/* with User-Agent "acp-find-plugin/<ver>"
//   - "public_other" = /v1/* with any other UA
//   - "internal"     = anything else (X-API-Key gated paths). The optional
//                      X-Caller header (free-text, e.g. "sidecar",
//                      "defieval") is captured into caller_id for
//                      future per-bot breakdowns without a schema change.
public static class RequestSourceClassifier
{
    private const string McpUserAgentPrefix = "acp-find-plugin/";

    public static (string Source, string? CallerId) Classify(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {
            var ua = ctx.Request.Headers.UserAgent.ToString();
            if (!string.IsNullOrEmpty(ua) &&
                ua.StartsWith(McpUserAgentPrefix, StringComparison.Ordinal))
            {
                return ("mcp_plugin", null);
            }
            return ("public_other", null);
        }

        var caller = ctx.Request.Headers["X-Caller"].ToString();
        return ("internal", string.IsNullOrEmpty(caller) ? null : caller);
    }
}
