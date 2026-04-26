using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services.MarketplaceSource;

namespace ACP_Metabot.Api.Services;

public class MarketplaceIndexerService : BackgroundService
{
    // Trust-boundary caps for third-party agent fields.
    private const int MaxFieldLen = 256;
    private const int MaxDescriptionLen = 4096;
    private const int MaxSchemaJsonLen = 16384;

    private readonly IServiceProvider _services;
    private readonly ILogger<MarketplaceIndexerService> _logger;
    private readonly TimeSpan _interval;
    private readonly int _embeddingBatchSize;

    public DateTime? LastFetchAt { get; private set; }
    public int LastFetchCount { get; private set; }

    public MarketplaceIndexerService(IServiceProvider services, IConfiguration config,
        ILogger<MarketplaceIndexerService> logger)
    {
        _services = services;
        _logger = logger;
        var seconds = config.GetValue<int?>("Indexer:IntervalSeconds") ?? 600;
        _interval = TimeSpan.FromSeconds(Math.Max(30, seconds));
        _embeddingBatchSize = config.GetValue<int?>("Indexer:EmbeddingConcurrency") ?? 32;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[indexer] starting, interval={Interval}s", _interval.TotalSeconds);
        // First tick is immediate so the index is warm by first request.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[indexer] fetch tick failed — retrying after interval");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("[indexer] stopped");
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IMarketplaceSource>();
        var repo = scope.ServiceProvider.GetRequiredService<OfferingRepository>();
        var embedder = scope.ServiceProvider.GetRequiredService<VoyageEmbeddingProvider>();
        var reputation = scope.ServiceProvider.GetRequiredService<ReputationService>();
        var search = scope.ServiceProvider.GetRequiredService<SearchService>();

        var fetched = await source.FetchAsync(ct);
        var nowUtc = DateTime.UtcNow;

        // Build the bulk upsert input list. All field truncation, schema
        // serialisation, and hashing happens here so the repo layer just sees
        // a clean list of canonical UpsertItems.
        var items = new List<UpsertItem>(fetched.Count);
        foreach (var dto in fetched)
        {
            // Third-party agents can put anything in these fields. Truncate at
            // the trust boundary so DB rows, embedding inputs, and LLM prompts
            // are all bounded regardless of upstream behavior.
            var agentName = Truncate(dto.AgentName, MaxFieldLen);
            var offeringName = Truncate(dto.OfferingName, MaxFieldLen);
            var description = Truncate(dto.Description, MaxDescriptionLen);

            string? schemaJson = null;
            if (dto.RequirementSchema is not null)
            {
                schemaJson = JsonSerializer.Serialize(dto.RequirementSchema);
                if (schemaJson.Length > MaxSchemaJsonLen)
                    schemaJson = schemaJson.Substring(0, MaxSchemaJsonLen);
            }

            var hash = ContentHash(dto.AgentAddress, offeringName, description,
                dto.PriceUsdc, dto.PriceType, dto.Chain, schemaJson);
            items.Add(new UpsertItem(
                dto.AgentAddress, agentName, offeringName, description,
                schemaJson, dto.PriceUsdc, dto.PriceType, dto.IsPrivate, dto.Chain,
                hash, dto.UsageCount, dto.AgentJobCount));
        }

        var summary = await repo.UpsertManyAsync(items, nowUtc);

        LastFetchAt = nowUtc;
        LastFetchCount = fetched.Count;
        _logger.LogInformation("[indexer] fetch complete: total={Total} added={Added} updated={Updated} unchanged={Unchanged}",
            fetched.Count, summary.Added, summary.Updated, summary.Unchanged);

        // Refresh reputation caches with the freshly-persisted corpus.
        // Pull from DB (not the DTO list) so we have the assigned offering ids
        // and any pre-existing rows we didn't see this fetch.
        if (fetched.Count > 0)
        {
            var corpus = await repo.ListAllAsync();
            reputation.RebuildFromCorpus(corpus);
            _logger.LogInformation("[indexer] reputation cache rebuilt with {N} offerings", corpus.Count);

            // Daily snapshot — once per UTC day. INSERT...SELECT is a no-op
            // if today's snapshot already exists.
            var today = nowUtc.ToString("yyyy-MM-dd");
            var snapped = await repo.WriteSnapshotIfMissingAsync(today);
            if (snapped > 0)
                _logger.LogInformation("[indexer] reputation snapshot written for {Date}: {N} rows", today, snapped);
        }

        // Embed any rows missing an embedding for the current model
        await EmbedPendingAsync(repo, embedder, ct);

        // Rebuild the search corpus cache last so it picks up rows that were
        // freshly embedded in this cycle.
        await search.RefreshCorpusAsync();
    }

    private async Task EmbedPendingAsync(OfferingRepository repo, VoyageEmbeddingProvider embedder, CancellationToken ct)
    {
        var pending = await repo.ListNeedingEmbeddingAsync(limit: 10000, dimension: embedder.Dimension);
        if (pending.Count == 0) return;
        _logger.LogInformation("[indexer] embedding {N} offerings with model={Model}", pending.Count, embedder.ModelId);

        // Batch by _embeddingBatchSize to bound memory + per-call cost.
        for (int i = 0; i < pending.Count; i += _embeddingBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = pending.Skip(i).Take(_embeddingBatchSize).ToArray();
            var texts = slice.Select(o => BuildEmbeddingText(o)).ToArray();
            try
            {
                var vectors = await embedder.EmbedAsync(texts, ct);
                for (int j = 0; j < slice.Length; j++)
                {
                    var vec = vectors[j];
                    var blob = new byte[vec.Length * sizeof(float)];
                    Buffer.BlockCopy(vec, 0, blob, 0, blob.Length);
                    await repo.UpsertEmbeddingAsync(slice[j].Id, embedder.ModelId, vec.Length, blob, DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[indexer] embedding batch failed; will retry next tick");
                break;
            }
        }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        return s.Length <= max ? s : s.Substring(0, max);
    }

    private static string BuildEmbeddingText(Offering o)
    {
        // Compose the searchable text from name + description + price.
        // Keep it short so embedding cost stays low.
        return $"Offering: {o.OfferingName}\nAgent: {o.AgentName}\nPrice: {o.PriceUsdc} USDC\nDescription: {o.Description}";
    }

    private static string ContentHash(string agentAddress, string offeringName,
        string description, double priceUsdc, string priceType, string chain, string? schemaJson)
    {
        var canonical = string.Join("",
            agentAddress, offeringName, description,
            priceUsdc.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            priceType, chain, schemaJson ?? "");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
