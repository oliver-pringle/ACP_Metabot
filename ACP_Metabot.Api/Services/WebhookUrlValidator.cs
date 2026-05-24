using System.Net;
using System.Net.Sockets;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// SSRF guard for buyer-supplied webhookUrl.
///
/// Called twice for every URL:
///   1. At watch registration — fail-fast before persisting state.
///   2. Before each webhook delivery — defends against DNS rebinding (a
///      malicious DNS server could return a public IP at registration
///      time and a private IP at delivery time).
/// </summary>
public static class WebhookUrlValidator
{
    public record Result(bool Ok, string? Reason);

    // 2026-05-24 hardening: bound URL length + restrict to standard webhook
    // port. Pre-hardening any-port + any-length was accepted.
    public const int MaxUrlLength = 2048;
    private const int AllowedHttpsPort = 443;

    public static async Task<Result> ValidateAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new Result(false, "webhookUrl is empty");

        if (url.Length > MaxUrlLength)
            return new Result(false, $"webhookUrl exceeds {MaxUrlLength} characters");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new Result(false, "webhookUrl is not a valid absolute URL");

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return new Result(false, "webhookUrl must use https");

        if (!uri.IsDefaultPort && uri.Port != AllowedHttpsPort)
            return new Result(false, $"webhookUrl port {uri.Port} not allowed for https:// in production; allowed: {AllowedHttpsPort}");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return new Result(false, "webhookUrl must not contain userinfo (user:pass@host)");

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            return new Result(false, "webhookUrl has no host");

        // Refuse raw non-ASCII unicode in hostname — require punycode form.
        foreach (var ch in host)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '.'))
                return new Result(false, $"webhookUrl host contains non-ASCII character '{ch}' — use punycode (xn--) form");
        }

        // Hostname blacklist (catches obvious cases without DNS).
        if (IsBlockedHostname(host))
            return new Result(false, $"webhookUrl hostname '{host}' is not allowed");

        // Resolve and check every address. If host is already an IP literal,
        // GetHostAddressesAsync returns just that address.
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, $"webhookUrl DNS lookup failed: {ex.Message}");
        }

        if (addresses.Length == 0)
            return new Result(false, "webhookUrl host has no DNS records");

        foreach (var ip in addresses)
        {
            if (IsPrivateOrInternal(ip))
                return new Result(false,
                    $"webhookUrl resolves to a private/internal address: {ip}");
        }

        return new Result(true, null);
    }

    /// 2026-05-24 hardening: connect-time IP guard for SocketsHttpHandler.
    /// ConnectCallback. Re-validates the actual resolved IPEndPoint against
    /// the same blocklist before the TCP connect, closing the DNS-rebind
    /// TOCTOU window between ValidateAsync's DNS lookup and HttpClient's
    /// own connect-time resolution. Portfolio defense-in-depth pattern.
    public static bool IsConnectBlocked(IPAddress addr, out string reason)
    {
        if (IsPrivateOrInternal(addr))
        {
            reason = $"blocked private/internal address {addr}";
            return true;
        }
        reason = "";
        return false;
    }

    private static bool IsBlockedHostname(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsPrivateOrInternal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 0.0.0.0/8 — "this network"
            if (b[0] == 0) return true;
            // 10.0.0.0/8 — RFC1918 private
            if (b[0] == 10) return true;
            // 100.64.0.0/10 — Carrier-grade NAT (RFC6598)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 127.0.0.0/8 — handled by IsLoopback above
            // 169.254.0.0/16 — link-local; includes 169.254.169.254 cloud metadata
            if (b[0] == 169 && b[1] == 254) return true;
            // 172.16.0.0/12 — RFC1918 private
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.0.0.0/24 — IETF Protocol Assignments (includes 192.0.0.0/29)
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;
            // 192.0.2.0/24 — TEST-NET-1 documentation (RFC 5737)
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;
            // 198.51.100.0/24 — TEST-NET-2 documentation (RFC 5737)
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
            // 203.0.113.0/24 — TEST-NET-3 documentation (RFC 5737)
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
            // 198.18.0.0/15 — benchmark testing (RFC 2544)
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
            // 192.168.0.0/16 — RFC1918 private
            if (b[0] == 192 && b[1] == 168) return true;
            // 224.0.0.0/4 — multicast
            if (b[0] >= 224 && b[0] <= 239) return true;
            // 240.0.0.0/4 — reserved (includes broadcast 255.255.255.255)
            if (b[0] >= 240) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv6Multicast) return true;
            if (ip.Equals(IPAddress.IPv6Any)) return true;
            // IPv4-mapped (::ffff:a.b.c.d) — re-check as v4
            if (ip.IsIPv4MappedToIPv6)
            {
                return IsPrivateOrInternal(ip.MapToIPv4());
            }
            var b = ip.GetAddressBytes();
            // Unique-local fc00::/7
            if ((b[0] & 0xFE) == 0xFC) return true;
            // 2001:db8::/32 documentation (RFC 3849)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true;
            // 64:ff9b::/96 IPv4/IPv6 translation (RFC 6052)
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) return true;
            return false;
        }

        // Unknown address family — block defensively.
        return true;
    }
}
