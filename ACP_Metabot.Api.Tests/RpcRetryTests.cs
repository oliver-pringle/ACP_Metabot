using System.Net;
using System.Net.Http;
using ACP_Metabot.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.JsonRpc.Client;
using Xunit;

namespace ACP_Metabot.Api.Tests;

public class RpcRetryTests : IDisposable
{
    public RpcRetryTests()
    {
        // Tests run with no real delay so they finish fast.
        RpcRetry.Delayer = (_, _) => Task.CompletedTask;
    }

    public void Dispose()
    {
        RpcRetry.Delayer = (t, ct) => Task.Delay(t, ct);
    }

    [Fact]
    public async Task ReturnsImmediatelyOnFirstSuccess()
    {
        var attempts = 0;
        var result = await RpcRetry.ExecuteAsync<int>(
            () => { attempts++; return Task.FromResult(42); },
            "ok", NullLogger.Instance, CancellationToken.None);
        Assert.Equal(42, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Retries429ThenSucceeds()
    {
        var attempts = 0;
        var result = await RpcRetry.ExecuteAsync(
            () =>
            {
                attempts++;
                if (attempts < 3) throw Wrap429();
                return Task.FromResult("ok");
            },
            "429", NullLogger.Instance, CancellationToken.None);
        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Retries5xxThenSucceeds()
    {
        var attempts = 0;
        var result = await RpcRetry.ExecuteAsync(
            () =>
            {
                attempts++;
                if (attempts < 2) throw new HttpRequestException("upstream",
                    inner: null, statusCode: HttpStatusCode.BadGateway);
                return Task.FromResult(7);
            },
            "5xx", NullLogger.Instance, CancellationToken.None);
        Assert.Equal(7, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RetriesNetworkLevelFailure()
    {
        var attempts = 0;
        var result = await RpcRetry.ExecuteAsync(
            () =>
            {
                attempts++;
                if (attempts == 1) throw new HttpRequestException("conn reset");
                return Task.FromResult("ok");
            },
            "net", NullLogger.Instance, CancellationToken.None);
        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task DoesNotRetry4xxOther()
    {
        var attempts = 0;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RpcRetry.ExecuteAsync<int>(
                () =>
                {
                    attempts++;
                    throw new HttpRequestException("bad request",
                        inner: null, statusCode: HttpStatusCode.BadRequest);
                },
                "400", NullLogger.Instance, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DoesNotRetryArgumentException()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RpcRetry.ExecuteAsync<int>(
                () => { attempts++; throw new ArgumentException("bad input"); },
                "arg", NullLogger.Instance, CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RpcRetry.ExecuteAsync<int>(
                () => Task.FromCanceled<int>(cts.Token),
                "cancel", NullLogger.Instance, cts.Token));
    }

    [Fact]
    public async Task ExhaustsAfterMaxAttempts()
    {
        var attempts = 0;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RpcRetry.ExecuteAsync<int>(
                () => { attempts++; throw Wrap429Bare(); },
                "exhaust", NullLogger.Instance, CancellationToken.None, maxAttempts: 3));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void IsRetryable_ClassifiesCorrectly()
    {
        Assert.True(RpcRetry.IsRetryable(Wrap429()));
        Assert.True(RpcRetry.IsRetryable(Wrap429Bare()));
        Assert.True(RpcRetry.IsRetryable(new HttpRequestException("net")));
        Assert.True(RpcRetry.IsRetryable(new HttpRequestException("502",
            inner: null, statusCode: HttpStatusCode.BadGateway)));
        Assert.False(RpcRetry.IsRetryable(new HttpRequestException("400",
            inner: null, statusCode: HttpStatusCode.BadRequest)));
        Assert.False(RpcRetry.IsRetryable(new ArgumentException("bad")));
        Assert.False(RpcRetry.IsRetryable(new InvalidOperationException("oops")));
    }

    private static RpcClientUnknownException Wrap429() => new(
        "RPC error", new HttpRequestException("Too Many Requests",
            inner: null, statusCode: HttpStatusCode.TooManyRequests));

    private static HttpRequestException Wrap429Bare() => new(
        "Too Many Requests", inner: null, statusCode: HttpStatusCode.TooManyRequests);
}
