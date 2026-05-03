using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// Periodic background scan of <c>JobCreated</c> events on the V2 ACP
/// contract to enumerate every V2 seller wallet, regardless of whether the
/// keyword sweep happens to surface them. Result is upserted into
/// <c>v2_known_sellers</c> and consumed as Source A of
/// <see cref="MarketplaceSource.AcpV2MarketplaceSource"/>'s wallet set on the
/// next indexer cycle.
///
/// Cold-start: scans from <c>Indexer:V2:SellerScanFromBlock</c> if set,
/// otherwise from <c>Reputation:ContractDeployBlock</c>. After each successful
/// pass the head block is persisted as the checkpoint, so subsequent runs
/// only scan the delta since the previous tick.
/// </summary>
public class V2SellerScannerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<V2SellerScannerService> _logger;
    private readonly TimeSpan _interval;
    private readonly long? _coldStartFromBlock;

    public DateTime? LastScanAt { get; private set; }
    public int LastScanNewSellers { get; private set; }
    public long LastScannedBlock { get; private set; }

    public V2SellerScannerService(IServiceProvider services, IConfiguration config,
        ILogger<V2SellerScannerService> logger)
    {
        _services = services;
        _logger = logger;
        var minutes = config.GetValue<int?>("Indexer:V2:SellerScanIntervalMinutes") ?? 60;
        _interval = TimeSpan.FromMinutes(Math.Max(5, minutes));
        _coldStartFromBlock = config.GetValue<long?>("Indexer:V2:SellerScanFromBlock");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[v2-seller-scan] starting, interval={Interval}min", _interval.TotalMinutes);

        // First tick on a 30s delay so the indexer's first cycle (which
        // already runs Source C immediately) doesn't compete with the chain
        // RPC for startup window.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

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
                _logger.LogError(ex, "[v2-seller-scan] tick failed — retrying after interval");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("[v2-seller-scan] stopped");
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<ChainEventScanner>();
        var sellers = scope.ServiceProvider.GetRequiredService<V2KnownSellersRepository>();

        var checkpoint = await sellers.GetLastScannedBlockAsync();
        var headBlock = await scanner.GetHeadBlockAsync();

        long fromBlock;
        if (checkpoint > 0)
        {
            fromBlock = checkpoint + 1;
        }
        else
        {
            fromBlock = _coldStartFromBlock ?? scanner.DeployBlock;
        }

        if (fromBlock > headBlock)
        {
            _logger.LogDebug("[v2-seller-scan] checkpoint up-to-date (from={From}, head={Head})",
                fromBlock, headBlock);
            LastScanAt = DateTime.UtcNow;
            LastScannedBlock = headBlock;
            return;
        }

        _logger.LogInformation(
            "[v2-seller-scan] scanning [{From}..{To}] ({Count} blocks)",
            fromBlock, headBlock, headBlock - fromBlock + 1);

        var observations = await scanner.ScanProvidersAsync(fromBlock, headBlock, ct);
        var nowUtc = DateTime.UtcNow;
        if (observations.Count > 0)
        {
            await sellers.UpsertManyAsync(observations, nowUtc);
        }
        await sellers.SetLastScannedBlockAsync(headBlock, nowUtc);

        LastScanAt = nowUtc;
        LastScanNewSellers = observations.Count;
        LastScannedBlock = headBlock;

        _logger.LogInformation(
            "[v2-seller-scan] complete: {Distinct} distinct providers in window; checkpoint={Head}",
            observations.Count, headBlock);
    }
}
