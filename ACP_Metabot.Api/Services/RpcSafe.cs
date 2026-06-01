namespace ACP_Metabot.Api.Services;

/// <summary>
/// P57 — RPC URL scheme guard. Every URL fed into a Nethereum client MUST be
/// validated for an https:// scheme before construction (http:// permitted only
/// for loopback so local dev / a docker-internal node still works). Keeps RPC
/// keys (Alchemy/Infura/QuickNode embed them in the URL) off the wire in
/// plaintext and blocks a misconfigured plaintext endpoint from silently
/// degrading the indexer.
/// </summary>
public static class RpcSafe
{
    public static string RequireHttps(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host == "localhost"))))
            throw new InvalidOperationException($"{name} must be an absolute https:// URL (http:// permitted only for loopback); got '{url}'");
        return url;
    }
}
