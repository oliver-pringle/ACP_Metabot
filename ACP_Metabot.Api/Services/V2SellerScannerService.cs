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
/// Cold-start safety: scans at most <c>Indexer:V2:MaxBlocksPerTick</c>
/// (default 100K) per pass and persists observations + advances the
/// checkpoint AFTER EACH CHUNK via the streaming scanner. A transient RPC
/// failure mid-scan therefore strands at most one chunk's worth of work, not
/// the whole cold-start range. Cadence adapts: when the checkpoint is more
/// than one tick-worth of blocks behind head, the loop sleeps the
/// <c>CatchUpIntervalMinutes</c> (default 5min); once caught up to within
/// one window of head, it falls back to <c>SteadyStateIntervalMinutes</c>
/// (default 60min).
/// </summary>
public class V2SellerScannerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<V2SellerScannerService> _logger;
    private readonly TimeSpan _steadyInterval;
    private readonly TimeSpan _catchUpInterval;
    private readonly long? _coldStartFromBlock;
    private readonly long _maxBlocksPerTick;
    // Smaller than the per-agent 10K chunk because the seller enumeration drops
    // the topic-3 (provider) filter, so each chunk pulls every JobCreated in
    // the range. Free public RPCs (publicnode Base) have routinely exceeded
    // the 20s default RPC timeout on 10K-block unfiltered queries.
    private readonly long _chunkSize;

    public DateTime? LastScanAt { get; private set; }
    public int LastScanNewSellers { get; private set; }
    public long LastScannedBlock { get; private set; }

    public V2SellerScannerService(IServiceProvider services, IConfiguration config,
        ILogger<V2SellerScannerService> logger)
    {
        _services = services;
        _logger = logger;
        var steady = config.GetValue<int?>("Indexer:V2:SellerScanIntervalMinutes") ?? 60;
        _steadyInterval = TimeSpan.FromMinutes(Math.Max(5, steady));
        var catchUp = config.GetValue<int?>("Indexer:V2:SellerScanCatchUpIntervalMinutes") ?? 5;
        _catchUpInterval = TimeSpan.FromMinutes(Math.Max(1, catchUp));
        _coldStartFromBlock = config.GetValue<long?>("Indexer:V2:SellerScanFromBlock");
        _maxBlocksPerTick = Math.Max(2_000L,
            config.GetValue<long?>("Indexer:V2:MaxBlocksPerTick") ?? 100_000L);
        _chunkSize = Math.Max(100L,
            config.GetValue<long?>("Indexer:V2:SellerScanChunkSize") ?? 2_000L);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[v2-seller-scan] starting, steady={Steady}min catchUp={CatchUp}min maxBlocksPerTick={Max} chunkSize={Chunk}",
            _steadyInterval.TotalMinutes, _catchUpInterval.TotalMinutes, _maxBlocksPerTick, _chunkSize);

        // Brief startup delay so the indexer's first cycle (which already runs
        // Source C immediately) doesn't compete with the chain RPC.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            bool behind = false;
            try
            {
                behind = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[v2-seller-scan] tick failed — retrying after interval");
            }

            var sleep = behind ? _catchUpInterval : _steadyInterval;
            try { await Task.Delay(sleep, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("[v2-seller-scan] stopped");
    }

    /// <summary>
    /// Runs one scan window. Returns true if the checkpoint is still behind
    /// head after this pass (so the loop should retry on the catch-up cadence
    /// rather than the steady-state cadence).
    /// </summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<ChainEventScanner>();
        var sellers = scope.ServiceProvider.GetRequiredService<V2KnownSellersRepository>();

        var checkpoint = await sellers.GetLastScannedBlockAsync();
        var headBlock = await scanner.GetHeadBlockAsync();

        long fromBlock = checkpoint > 0
            ? checkpoint + 1
            : (_coldStartFromBlock ?? scanner.DeployBlock);

        if (fromBlock > headBlock)
        {
            _logger.LogDebug(
                "[v2-seller-scan] checkpoint up-to-date (from={From}, head={Head})",
                fromBlock, headBlock);
            LastScanAt = DateTime.UtcNow;
            LastScannedBlock = headBlock;
            return false;
        }

        // Bound the scan window per tick so a fresh deploy doesn't try to
        // process millions of blocks in a single chunked call (which on the
        // first cold-start was crashing inside RunChunkedAsync — likely a
        // transient publicnode RPC error — and orphaning the full window).
        long endBlock = Math.Min(fromBlock + _maxBlocksPerTick - 1, headBlock);

        _logger.LogInformation(
            "[v2-seller-scan] scanning [{From}..{To}] ({Window} blocks; head={Head})",
            fromBlock, endBlock, endBlock - fromBlock + 1, headBlock);

        int totalNewSellers = 0;
        long lastSavedBlock = checkpoint;
        Exception? streamingFailure = null;
        try
        {
            await foreach (var chunk in scanner.ScanProvidersStreamingAsync(
                fromBlock, endBlock, ct, _chunkSize))
            {
                if (chunk.Observations.Count > 0)
                {
                    await sellers.UpsertManyAsync(chunk.Observations, DateTime.UtcNow);
                    totalNewSellers += chunk.Observations.Count;
                }
                // Advance the checkpoint to this chunk's upper bound only AFTER
                // the upsert succeeds. Cold-start partial progress survives a
                // mid-window RPC failure: the next tick resumes from the last
                // saved chunk, not the start of the original window.
                await sellers.SetLastScannedBlockAsync(chunk.ToBlock, DateTime.UtcNow);
                lastSavedBlock = chunk.ToBlock;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            streamingFailure = ex;
            _logger.LogWarning(ex,
                "[v2-seller-scan] partial failure mid-window; checkpoint left at {LastSaved} (head={Head})",
                lastSavedBlock, headBlock);
        }

        LastScanAt = DateTime.UtcNow;
        LastScanNewSellers = totalNewSellers;
        LastScannedBlock = lastSavedBlock;

        if (streamingFailure is null)
        {
            _logger.LogInformation(
                "[v2-seller-scan] window complete: {Distinct} provider obs; checkpoint={End} head={Head} behind={Behind}",
                totalNewSellers, lastSavedBlock, headBlock, headBlock - lastSavedBlock);
        }

        // Behind by more than one full tick window → use the catch-up cadence.
        return (headBlock - lastSavedBlock) > _maxBlocksPerTick;
    }
}
