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
    // Canonical ABI: JobCompleted(uint256 indexed jobId, address indexed evaluator, bytes32 reason).
    // The indexed `evaluator` is part of the signature — omitting it computes the wrong topic0,
    // so the log filter matches ZERO completed jobs. See ChainEventScannerAbiSignatureTests.
    [Parameter("uint256", "jobId",     1, true)]  public System.Numerics.BigInteger JobId      { get; set; }
    [Parameter("address", "evaluator", 2, true)]  public string Evaluator { get; set; } = "";
    [Parameter("bytes32", "reason",    3, false)] public byte[] Reason { get; set; } = Array.Empty<byte>();
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
    // Optional second Web3 dedicated to unfiltered enumerative scans (V2
    // seller enumeration). Falls back to <c>_web3</c> when no separate URL
    // is configured. publicnode rejects unfiltered eth_getLogs queries with
    // "request timed out", but happily serves the per-agent topic-filtered
    // queries that the reputation path uses — so a deployment can keep the
    // free publicnode for reputation and point this knob at e.g. the official
    // Coinbase RPC (mainnet.base.org) or a paid Alchemy/QuickNode for the
    // enumerative path only.
    private readonly Web3 _enumWeb3;
    private readonly string _contractAddress;
    private readonly long _deployBlock;
    private readonly long _chunkSize;
    private readonly int _maxFirstScanDays;
    // Above this many JobCreated events for one agent, the per-job status scan
    // (jobId-filtered, full-range, ×4 events) is millions of getLogs and never
    // completes — so we report the exact job COUNT and compute detailed status
    // over only the most-recent N jobs. 0 disables the cap. See PlanStatusScan.
    // Default 200 (was 1000): a recent sample of 200 jobs is well above the
    // 5-terminal InsufficientData floor and still statistically valid for the
    // completion/dispute rate, while cutting the status getLogs burst ~5x — the
    // burst that strands high-volume agents on a 429-prone public RPC.
    private readonly int _statusScanMaxJobs;
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
        _statusScanMaxJobs = config.GetValue<int?>("Reputation:StatusScanMaxJobs")   ?? 200;

        // Nethereum's RpcClient defaults to a 20s ConnectionTimeout — applied
        // app-wide via the static ClientBase.ConnectionTimeout. Bump it BEFORE
        // constructing any RpcClient so the per-request CancellationTokenSource
        // Nethereum spins up reads the longer timeout. Note: most public RPCs
        // also enforce their own server-side timeout (publicnode returns "request
        // timed out" on unfiltered eth_getLogs regardless of client timeout),
        // so the fix for that path is a different RPC, not a longer client wait.
        var rpcTimeout = TimeSpan.FromSeconds(
            Math.Max(5, config.GetValue<int?>("Reputation:RpcTimeoutSeconds") ?? 60));
        Nethereum.JsonRpc.Client.ClientBase.ConnectionTimeout = rpcTimeout;
        var rpcClient = new Nethereum.JsonRpc.Client.RpcClient(new Uri(RpcSafe.RequireHttps(rpcUrl, "BASE_RPC_URL")));
        _web3 = new Web3(rpcClient);

        var enumRpcUrl = config["BASE_RPC_URL_ENUM"]
            ?? config["Indexer:V2:SellerScanRpcUrl"];
        if (!string.IsNullOrWhiteSpace(enumRpcUrl)
            && !string.Equals(enumRpcUrl, rpcUrl, StringComparison.OrdinalIgnoreCase))
        {
            var enumClient = new Nethereum.JsonRpc.Client.RpcClient(new Uri(RpcSafe.RequireHttps(enumRpcUrl, "BASE_RPC_URL_ENUM")));
            _enumWeb3 = new Web3(enumClient);
            _logger.LogInformation(
                "[chain-scan] enumerative RPC client ready (separate from per-agent); url={Url}",
                enumRpcUrl);
        }
        else
        {
            _enumWeb3 = _web3;
        }

        _logger.LogInformation(
            "[chain-scan] per-agent RPC client ready; ConnectionTimeout={Sec}s contract={Addr} chunkSize={Chunk}",
            Nethereum.JsonRpc.Client.ClientBase.ConnectionTimeout.TotalSeconds,
            _contractAddress, _chunkSize);
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

        // Use the dedicated enumerative-path Web3 if configured; otherwise
        // falls back to the same per-agent client.
        var handler = _enumWeb3.Eth.GetEvent<JobCreatedEvent>(_contractAddress);
        foreach (var (s, e) in BlockRangeChunker.Chunk(startBlock, toBlock, effectiveChunk))
        {
            ct.ThrowIfCancellationRequested();
            var fromBP = new BlockParameter(new HexBigInteger(s));
            var toBP   = new BlockParameter(new HexBigInteger(e));
            // No topic filter on provider — this is the enumerative pass.
            var filter = handler.CreateFilterInput(fromBP, toBP);

            // Per-chunk retry with backoff. Free public RPCs (publicnode Base)
            // routinely throw transient timeouts / 429s on unfiltered
            // eth_getLogs even on a small range — burning a whole chunk of
            // work to a single flake makes cold-start glacial. Three attempts,
            // 5s/15s backoff, then propagate to the streamer's caller (which
            // leaves the checkpoint at the previous chunk).
            List<Nethereum.Contracts.EventLog<JobCreatedEvent>>? logs = null;
            Exception? lastEx = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(attempt * 10), ct); }
                    catch (OperationCanceledException) { yield break; }
                }
                try
                {
                    logs = (List<Nethereum.Contracts.EventLog<JobCreatedEvent>>?)
                        await handler.GetAllChangesAsync(filter);
                    lastEx = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
                catch (Exception ex)
                {
                    lastEx = ex;
                    _logger.LogWarning(
                        "[chain-scan] chunk [{From}..{To}] attempt {N} failed: {Type} {Msg}",
                        s, e, attempt + 1, ex.GetType().Name, ex.Message);
                }
            }
            if (lastEx is not null) throw lastEx;

            var firstSeen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var log in logs!)
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

    /// <summary>
    /// Per-job ledger for a single agent over the last <paramref name="days"/>.
    /// Returns up to <paramref name="limit"/> jobs newest-first, each with the
    /// counterparty (client wallet), creation timestamp, current status
    /// (active / completed / rejected / expired), and amount in USDC when a
    /// JobFunded event is present.
    ///
    /// RPC-heavy: chunked scans across the requested window. Cap days at 90 to
    /// keep worst-case RPC budget bounded; the public endpoint clamps further.
    /// </summary>
    public async Task<IReadOnlyList<Models.AgentJobRecord>> ListAgentRecentJobsAsync(
        string agentAddress, int days, int limit, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 90);
        limit = Math.Clamp(limit, 1, 100);

        var headBlock = (long)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        long capBlocks = (long)days * 86_400L / BaseBlockTimeSeconds;
        long startBlock = Math.Max(_deployBlock, headBlock - capBlocks);
        if (startBlock > headBlock) return Array.Empty<Models.AgentJobRecord>();

        // 1. JobCreated where provider = agent — gives the per-job (id, client) tuples.
        var createdHandler = _web3.Eth.GetEvent<JobCreatedEvent>(_contractAddress);
        var createdLogs = await RunChunkedAsync(createdHandler,
            (fromBP, toBP) => createdHandler.CreateFilterInput(
                (object[]?)null, (object[]?)null, new object[] { agentAddress }, fromBP, toBP),
            startBlock, headBlock, ct);

        if (createdLogs.Count == 0) return Array.Empty<Models.AgentJobRecord>();

        // Build the per-job records. Status starts as "active"; later events
        // upgrade it to completed / rejected / expired.
        var records = new Dictionary<System.Numerics.BigInteger, (DateTime CreatedAt, string Counterparty, string Status, decimal? AmountUsdc)>();
        foreach (var log in createdLogs)
        {
            ct.ThrowIfCancellationRequested();
            var ts = await GetBlockTimeAsync((long)log.Log.BlockNumber.Value, ct);
            records[log.Event.JobId] = (ts, log.Event.Client, "active", null);
        }

        // Sort + truncate now to the window we care about — we only need the
        // last `limit` jobs, so post-event scans can skip older ones entirely.
        var orderedJobIds = records
            .OrderByDescending(kv => kv.Value.CreatedAt)
            .Take(limit)
            .Select(kv => kv.Key)
            .ToList();

        // Build a HashSet for fast membership checks against the batched logs
        // below. Without this, an agent with thousands of older jobs would
        // pay full RPC cost just to find statuses we throw away.
        var keepSet = new HashSet<System.Numerics.BigInteger>(orderedJobIds);

        // 2. JobFunded — amount per job. JobFunded.Amount is uint256; for V1
        // the unit is the contract's payment token (USDC, 6 decimals on Base).
        // Convert via decimal division and clamp at decimal.MaxValue to defend
        // against pathological values from a bad upstream.
        var fundedHandler = _web3.Eth.GetEvent<JobFundedEvent>(_contractAddress);
        // 3. JobCompleted / JobRejected / JobExpired — status per job.
        var completedHandler = _web3.Eth.GetEvent<JobCompletedEvent>(_contractAddress);
        var rejectedHandler  = _web3.Eth.GetEvent<JobRejectedEvent>(_contractAddress);
        var expiredHandler   = _web3.Eth.GetEvent<JobExpiredEvent>(_contractAddress);

        const int batchSize = 50;
        var keptIds = orderedJobIds;
        for (int i = 0; i < keptIds.Count; i += batchSize)
        {
            var batch = keptIds.GetRange(i, Math.Min(batchSize, keptIds.Count - i));
            var topicJobIds = batch.Select(id => (object)id).ToArray();

            var fundedBatch = await RunChunkedAsync(fundedHandler,
                (fromBP, toBP) => fundedHandler.CreateFilterInput(
                    topicJobIds, (object[]?)null, fromBP, toBP),
                startBlock, headBlock, ct);
            foreach (var log in fundedBatch)
            {
                if (!records.TryGetValue(log.Event.JobId, out var cur)) continue;
                decimal? usdc = null;
                try
                {
                    // USDC is 6 decimals on Base. Cast big amounts down by
                    // dividing first to avoid Decimal overflow at the scale
                    // step. Anything above 1B USDC is almost certainly bad
                    // data — clamp to defend the response shape.
                    var raw = log.Event.Amount;
                    if (raw <= 0) usdc = 0m;
                    else
                    {
                        var divided = raw / 1_000_000;
                        if (divided > 1_000_000_000) usdc = 1_000_000_000m;
                        else usdc = (decimal)divided + ((decimal)(long)(raw % 1_000_000)) / 1_000_000m;
                    }
                }
                catch
                {
                    usdc = null;
                }
                records[log.Event.JobId] = (cur.CreatedAt, cur.Counterparty, cur.Status, usdc);
            }

            var completedBatch = await RunChunkedAsync(completedHandler,
                (fromBP, toBP) => completedHandler.CreateFilterInput(
                    topicJobIds, fromBP, toBP),
                startBlock, headBlock, ct);
            foreach (var log in completedBatch)
            {
                if (!records.TryGetValue(log.Event.JobId, out var cur)) continue;
                records[log.Event.JobId] = (cur.CreatedAt, cur.Counterparty, "completed", cur.AmountUsdc);
            }

            var rejectedBatch = await RunChunkedAsync(rejectedHandler,
                (fromBP, toBP) => rejectedHandler.CreateFilterInput(
                    topicJobIds, (object[]?)null, fromBP, toBP),
                startBlock, headBlock, ct);
            foreach (var log in rejectedBatch)
            {
                if (!records.TryGetValue(log.Event.JobId, out var cur)) continue;
                // Self-rejections (provider rejecting the buyer) shouldn't
                // overwrite a completed status. Mirrors ScanAgentAsync logic.
                if (string.Equals(log.Event.Rejector, agentAddress, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (cur.Status == "completed") continue;
                records[log.Event.JobId] = (cur.CreatedAt, cur.Counterparty, "rejected", cur.AmountUsdc);
            }

            var expiredBatch = await RunChunkedAsync(expiredHandler,
                (fromBP, toBP) => expiredHandler.CreateFilterInput(
                    topicJobIds, fromBP, toBP),
                startBlock, headBlock, ct);
            foreach (var log in expiredBatch)
            {
                if (!records.TryGetValue(log.Event.JobId, out var cur)) continue;
                if (cur.Status == "completed" || cur.Status == "rejected") continue;
                records[log.Event.JobId] = (cur.CreatedAt, cur.Counterparty, "expired", cur.AmountUsdc);
            }
        }

        return orderedJobIds
            .Select(id => records[id])
            .Zip(orderedJobIds, (rec, id) => new Models.AgentJobRecord(
                JobId: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CreatedAt: rec.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Status: rec.Status,
                Counterparty: rec.Counterparty,
                AmountUsdc: rec.AmountUsdc))
            .ToArray();
    }

    /// <summary>
    /// Plan for the bounded per-agent status scan. <see cref="TotalJobs"/> is the EXACT
    /// JobCreated count; <see cref="StatusJobIds"/> is the subset whose detailed status
    /// (funded / completed / rejected / expired / response-time) we actually scan, and
    /// <see cref="StatusFromBlock"/> is the block to scan those events from.
    /// </summary>
    public readonly record struct StatusScanPlan(
        IReadOnlyList<System.Numerics.BigInteger> StatusJobIds,
        long StatusFromBlock,
        long TotalJobs,
        bool Capped);

    /// <summary>
    /// Pure planner: given each job's creation block, decide which jobs to scan status
    /// for. When the count is within <paramref name="cap"/> (or cap &lt;= 0) all jobs are
    /// scanned from <paramref name="fullScanFromBlock"/>. When it exceeds the cap, only
    /// the most-recent <paramref name="cap"/> jobs are scanned, from the earliest of THOSE
    /// jobs' blocks (clamped to <paramref name="fullScanFromBlock"/>) — a job's lifecycle
    /// events never precede its JobCreated, so this window is complete for the sample.
    /// TotalJobs is always the full count, so high-volume agents stop reading as 0.
    /// </summary>
    public static StatusScanPlan PlanStatusScan(
        IReadOnlyDictionary<System.Numerics.BigInteger, long> jobBlocks,
        long fullScanFromBlock,
        int cap)
    {
        long total = jobBlocks.Count;
        if (cap <= 0 || total <= cap)
            return new StatusScanPlan(jobBlocks.Keys.ToList(), fullScanFromBlock, total, Capped: false);

        var recent = jobBlocks.OrderByDescending(kv => kv.Value).Take(cap).ToList();
        long minBlock = recent.Min(kv => kv.Value);
        var ids = recent.Select(kv => kv.Key).ToList();
        return new StatusScanPlan(ids, Math.Max(fullScanFromBlock, minBlock), total, Capped: true);
    }

    public async Task<ChainScanResult> ScanAgentAsync(
        string agentAddress, long fromBlock, DateTime nowUtc, CancellationToken ct)
    {
        var headBlock = (long)(await RpcRetry.ExecuteAsync(
            () => _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync(),
            "blockNumber", _logger, ct)).Value;

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
            // Edge: cap pushed past head, or a lagging RPC node reports a head below
            // our last checkpoint. Return zeros (TotalJobs=0 folds to keep-prior). Do
            // NOT retreat the checkpoint below the prior (fromBlock-1) — persisting a
            // lower head would make the next delta re-scan already-counted blocks and
            // double-count them onto the cumulative total.
            return new ChainScanResult(
                AgentAddress:               agentAddress,
                TotalJobs:                  0, Completed: 0, Rejected: 0, Expired: 0,
                CompletedLast30d:           0,
                LastJobSubmittedAt:         null,
                AvgResponseSeconds30d:      null,
                ResponseTimeSampleCount30d: 0,
                HighestScannedBlock:        Math.Max(headBlock, fromBlock - 1));
        }

        // 1. JobCreated filtered on provider (topic3) — gives the agent's full jobId set.
        //    topic1 (jobId) = any, topic2 (client) = any, topic3 (provider) = agentAddress
        var createdHandler = _web3.Eth.GetEvent<JobCreatedEvent>(_contractAddress);
        var step1Coverage = new ScanCoverage();
        var createdLogs = await RunChunkedAsync(createdHandler,
            (fromBP, toBP) => createdHandler.CreateFilterInput(
                (object[]?)null, (object[]?)null, new object[] { agentAddress }, fromBP, toBP),
            startBlock, headBlock, ct, step1Coverage);

        // Record each job's creation block, then bound the O(jobs) status scan. For a
        // very high-volume agent (20k+ jobs) scanning status per job is millions of
        // getLogs and never completes — which left Quiver (21,983) / UW Agent (21,952)
        // stuck at TotalJobs=0. We keep the EXACT count (plan.TotalJobs) and scan
        // detailed status for only the most-recent StatusScanMaxJobs jobs.
        var jobBlocks = new Dictionary<System.Numerics.BigInteger, long>();
        foreach (var log in createdLogs)
            jobBlocks[log.Event.JobId] = (long)log.Log.BlockNumber.Value;

        var plan = PlanStatusScan(jobBlocks, startBlock, _statusScanMaxJobs);
        var statusFromBlock = plan.StatusFromBlock;

        // Always-on step-1 diagnostic. The JobCreated count is the EXACT job total
        // for this window and is independent of the (bounded, flaky) status scan.
        // failedChunks>0 means step-1 had to abandon chunks (RPC exhausted retries)
        // so the count may be an undercount — untrustworthy scans must NOT accumulate
        // or advance the checkpoint (see ChooseHighestScannedBlock + the caller's
        // ComputeEffectiveTotalJobs). This line is what reveals, on the next pass and
        // without anyone watching, whether a high-volume agent's step-1 truly saw ~0.
        var step1Trustworthy = step1Coverage.FailedChunks == 0;
        var wasColdStartScan = fromBlock <= _deployBlock;
        _logger.LogInformation(
            "[chain-scan] {Addr} step1 jobCreated={Count} window=[{Start}..{Head}] chunks={Chunks} failedChunks={Failed} coldStart={Cold} trustworthy={Trust}",
            agentAddress, jobBlocks.Count, startBlock, headBlock,
            ((headBlock - startBlock) / _chunkSize) + 1, step1Coverage.FailedChunks,
            wasColdStartScan, step1Trustworthy);
        if (plan.Capped)
            _logger.LogInformation(
                "[chain-scan] {Addr} high-volume: {Total} jobs; status scanned for most-recent {N} from block {From}",
                agentAddress, plan.TotalJobs, plan.StatusJobIds.Count, statusFromBlock);

        var jobIds = new HashSet<System.Numerics.BigInteger>(plan.StatusJobIds);
        var fundedTimestamps    = new Dictionary<System.Numerics.BigInteger, DateTime>();
        var submittedTimestamps = new Dictionary<System.Numerics.BigInteger, DateTime>();
        DateTime? lastSubmitted = null;

        long completed = 0, rejected = 0, expired = 0, completedLast30d = 0;
        var thirtyDaysAgo = nowUtc.AddDays(-30);

        // Steps 2-3 (the bounded status scan) are DECOUPLED from the persisted job
        // COUNT: TotalJobs comes from step 1 (plan.TotalJobs) and is already known.
        // If the status scan throws — a getLogs chunk or a GetBlockTime lookup that
        // exhausts retries under a 429 burst — we log + degrade rather than abort the
        // whole scan, otherwise a high-volume agent like UW (21,944 jobs from step 1)
        // would land NO row at all (its row was previously lost exactly this way).
        // statusScanComplete flows into the InsufficientData flag so a partial-status
        // row is labelled honestly instead of showing a confident neutral-50.
        bool statusScanComplete = true;
        var statusCoverage = new ScanCoverage();
        try
        {
            // 2. JobSubmitted filtered on provider (topic2) — gives T1 for response time.
            //    topic1 (jobId) = any, topic2 (provider) = agentAddress
            var submittedHandler = _web3.Eth.GetEvent<JobSubmittedEvent>(_contractAddress);
            var submittedLogs = await RunChunkedAsync(submittedHandler,
                (fromBP, toBP) => submittedHandler.CreateFilterInput(
                    (object[]?)null, new object[] { agentAddress }, fromBP, toBP),
                statusFromBlock, headBlock, ct, statusCoverage);
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
                    statusFromBlock, headBlock, ct, statusCoverage);
                foreach (var log in fundedBatch)
                {
                    fundedTimestamps[log.Event.JobId] =
                        await GetBlockTimeAsync((long)log.Log.BlockNumber.Value, ct);
                }

                // JobCompleted: topic1 = jobId (indexed), topic2 = evaluator (indexed) — filter on jobId only.
                var completedBatch = await RunChunkedAsync(completedHandler,
                    (fromBP, toBP) => completedHandler.CreateFilterInput(
                        topicJobIds, fromBP, toBP),
                    statusFromBlock, headBlock, ct, statusCoverage);
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
                    statusFromBlock, headBlock, ct, statusCoverage);
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
                    statusFromBlock, headBlock, ct, statusCoverage);
                expired += expiredBatch.Count;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            statusScanComplete = false;
            _logger.LogWarning(ex,
                "[chain-scan] {Addr} status scan incomplete after step1 ({Total} jobs); persisting exact TotalJobs with partial status",
                agentAddress, plan.TotalJobs);
        }
        // A silently-dropped status chunk (continue-on-fail, no throw) also means
        // the recent-window terminal counts are a partial sample.
        if (statusCoverage.FailedChunks > 0) statusScanComplete = false;

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
            TotalJobs:                  plan.TotalJobs,
            Completed:                  completed,
            Rejected:                   rejected,
            Expired:                    expired,
            CompletedLast30d:           completedLast30d,
            LastJobSubmittedAt:         lastSubmitted,
            AvgResponseSeconds30d:      avgResponseSeconds,
            ResponseTimeSampleCount30d: sampleCount,
            HighestScannedBlock:        ChooseHighestScannedBlock(headBlock, fromBlock, step1Trustworthy),
            Step1Trustworthy:           step1Trustworthy,
            WasColdStartScan:           wasColdStartScan,
            StatusScanComplete:         statusScanComplete);
    }

    // Mutable per-scan chunk-failure sink. When passed to RunChunkedAsync, it
    // counts chunks that exhausted RPC retries and were skipped (not aborted on).
    // Used by ScanAgentAsync to decide whether the step-1 count is trustworthy
    // enough to accumulate + advance the checkpoint. Null sink = caller doesn't
    // care about coverage (still gets continue-on-fail robustness).
    private sealed class ScanCoverage { public int FailedChunks; }

    // Pure: where to leave the scan checkpoint. A trustworthy scan advances to
    // the head it reached; an untrustworthy one (step-1 dropped chunks) must NOT
    // advance past the data it missed — leave it exactly one below the original
    // fromBlock so the next pass re-scans the identical window. Exposed for unit
    // tests. fromBlock is the raw caller-supplied value (>= 1).
    public static long ChooseHighestScannedBlock(long head, long fromBlock, bool step1Trustworthy)
        => step1Trustworthy ? head : fromBlock - 1;

    private async Task<List<EventLog<TEvt>>> RunChunkedAsync<TEvt>(
        Event<TEvt> handler,
        Func<BlockParameter, BlockParameter, NewFilterInput> filterBuilder,
        long fromBlock, long toBlock, CancellationToken ct,
        ScanCoverage? coverage = null) where TEvt : IEventDTO, new()
    {
        var all = new List<EventLog<TEvt>>();
        foreach (var (s, e) in BlockRangeChunker.Chunk(fromBlock, toBlock, _chunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var fromBP = new BlockParameter(new HexBigInteger(s));
            var toBP   = new BlockParameter(new HexBigInteger(e));
            var filter = filterBuilder(fromBP, toBP);
            // Wrapped in RpcRetry so 429s / 5xx / network flakes on free Base
            // RPCs don't kill a scan mid-flight. If a single chunk STILL exhausts
            // its retry budget we log + count it and CONTINUE rather than abort
            // the whole scan — one flaky chunk must not strand a 390-chunk
            // cold-start (which is how high-volume agents ended up with no row).
            // The caller inspects coverage.FailedChunks to decide whether the
            // resulting count is complete enough to trust.
            try
            {
                var logs = await RpcRetry.ExecuteAsync(
                    () => handler.GetAllChangesAsync(filter),
                    $"getLogs[{s}..{e}]", _logger, ct);
                all.AddRange(logs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (coverage is not null) coverage.FailedChunks++;
                _logger.LogWarning(
                    "[chain-scan] chunk getLogs[{From}..{To}] GIVING UP after retries: {Type} {Msg}",
                    s, e, ex.GetType().Name, ex.Message);
            }
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
        var block = await RpcRetry.ExecuteAsync(
            () => _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(
                new HexBigInteger(blockNumber)),
            $"getBlock[{blockNumber}]", _logger, ct);
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
