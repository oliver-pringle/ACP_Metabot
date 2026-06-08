using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ACP_Metabot.Api.Services.AcpClients;

/// P39: SocketsHttpHandler.ConnectCallback for INTERNAL, X-API-Key-bearing
/// cross-bot HttpClients. Without a connect-time pin + no-redirect, a post-boot
/// DNS-rebind of an internal hostname (or a 3xx from a compromised peer) could
/// bounce a key-bearing request to cloud-metadata / link-local and exfiltrate
/// the key.
///
/// Re-resolves at every physical connect and pins the socket to the resolved
/// address. RFC1918 / loopback are PERMITTED (the acp-shared docker peers) as
/// are public IPs; only the genuinely dangerous rebind targets are blocked —
/// link-local incl. 169.254.169.254 cloud-metadata, 0.0.0.0/8, multicast,
/// reserved, IPv6 variants. Lifted from ACP_OracleBot / ACP_PrivateTrader.
public static class InternalConnectCallbacks
{
    public static async ValueTask<Stream> PinResolvedIp(
        SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        if (addresses.Length == 0)
            throw new HttpRequestException($"no addresses resolved for {host}");

        Exception? lastError = null;
        foreach (var addr in addresses)
        {
            if (IsDangerous(addr))
            {
                lastError = new HttpRequestException(
                    $"internal connect target {addr} blocked (metadata/link-local/reserved)");
                continue;
            }
            try
            {
                var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addr, port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new HttpRequestException($"no usable address for {host}");
    }

    private static bool IsDangerous(IPAddress addr)
    {
        if (addr.IsIPv6LinkLocal) return true;
        if (addr.IsIPv6Multicast) return true;
        if (IPAddress.IPv6Any.Equals(addr)) return true;

        if (addr.AddressFamily == AddressFamily.InterNetwork || addr.IsIPv4MappedToIPv6)
        {
            var b = addr.MapToIPv4().GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return true; // link-local + cloud metadata 169.254.169.254
            if (b[0] == 0) return true;                  // 0.0.0.0/8 "this host"
            if ((b[0] & 0xf0) == 0xe0) return true;      // multicast 224/4
            if ((b[0] & 0xf0) == 0xf0) return true;      // reserved 240/4
        }
        return false;
    }
}
