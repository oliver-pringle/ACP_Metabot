using ACP_Metabot.Api.Data;
using Xunit;

namespace ACP_Metabot.Api.Tests;

// The C# classifier and the SQL CTE in RequestMetricsRepository
// implement the same family rules. These tests pin the C# side so any
// rule change is loud — keep the SQL CTE updated alongside.
public class UserAgentClassifierTests
{
    [Fact]
    public void NullOrEmpty_IsUnknown()
    {
        Assert.Equal(("unknown", (string?)null), UserAgentClassifier.Classify(null));
        Assert.Equal(("unknown", (string?)null), UserAgentClassifier.Classify(""));
    }

    [Theory]
    [InlineData("acp-find-plugin/0.5.0",  "0.5.0")]
    [InlineData("acp-find-plugin/0.4.0",  "0.4.0")]
    [InlineData("acp-find-plugin/0.0",    "0.0")]
    [InlineData("acp-find-plugin/1.2.3.4","1.2.3.4")]
    [InlineData("acp-find-plugin/0.0-smoke", "0.0")]
    public void AcpPlugin_ExtractsVersion(string ua, string expectedVersion)
    {
        var (family, version) = UserAgentClassifier.Classify(ua);
        Assert.Equal("acp-find-plugin", family);
        Assert.Equal(expectedVersion, version);
    }

    [Theory]
    [InlineData("curl/8.5.0")]
    [InlineData("curl/7.81.0")]
    [InlineData("Curl/8.18.0")] // case-insensitive
    public void Curl_IsCurlFamily_WithNullVersion(string ua)
    {
        var (family, version) = UserAgentClassifier.Classify(ua);
        Assert.Equal("curl", family);
        Assert.Null(version);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/134.0.0.0")]
    [InlineData("Mozilla/5.0 (compatible; CensysInspect/1.1)")]
    [InlineData("mozilla/5.0 (X11; Linux)")] // case-insensitive
    public void Mozilla_IsBrowserFamily(string ua)
    {
        var (family, version) = UserAgentClassifier.Classify(ua);
        Assert.Equal("browser", family);
        Assert.Null(version);
    }

    [Theory]
    [InlineData("TLM-Audit-Scanner/1.0")]
    [InlineData("Go-http-client/2.0")]
    [InlineData("Shields.io/2c25b78")]
    [InlineData("Claude-User (claude-code/2.1.123; +https://support.anthropic.com/)")]
    public void Other_FallsThrough(string ua)
    {
        var (family, version) = UserAgentClassifier.Classify(ua);
        Assert.Equal("other", family);
        Assert.Null(version);
    }
}
