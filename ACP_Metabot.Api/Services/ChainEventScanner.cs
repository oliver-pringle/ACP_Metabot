using ACP_Metabot.Api.Models;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace ACP_Metabot.Api.Services;

[Event("JobCreated")]
public class JobCreatedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",      1, true)]  public System.Numerics.BigInteger JobId      { get; set; }
    [Parameter("address", "client",     2, true)]  public string Client    { get; set; } = "";
    [Parameter("address", "provider",   3, true)]  public string Provider  { get; set; } = "";
    [Parameter("address", "evaluator",  4, false)] public string Evaluator { get; set; } = "";
    [Parameter("uint256", "expiredAt",  5, false)] public System.Numerics.BigInteger ExpiredAt  { get; set; }
    [Parameter("address", "hook",       6, false)] public string Hook      { get; set; } = "";
}

[Event("JobFunded")]
public class JobFundedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",  1, true)]  public System.Numerics.BigInteger JobId  { get; set; }
    [Parameter("address", "client", 2, true)]  public string Client { get; set; } = "";
    [Parameter("uint256", "amount", 3, false)] public System.Numerics.BigInteger Amount { get; set; }
}

[Event("JobSubmitted")]
public class JobSubmittedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",       1, true)]  public System.Numerics.BigInteger JobId      { get; set; }
    [Parameter("address", "provider",    2, true)]  public string Provider  { get; set; } = "";
    [Parameter("bytes32", "deliverable", 3, false)] public byte[] Deliverable { get; set; } = Array.Empty<byte>();
}

[Event("JobCompleted")]
public class JobCompletedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",  1, true)]  public System.Numerics.BigInteger JobId  { get; set; }
    [Parameter("bytes32", "reason", 2, false)] public byte[] Reason { get; set; } = Array.Empty<byte>();
}

[Event("JobRejected")]
public class JobRejectedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",    1, true)]  public System.Numerics.BigInteger JobId    { get; set; }
    [Parameter("address", "rejector", 2, true)]  public string Rejector { get; set; } = "";
    [Parameter("bytes32", "reason",   3, false)] public byte[] Reason   { get; set; } = Array.Empty<byte>();
}

[Event("JobExpired")]
public class JobExpiredEvent : IEventDTO
{
    [Parameter("uint256", "jobId", 1, true)] public System.Numerics.BigInteger JobId { get; set; }
}

public class ChainEventScanner
{
    private readonly Web3 _web3;
    private readonly string _contractAddress;
    private readonly long _deployBlock;
    private readonly long _chunkSize;
    private readonly int _maxFirstScanDays;
    private readonly ILogger<ChainEventScanner> _logger;
    // Block-timestamp LRU; bounded so it can't grow without limit during a long warmer pass.
    private readonly Dictionary<long, DateTime> _blockTimestamps = new(capacity: 4096);

    private const long BaseBlockTimeSeconds = 2;

    public ChainEventScanner(IConfiguration config, ILogger<ChainEventScanner> logger)
    {
        _logger = logger;
        var rpcUrl = config["BASE_RPC_URL"]
            ?? throw new InvalidOperationException("BASE_RPC_URL not configured");
        _contractAddress = config["ACP_CONTRACT_ADDRESS_BASE"]
            ?? throw new InvalidOperationException("ACP_CONTRACT_ADDRESS_BASE not configured");
        _deployBlock      = config.GetValue<long?>("Reputation:ContractDeployBlock") ?? 0L;
        _chunkSize        = config.GetValue<long?>("Reputation:ChunkSize")           ?? 10_000L;
        _maxFirstScanDays = config.GetValue<int?>("Reputation:MaxFirstScanDays")     ?? 90;

        // Nethereum's RpcClient defaults to a 20s ConnectionTimeout — applied
        // app-wide via the static ClientBase.ConnectionTimeout. The free
        // publicnode Base RPC blew through that on the V2 seller enumeration
        // (unfiltered eth_getLogs across 10K blocks). Bump the static default
        // here on construction so every RpcClient instance — incl. the Web3
        // built from the URL string below — gets the longer timeout. Per-agent
        // reputation scans are fine on either window.
        var rpcTimeout = TimeSpan.FromSeconds(
            Math.Max(5, config.GetValue<int?>("Reputation:RpcTimeoutSeconds") ?? 60));
        Nethereum.JsonRpc.Client.ClientBase.ConnectionTimeout = rpcTimeout;
        _web3 = new Web3(rpcUrl);
    }

    /// <summary>
    /// Returns the current Base mainnet head block. Exposed so the V2 seller
    /// scanner can decide its scan window without holding a duplicate Web3
    /// instance.
    /// </summary>
    public async Task<long> GetHeadBlockAsync()
    {
        return (long)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
    }

    /// <summary>The contract deploy block from configuration. Used as the
    /// cold-start floor for V2 seller enumeration.</summary>
    public long DeployBlock => _deployBlock;

    /// <summary>One chunk of a global provider scan.</summary>
    public record ProviderChunk(long FromBlock, long ToBlock,
        IReadOnlyList<(string Provider, long BlockNumber)> Observations);

    /// <summary>
    /// Streaming variant of the V2 seller enumeration. Iterates the configured
    /// block range one chunk at a time, yielding the distinct providers found
    /// in each chunk along with the chunk's [from..to] bounds. The caller is
    /// expected to persist each chunk's observations and advance its checkpoint
    /// to <c>ToBlock</c> before consuming the next iteration — a transient RPC
    /// failure mid-range then strands at most one chunk's worth of work, not
    /// the whole scan.
    ///
    /// Used by <c>V2SellerScannerService</c> to populate the
    /// <c>v2_known_sellers</c> table that drives Source A of
    /// <see cref="MarketplaceSource.AcpV2MarketplaceSource"/>'s wallet set.
    /// </summary>
    public async IAsyncEnumerable<ProviderChunk> ScanProvidersStreamingAsync(
        long fromBlock, long toBlock,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        long? chunkSize = null)
    {
        var startBlock = Math.Max(fromBlock, _deployBlock);
        if (startBlock > toBlock) yield break;

        var effectiveChunk = chunkSize.HasValue
            ? Math.Max(100L, chunkSize.Value)
            : _chunkSize;

        var handler = _web3.Eth.GetEvent<JobCreatedEvent>(_contractAddress);
        foreach (var (s, e) in BlockRangeChunker.Chunk(startBlock, toBlock, effectiveChunk))
        {
            ct.ThrowIfCancellationRequested();
            var fromBP = new BlockParameter(new HexBigInteger(s));
            var toBP   = new BlockParameter(new HexBigInteger(e));
            // No topic filter on provider — this is the enumerative pass.
            var filter = handler.CreateFilterInput(fromBP, toBP);
            var logs   = await handler.GetAllChangesAsync(filter);

            var firstSeen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var log in logs)
            {
                var addr = (log.Event.Provider ?? "").Trim().ToLowerInvariant();
                if (addr.Length != 42 || !addr.StartsWith("0x")) continue;
                var blk = (long)log.Log.BlockNumber.Value;
                if (!firstSeen.TryGetValue(addr, out var existing) || blk < existing)
                    firstSeen[addr] = blk;
            }

            var observations = new List<(string, long)>(firstSeen.Count);
            foreach (var (addr, blk) in firstSeen) observations.Add((addr, blk));
            yield return new ProviderChunk(s, e, observations);
        }
    }

    /// <summary>
    /// Eager variant of <see cref="ScanProvidersStreamingAsync"/> — accumulates
    /// every chunk's distinct providers into a single deduped list. Useful for
    /// tests and ad-hoc backfills; production scanners should prefer the
    /// streaming variant so a partial failure still saves prior progress.
    /// </summary>
    public async Task<IReadOnlyList<(string Provider, long BlockNumber)>> ScanProvidersAsync(
        long fromBlock, long toBlock, CancellationToken ct)
    {
        var firstSeen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await foreach (var chunk in ScanProvidersStreamingAsync(fromBlock, toBlock, ct))
        {
            foreach (var (addr, blk) in chunk.Observations)
            {
                if (!firstSeen.TryGetValue(addr, out var existing) || blk < existing)
                    firstSeen[addr] = blk;
            }
        }

        var result = new List<(string, long)>(firstSeen.Count);
        foreach (var (addr, blk) in firstSeen) result.Add((addr, blk));
        return result;
    }

    public async Task<ChainScanResult> ScanAgentAsync(
        string agentAddress, long fromBlock, DateTime nowUtc, CancellationToken ct)
    {
        var headBlock = (long)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;

        // Cold-start cap: if the caller is asking us to scan from at-or-before
        // the deploy block (first ever scan for this agent), restrict the
        // initial scan to the last MaxFirstScanDays. Subsequent calls pass
        // LastScannedBlock+1 which is well past the deploy block, so the cap
        // is a no-op — delta scans always cover the whole gap since the last run.
        long startBlock = Math.Max(fromBlock, _deployBlock);
        if (fromBlock <= _deployBlock && _maxFirstScanDays > 0)
        {
            long capBlocks = (long)_maxFirstScanDays * 86_400L / BaseBlockTimeSeconds;
            long capStart  = headBlock - capBlocks;
            if (capStart > startBlock) startBlock = capStart;
        }
        if (startBlock > headBlock)
        {
            // Edge: cap pushed past head (config sanity issue). Return zeros.
            return new ChainScanResult(
                AgentAddress:               agentAddress,
                TotalJobs:                  0, Completed: 0, Rejected: 0, Expired: 0,
                CompletedLast30d:           0,
                LastJobSubmittedAt:         null,
                AvgResponseSeconds30d:      null,
                ResponseTimeSampleCount30d: 0,
                HighestScannedBlock:        headBlock);
        }

        // 1. JobCreated filtered on provider (topic3) — gives the agent's full jobId set.
        //    topic1 (jobId) = any, topic2 (client) = any, topic3 (provider) = agentAddress
        var createdHandler = _web3.Eth.GetEvent<JobCreatedEvent>(_contractAddress);
        var createdLogs = await RunChunkedAsync(createdHandler,
            (fromBP, toBP) => createdHandler.CreateFilterInput(
                (object[]?)null, (object[]?)null, new object[] { agentAddress }, fromBP, toBP),
            startBlock, headBlock, ct);

        var jobIds = new HashSet<System.Numerics.BigInteger>();
        var fundedTimestamps    = new Dictionary<System.Numerics.BigInteger, DateTime>();
        var submittedTimestamps = new Dictionary<System.Numerics.BigInteger, DateTime>();
        DateTime? lastSubmitted = null;
        foreach (var log in createdLogs) jobIds.Add(log.Event.JobId);

        // 2. JobSubmitted filtered on provider (topic2) — gives T1 for response time.
        //    topic1 (jobId) = any, topic2 (provider) = agentAddress
        var submittedHandler = _web3.Eth.GetEvent<JobSubmittedEvent>(_contractAddress);
        var submittedLogs = await RunChunkedAsync(submittedHandler,
            (fromBP, toBP) => submittedHandler.CreateFilterInput(
                (object[]?)null, new object[] { agentAddress }, fromBP, toBP),
            startBlock, headBlock, ct);
        foreach (var log in submittedLogs)
        {
            if (!jobIds.Contains(log.Event.JobId)) continue;
            var ts = await GetBlockTimeAsync((long)log.Log.BlockNumber.Value, ct);
            submittedTimestamps[log.Event.JobId] = ts;
            if (lastSubmitted is null || ts > lastSubmitted) lastSubmitted = ts;
        }

        // 3. Other events are NOT indexed on provider, so we filter by jobId set
        //    in batches (max 50 jobIds per topic filter — well under RPC limits).
        var fundedHandler    = _web3.Eth.GetEvent<JobFundedEvent>(_contractAddress);
        var completedHandler = _web3.Eth.GetEvent<JobCompletedEvent>(_contractAddress);
        var rejectedHandler  = _web3.Eth.GetEvent<JobRejectedEvent>(_contractAddress);
        var expiredHandler   = _web3.Eth.GetEvent<JobExpiredEvent>(_contractAddress);

        long completed = 0, rejected = 0, expired = 0, completedLast30d = 0;
        var thirtyDaysAgo = nowUtc.AddDays(-30);

        const int batchSize = 50;
        var allJobIds = jobIds.ToList();
        for (int i = 0; i < allJobIds.Count; i += batchSize)
        {
            var batch       = allJobIds.GetRange(i, Math.Min(batchSize, allJobIds.Count - i));
            var topicJobIds = batch.Select(id => (object)id).ToArray();

            // JobFunded: topic1 = jobId (indexed), topic2 = client (indexed) — filter only by jobId
            var fundedBatch = await RunChunkedAsync(fundedHandler,
                (fromBP, toBP) => fundedHandler.CreateFilterInput(
                    topicJobIds, (object[]?)null, fromBP, toBP),
                startBlock, headBlock, ct);
            foreach (var log in fundedBatch)
            {
                fundedTimestamps[log.Event.JobId] =
                    await GetBlockTimeAsync((long)log.Log.BlockNumber.Value, ct);
            }

            // JobCompleted: topic1 = jobId (indexed only)
            var completedBatch = await RunChunkedAsync(completedHandler,
                (fromBP, toBP) => completedHandler.CreateFilterInput(
                    topicJobIds, fromBP, toBP),
                startBlock, headBlock, ct);
            foreach (var log in completedBatch)
            {
                completed++;
                var ts = await GetBlockTimeAsync((long)log.Log.BlockNumber.Value, ct);
                if (ts >= thirtyDaysAgo) completedLast30d++;
            }

            // JobRejected: topic1 = jobId (indexed), topic2 = rejector (indexed)
            var rejectedBatch = await RunChunkedAsync(rejectedHandler,
                (fromBP, toBP) => rejectedHandler.CreateFilterInput(
                    topicJobIds, (object[]?)null, fromBP, toBP),
                startBlock, headBlock, ct);
            foreach (var log in rejectedBatch)
            {
                // Exclude self-rejections (agent rejecting buyer's spec).
                if (string.Equals(log.Event.Rejector, agentAddress, StringComparison.OrdinalIgnoreCase))
                    continue;
                rejected++;
            }

            // JobExpired: topic1 = jobId (indexed only)
            var expiredBatch = await RunChunkedAsync(expiredHandler,
                (fromBP, toBP) => expiredHandler.CreateFilterInput(
                    topicJobIds, fromBP, toBP),
                startBlock, headBlock, ct);
            expired += expiredBatch.Count;
        }

        // 4. Average response time over completed jobs in last 30d only.
        double? avgResponseSeconds = null;
        long sampleCount           = 0;
        var responseDurations      = new List<double>();
        foreach (var (jobId, submitTs) in submittedTimestamps)
        {
            if (submitTs < thirtyDaysAgo) continue;
            if (!fundedTimestamps.TryGetValue(jobId, out var fundTs)) continue;
            var seconds = (submitTs - fundTs).TotalSeconds;
            if (seconds <= 0) continue;
            responseDurations.Add(seconds);
            sampleCount++;
        }
        if (responseDurations.Count > 0) avgResponseSeconds = responseDurations.Average();

        return new ChainScanResult(
            AgentAddress:               agentAddress,
            TotalJobs:                  jobIds.Count,
            Completed:                  completed,
            Rejected:                   rejected,
            Expired:                    expired,
            CompletedLast30d:           completedLast30d,
            LastJobSubmittedAt:         lastSubmitted,
            AvgResponseSeconds30d:      avgResponseSeconds,
            ResponseTimeSampleCount30d: sampleCount,
            HighestScannedBlock:        headBlock);
    }

    private async Task<List<EventLog<TEvt>>> RunChunkedAsync<TEvt>(
        Event<TEvt> handler,
        Func<BlockParameter, BlockParameter, NewFilterInput> filterBuilder,
        long fromBlock, long toBlock, CancellationToken ct) where TEvt : IEventDTO, new()
    {
        var all = new List<EventLog<TEvt>>();
        foreach (var (s, e) in BlockRangeChunker.Chunk(fromBlock, toBlock, _chunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var fromBP = new BlockParameter(new HexBigInteger(s));
            var toBP   = new BlockParameter(new HexBigInteger(e));
            var filter = filterBuilder(fromBP, toBP);
            var logs   = await handler.GetAllChangesAsync(filter);
            all.AddRange(logs);
        }
        return all;
    }

    // Lock guarding the non-concurrent _blockTimestamps. Held only around
    // dictionary access — never across the await for the RPC fetch.
    private readonly object _blockTimestampsLock = new();

    private async Task<DateTime> GetBlockTimeAsync(long blockNumber, CancellationToken ct)
    {
        lock (_blockTimestampsLock)
        {
            if (_blockTimestamps.TryGetValue(blockNumber, out var cached)) return cached;
        }
        var block = await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(
            new HexBigInteger(blockNumber));
        var ts = DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value).UtcDateTime;
        lock (_blockTimestampsLock)
        {
            // Re-check in case another thread populated the same block while we waited.
            if (_blockTimestamps.TryGetValue(blockNumber, out var nowCached)) return nowCached;
            // Cap cache size; evict oldest-by-insertion when full.
            if (_blockTimestamps.Count >= 4096)
            {
                var firstKey = _blockTimestamps.Keys.First();
                _blockTimestamps.Remove(firstKey);
            }
            _blockTimestamps[blockNumber] = ts;
            return ts;
        }
    }
}
