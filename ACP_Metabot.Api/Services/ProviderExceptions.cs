namespace ACP_Metabot.Api.Services;

// Typed exceptions for upstream Voyage / Claude failures. The metrics
// middleware catches these to tag request_log rows with provider_error
// like "voyage_429" or "claude_502", then rethrows so the existing
// 500-handling pathway runs unchanged.
//
// StatusCode == 0 means a network/transport failure (DNS, TLS, timeout,
// connection reset) — i.e. no HTTP response was ever received.

public sealed class VoyageApiException : Exception
{
    public int StatusCode { get; }
    public string? UpstreamBody { get; }

    public VoyageApiException(int statusCode, string? upstreamBody, string message)
        : base(message)
    {
        StatusCode = statusCode;
        UpstreamBody = upstreamBody;
    }

    public VoyageApiException(int statusCode, string? upstreamBody, string message, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
        UpstreamBody = upstreamBody;
    }
}

public sealed class ClaudeApiException : Exception
{
    public int StatusCode { get; }
    public string? UpstreamBody { get; }

    public ClaudeApiException(int statusCode, string? upstreamBody, string message)
        : base(message)
    {
        StatusCode = statusCode;
        UpstreamBody = upstreamBody;
    }

    public ClaudeApiException(int statusCode, string? upstreamBody, string message, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
        UpstreamBody = upstreamBody;
    }
}
