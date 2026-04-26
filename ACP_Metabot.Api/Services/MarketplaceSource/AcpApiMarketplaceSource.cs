using System.Text.Json.Serialization;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services.MarketplaceSource;

public class AcpApiMarketplaceSource : IMarketplaceSource
{
    private readonly HttpClient _http;
    private readonly ILogger<AcpApiMarketplaceSource> _logger;
    private readonly int _pageSize;
    private readonly int _maxPages;
    private readonly int _requestDelayMs;
    private readonly string _sortBy;
    private readonly string _sortOrder;

    public AcpApiMarketplaceSource(IConfiguration config, IHttpClientFactory httpFactory,
        ILogger<AcpApiMarketplaceSource> logger)
    {
        _logger = logger;
        var baseUrl = config["Indexer:ApiBaseUrl"] ?? "https://acpx.virtuals.io/";
        if (!baseUrl.EndsWith("/")) baseUrl += "/";
        _pageSize = Math.Clamp(config.GetValue<int?>("Indexer:ApiPageSize") ?? 100, 1, 100);
        _maxPages = config.GetValue<int?>("Indexer:ApiMaxPages") ?? 0; // 0 = no cap
        _requestDelayMs = Math.Max(0, config.GetValue<int?>("Indexer:ApiRequestDelayMs") ?? 50);
        _sortBy = config["Indexer:ApiSortBy"] ?? "usageCount";
        _sortOrder = config["Indexer:ApiSortOrder"] ?? "desc";

        _http = httpFactory.CreateClient(nameof(AcpApiMarketplaceSource));
        _http.BaseAddress ??= new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(30);
        // The upstream API checks Origin/Referer (CORS-style auth) — no bearer token.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ACP_Metabot/1.0 (+https://app.virtuals.io)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.Add("Origin", "https://app.virtuals.io");
        _http.DefaultRequestHeaders.Add("Referer", "https://app.virtuals.io/");
    }

    public async Task<IReadOnlyList<MarketplaceOfferingDto>> FetchAsync(CancellationToken ct)
    {
        var collected = new List<MarketplaceOfferingDto>(capacity: 1024);
        var seenKeys = new HashSet<string>();
        int page = 1;
        int? pageCount = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_maxPages > 0 && page > _maxPages)
            {
                _logger.LogInformation("[indexer] api source: stopping at maxPages={Max}", _maxPages);
                break;
            }

            var path = $"api/metrics/skills?page={page}&pageSize={_pageSize}&sortBy={_sortBy}&sortOrder={_sortOrder}";
            SkillsResponse? body;
            try
            {
                body = await _http.GetFromJsonAsync<SkillsResponse>(path, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[indexer] api source: fetch failed at page={Page} — aborting", page);
                throw;
            }

            if (body is null || body.Data.Count == 0)
            {
                if (page == 1)
                    _logger.LogWarning("[indexer] api source: page 1 was empty — upstream may be down");
                break;
            }

            pageCount ??= body.Pagination?.PageCount;

            int addedThisPage = 0;
            foreach (var row in body.Data)
            {
                var dto = MapToDto(row);
                if (dto is null) continue;
                var key = dto.AgentAddress + "|" + dto.OfferingName;
                if (!seenKeys.Add(key)) continue; // dedupe across pages just in case
                collected.Add(dto);
                addedThisPage++;
            }

            if (page == 1)
            {
                _logger.LogInformation("[indexer] api source: total reported={Total} pageCount={PageCount} pageSize={PageSize}",
                    body.Pagination?.Total, pageCount, _pageSize);
            }

            if (pageCount is not null && page >= pageCount.Value) break;
            if (body.Data.Count < _pageSize) break; // last page

            page++;
            if (_requestDelayMs > 0) await Task.Delay(_requestDelayMs, ct);
        }

        _logger.LogInformation("[indexer] api source: fetched {Count} offerings across {Pages} page(s)",
            collected.Count, page);
        return collected;
    }

    private static MarketplaceOfferingDto? MapToDto(SkillRow row)
    {
        var name = row.Skill?.Name?.Trim();
        var addr = row.Agent?.WalletAddress?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(addr)) return null;

        // priceV2 is current; fall back to legacy price field if absent.
        var priceType = row.Skill?.PriceV2?.Type ?? "fixed";
        var priceValue = row.Skill?.PriceV2?.Value ?? row.Skill?.Price ?? 0.0;

        // Normalize price type to the strings the rest of the system uses.
        priceType = priceType.ToLowerInvariant() switch
        {
            "fixed" => "fixed",
            "percentage" or "percent" => "percent",
            _ => priceType.ToLowerInvariant()
        };

        return new MarketplaceOfferingDto
        {
            AgentAddress = addr,
            AgentName = row.Agent?.Name ?? "",
            OfferingName = name,
            Description = row.Skill?.Description ?? "",
            RequirementSchema = null, // upstream doesn't expose it on the list endpoint
            PriceUsdc = priceValue,
            PriceType = priceType,
            IsPrivate = false,
            Chain = "base",
            UsageCount = row.Skill?.UsageCount ?? 0,
            AgentJobCount = row.Agent?.JobCount ?? 0
        };
    }

    private class SkillsResponse
    {
        [JsonPropertyName("data")] public List<SkillRow> Data { get; set; } = new();
        [JsonPropertyName("pagination")] public PaginationInfo? Pagination { get; set; }
    }

    private class PaginationInfo
    {
        [JsonPropertyName("page")] public int Page { get; set; }
        [JsonPropertyName("pageSize")] public int PageSize { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("pageCount")] public int PageCount { get; set; }
    }

    private class SkillRow
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("skill")] public SkillInfo? Skill { get; set; }
        [JsonPropertyName("agent")] public AgentInfo? Agent { get; set; }
        [JsonPropertyName("tag")] public string? Tag { get; set; }
    }

    private class SkillInfo
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("price")] public double? Price { get; set; }
        [JsonPropertyName("priceV2")] public PriceV2? PriceV2 { get; set; }
        [JsonPropertyName("usageCount")] public long? UsageCount { get; set; }
    }

    private class PriceV2
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("value")] public double? Value { get; set; }
    }

    private class AgentInfo
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("walletAddress")] public string? WalletAddress { get; set; }
        [JsonPropertyName("jobCount")] public long? JobCount { get; set; }
    }
}
