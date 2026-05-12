using System.Net;
using System.Net.Http;
using Nethereum.JsonRpc.Client;

namespace ACP_Metabot.Api.Services;

// Shared retry-with-jittered-backoff helper for transient RPC failures on
// Base mainnet public endpoints. Free RPCs (mainnet.base.org, publicnode,
// drpc.org) all 429 a single eth_getLogs caller after a burst of a few
// dozen calls; without retry the warmer's 90-day cold-start scan (~390
// chunked calls per agent) can't complete a single agent.
//
// Retries: 429 / 5xx / network-level failures. Does NOT retry cancellation,
// argument errors, or 4xx-other (those are real bugs).
public static class RpcRetry
{
    public const int DefaultMaxAttempts = 5;

    // Exposed for tests so they don't actually sleep.
    public static Func<TimeSpan, CancellationToken, Task> Delayer { get; set; }
        = (t, ct) => Task.Delay(t, ct);

    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        string description,
        Microsoft.Extensions.Logging.ILogger? logger,
        CancellationToken ct,
        int maxAttempts = DefaultMaxAttempts)
    {
        Exception? lastEx = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                // 250ms, 500ms, 1s, 2s, 4s × jitter [0.5, 1.5].
                var baseMs = 250 * (1 << (attempt - 1));
                var jitter = 0.5 + Random.Shared.NextDouble();
                await Delayer(TimeSpan.FromMilliseconds(baseMs * jitter), ct);
            }
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                lastEx = ex;
                logger?.LogWarning(
                    "[rpc-retry] {Desc} attempt {N}/{Max} failed: {Type} {Msg}",
                    description, attempt + 1, maxAttempts, ex.GetType().Name, ex.Message);
            }
        }
        throw lastEx ?? new InvalidOperationException("RpcRetry exhausted with no captured exception");
    }

    public static bool IsRetryable(Exception ex)
    {
        // Nethereum wraps HttpRequestException in RpcClientUnknownException.
        if (ex is RpcClientUnknownException rcue && rcue.InnerException is HttpRequestException innerHttp)
            return IsRetryableHttpStatus(innerHttp.StatusCode);
        if (ex is HttpRequestException http)
            return IsRetryableHttpStatus(http.StatusCode);
        // Bare RPC timeout from Nethereum (no inner HttpRequestException).
        if (ex is RpcClientTimeoutException) return true;
        return false;
    }

    private static bool IsRetryableHttpStatus(HttpStatusCode? status)
    {
        // Network-level failure (DNS, TCP, TLS) surfaces as a null StatusCode.
        if (status is null) return true;
        if (status == HttpStatusCode.TooManyRequests) return true;
        if ((int)status >= 500 && (int)status < 600) return true;
        return false;
    }
}
