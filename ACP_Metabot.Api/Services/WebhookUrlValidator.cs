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

    public static async Task<Result> ValidateAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new Result(false, "webhookUrl is empty");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new Result(false, "webhookUrl is not a valid absolute URL");

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return new Result(false, "webhookUrl must use https");

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            return new Result(false, "webhookUrl has no host");

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
            // 192.168.0.0/16 — RFC1918 private
            if (b[0] == 192 && b[1] == 168) return true;
            // 224.0.0.0/4 — multicast
            if (b[0] >= 224 && b[0] <= 239) return true;
            // 240.0.0.0/4 — reserved
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
            // Unique-local fc00::/7
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
            return false;
        }

        // Unknown address family — block defensively.
        return true;
    }
}
