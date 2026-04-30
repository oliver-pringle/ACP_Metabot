using System.Text.Json;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services.MarketplaceSource;

public class JsonFileMarketplaceSource : IMarketplaceSource
{
    public string MarketplaceVersion => "v1";

    private readonly string _path;
    private readonly ILogger<JsonFileMarketplaceSource> _logger;

    public JsonFileMarketplaceSource(IConfiguration config, ILogger<JsonFileMarketplaceSource> logger)
    {
        _path = config["Indexer:SourcePath"]
            ?? throw new InvalidOperationException("Indexer:SourcePath not configured");
        _logger = logger;
    }

    public async Task<IReadOnlyList<MarketplaceOfferingDto>> FetchAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            _logger.LogWarning("[indexer] source file not found at {Path} — returning empty set", _path);
            return Array.Empty<MarketplaceOfferingDto>();
        }
        await using var stream = File.OpenRead(_path);
        var list = await JsonSerializer.DeserializeAsync<List<MarketplaceOfferingDto>>(
            stream, cancellationToken: ct);
        return list ?? new List<MarketplaceOfferingDto>();
    }
}

