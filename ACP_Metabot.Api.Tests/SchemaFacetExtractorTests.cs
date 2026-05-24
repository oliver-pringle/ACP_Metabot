using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class SchemaFacetExtractorTests
{
    [Fact]
    public void Extract_object_form_returns_lowercase_top_level_keys()
    {
        var schema = """
        {"type":"object","properties":{"tokenSymbol":{"type":"string"},"ChainId":{"type":"integer"}}}
        """;
        var names = SchemaFacetExtractor.Extract(schema);
        Assert.Equal(new[] { "tokensymbol", "chainid" }, names);
    }

    [Fact]
    public void Extract_string_encoded_form_unwraps_and_returns_keys()
    {
        // Outer JSON is itself a STRING whose contents are JSON — the legacy
        // V1 shape that some V2 registrations still emit. Note the outer quoting.
        var schema = "\"{\\\"properties\\\":{\\\"walletAddress\\\":{}}}\"";
        var names = SchemaFacetExtractor.Extract(schema);
        Assert.Equal(new[] { "walletaddress" }, names);
    }

    [Fact]
    public void Extract_malformed_or_missing_properties_returns_empty()
    {
        Assert.Empty(SchemaFacetExtractor.Extract(null));
        Assert.Empty(SchemaFacetExtractor.Extract(""));
        Assert.Empty(SchemaFacetExtractor.Extract("not json at all"));
        Assert.Empty(SchemaFacetExtractor.Extract("[\"array\",\"not\",\"object\"]"));
        Assert.Empty(SchemaFacetExtractor.Extract("{\"type\":\"object\"}"));  // no .properties
    }
}
