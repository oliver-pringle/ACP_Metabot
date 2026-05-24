using System.Net;
using System.Net.Sockets;

namespace ACP_Metabot.Api.Services;

/// SocketsHttpHandler.ConnectCallback for webhook delivery. Re-validates the
/// resolved IPEndPoint against WebhookUrlValidator.IsConnectBlocked at TCP
/// connect time. Closes the DNS-rebind TOCTOU window between Metabot's
/// per-hop WebhookUrlValidator.ValidateAsync call and HttpClient's own
/// connect-time DNS resolution. Defense-in-depth.
///
/// Portfolio pattern (ACP_OracleBot v0.7 / ACP_WitnessBot d140292 /
/// ACP_SolanaBot 2026-05-24 / ACP_ChainlinkBot 2026-05-22 /
/// ACP_ButlerBridgeBot v0.2.2 / ACP_MEVProtect 2026-05-24).
public static class WebhookConnectCallbacks
{
    public static async ValueTask<Stream> PinValidatedIp(
        SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
            addresses = new[] { literal };
        else
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        Exception? lastError = null;
        foreach (var addr in addresses)
        {
            if (WebhookUrlValidator.IsConnectBlocked(addr, out var reason))
            {
                lastError = new HttpRequestException(
                    $"webhook connect target {addr} blocked: {reason}");
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
        throw lastError ?? new HttpRequestException($"no addresses resolved for {host}");
    }
}
