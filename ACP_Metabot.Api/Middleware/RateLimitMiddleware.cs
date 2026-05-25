using System.Collections.Concurrent;

namespace ACP_Metabot.Api.Middleware;

/// Two-bucket sliding-window rate limit on heavy + public endpoints. Closes
/// the portfolio gap surfaced in the 2026-05-24 coverage scan: pre-2026-05-24
/// Metabot had NO rate limit at all, despite being the marketplace gateway
/// behind api.acp-metabot.dev. A leaked X-API-Key OR an unauth /v1/search
/// burst could exhaust Voyage / Cohere / Claude embedding budgets.
///
///   1. Per-X-API-Key bucket — 600 req/min default.
///   2. Per-client-IP bucket — 60 req/min default. Defends against
///      unauthenticated abuse on the public /v1/* gateway AND against
///      stolen-key attackers who are still bound to one IP per session.
///
/// Placed BEFORE the X-API-Key middleware so unauthenticated floods on
/// the public /v1/search + /v1/digest + /v1/compose path also throttle.
/// Portfolio pattern (ported from ACP_OracleBot v0.7 / ACP_ChainlinkBot v1.3.1
/// / ACP_EASIssuer v1.1.1 / ACP_MEVProtect 2026-05-24).
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly int             _apiKeyCapacity;
    private readonly int             _ipCapacity;
    private readonly TimeSpan        _window;

    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _apiKeyBuckets = new();
    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _ipBuckets     = new();

    private long _tickCounter;
    private const int EvictEveryNTicks = 256;

    // Heavy path prefixes — embedding-heavy (Voyage), LLM-heavy (Claude),
    // RPC-heavy (chain reads), DB-heavy (FTS searches), or subscription
    // binds that mutate state + schedule background work.
    private static readonly string[] HeavyPathPrefixes =
    {
        // Public gateway — used by acp-find plugin + orchestrators
        "/v1/search",
        "/v1/digest",
        "/v1/today",
        "/v1/composeStack",
        "/v1/compose",
        // Risk + reputation endpoints (cross-bot consumers + buyer reads)
        "/v1/risk",
        "/v1/agentReputation",
        "/v1/internal/agentReputation",
        // Internal subscription binds (state mutation)
        "/v1/internal/marketplace/pulse",
        "/v1/internal/risk/watch",
        "/v1/internal/watch",
        // Internal heavy reads
        "/v1/internal/preHireBudgetCheck",
        "/v1/internal/sellerCoachingPack",
        "/v1/internal/buyerOrchestrate",
        // Smoke + agent endpoints
        "/v1/smoke/check",
        "/v1/agents",
        // Marketplace-pulse / risk subscription tier
        "/subscriptions",
    };

    public RateLimitMiddleware(RequestDelegate next, IConfiguration cfg)
    {
        _next = next;
        _apiKeyCapacity = cfg.GetValue("RateLimit:HeavyEndpointCapPerApiKey", 600);
        _ipCapacity     = cfg.GetValue("RateLimit:HeavyEndpointCapPerIp",      60);
        _window         = TimeSpan.FromMinutes(1);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";

        var isHeavy = false;
        foreach (var prefix in HeavyPathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { isHeavy = true; break; }
        }
        if (!isHeavy)
        {
            await _next(ctx);
            return;
        }

        if (ctx.Request.Headers.TryGetValue("X-API-Key", out var keyHeader) &&
            !string.IsNullOrEmpty(keyHeader.ToString()))
        {
            var keyHash = HashForBucket(keyHeader.ToString());
            if (!TryReserve(_apiKeyBuckets, keyHash, _apiKeyCapacity))
            {
                await Write429(ctx, $"rate limit exceeded; {_apiKeyCapacity} req/min per X-API-Key on heavy endpoints");
                return;
            }
        }

        var ip = ResolveClientIp(ctx);
        if (!TryReserve(_ipBuckets, ip, _ipCapacity))
        {
            await Write429(ctx, $"rate limit exceeded; {_ipCapacity} req/min per client IP on heavy endpoints");
            return;
        }

        MaybeEvict();
        await _next(ctx);
    }

    private bool TryReserve(
        ConcurrentDictionary<string, (DateTime WindowStart, int Count)> buckets,
        string key,
        int capacity)
    {
        var now = DateTime.UtcNow;
        var bucket = buckets.AddOrUpdate(key,
            _ => (now, 1),
            (_, b) => now - b.WindowStart > _window ? (now, 1) : (b.WindowStart, b.Count + 1));
        return bucket.Count <= capacity;
    }

    private void MaybeEvict()
    {
        if ((Interlocked.Increment(ref _tickCounter) % EvictEveryNTicks) != 0) return;
        var cutoff = DateTime.UtcNow - _window - _window;
        foreach (var kvp in _apiKeyBuckets)
            if (kvp.Value.WindowStart < cutoff) _apiKeyBuckets.TryRemove(kvp.Key, out _);
        foreach (var kvp in _ipBuckets)
            if (kvp.Value.WindowStart < cutoff) _ipBuckets.TryRemove(kvp.Key, out _);
    }

    private static async Task Write429(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.Response.WriteAsJsonAsync(new { error = message });
    }

    private static string HashForBucket(string raw)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 16);
    }

    // 2026-05-25 hardening (audit #1): use the post-UseForwardedHeaders
    // RemoteIpAddress, not raw X-Forwarded-For. Program.cs (lines 273-304)
    // already configures ForwardedHeadersOptions with the trusted proxy
    // network via TRUSTED_PROXY_NETWORKS — RemoteIpAddress is the real
    // client after the trusted hop. The pre-fix manual XFF parse bypassed
    // that trust boundary; rotating fake headers per request was the
    // documented bypass.
    private static string ResolveClientIp(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
