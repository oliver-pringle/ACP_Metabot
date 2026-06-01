using ACP_Metabot.Api.Services;
using Xunit;

namespace ACP_Metabot.Api.Tests;

public class RpcSafeTests
{
    [Fact]
    public void Https_IsReturnedUnchanged()
    {
        const string url = "https://base-mainnet.example.com/v2/key";
        Assert.Equal(url, RpcSafe.RequireHttps(url, "BASE_RPC_URL"));
    }

    [Fact]
    public void Http_NonLoopback_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RpcSafe.RequireHttps("http://base-mainnet.example.com/rpc", "BASE_RPC_URL"));
    }

    [Fact]
    public void Http_Loopback_IsAllowed()
    {
        const string url = "http://localhost:8545";
        Assert.Equal(url, RpcSafe.RequireHttps(url, "BASE_RPC_URL"));
    }

    [Fact]
    public void Garbage_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RpcSafe.RequireHttps("not-a-url", "BASE_RPC_URL"));
    }
}
