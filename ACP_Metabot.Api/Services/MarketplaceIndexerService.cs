using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services.MarketplaceSource;

namespace ACP_Metabot.Api.Services;

public class MarketplaceIndexerService : BackgroundService
{
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
        _embeddingBatchSize = config.GetValue<int?>("Indexer:EmbeddingConcurrency") ?? 4;
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

        var fetched = await source.FetchAsync(ct);
        var nowUtc = DateTime.UtcNow;

        int added = 0, updated = 0, unchanged = 0;
        foreach (var dto in fetched)
        {
            var schemaJson = dto.RequirementSchema is null
                ? null
                : JsonSerializer.Serialize(dto.RequirementSchema);
            var hash = ContentHash(dto.AgentAddress, dto.OfferingName, dto.Description,
                dto.PriceUsdc, dto.PriceType, dto.Chain, schemaJson);
            var result = await repo.UpsertAsync(
                dto.AgentAddress, dto.AgentName, dto.OfferingName, dto.Description,
                schemaJson, dto.PriceUsdc, dto.PriceType, dto.IsPrivate, dto.Chain,
                hash, nowUtc);
            if (result.IsNew) added++;
            else if (result.ContentChanged) updated++;
            else unchanged++;
        }

        LastFetchAt = nowUtc;
        LastFetchCount = fetched.Count;
        _logger.LogInformation("[indexer] fetch complete: total={Total} added={Added} updated={Updated} unchanged={Unchanged}",
            fetched.Count, added, updated, unchanged);

        // Embed any rows missing an embedding for the current model
        await EmbedPendingAsync(repo, embedder, ct);
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
