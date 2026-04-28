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
    private readonly ILogger<ChainEventScanner> _logger;
    // Block-timestamp LRU; bounded so it can't grow without limit during a long warmer pass.
    private readonly Dictionary<long, DateTime> _blockTimestamps = new(capacity: 4096);

    public ChainEventScanner(IConfiguration config, ILogger<ChainEventScanner> logger)
    {
        _logger = logger;
        var rpcUrl = config["BASE_RPC_URL"]
            ?? throw new InvalidOperationException("BASE_RPC_URL not configured");
        _contractAddress = config["ACP_CONTRACT_ADDRESS_BASE"]
            ?? throw new InvalidOperationException("ACP_CONTRACT_ADDRESS_BASE not configured");
        _deployBlock = config.GetValue<long?>("Reputation:ContractDeployBlock") ?? 0L;
        _web3 = new Web3(rpcUrl);
    }

    public async Task<ChainScanResult> ScanAgentAsync(
        string agentAddress, long fromBlock, DateTime nowUtc, CancellationToken ct)
    {
        var headBlock = (long)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        var startBlock = Math.Max(fromBlock, _deployBlock);

        var fromBP = new BlockParameter(new HexBigInteger(startBlock));
        var toBP   = new BlockParameter(new HexBigInteger(headBlock));

        // 1. JobCreated filtered on provider (topic3) — gives the agent's full jobId set.
        //    topic1 (jobId) = any, topic2 (client) = any, topic3 (provider) = agentAddress
        var createdHandler = _web3.Eth.GetEvent<JobCreatedEvent>(_contractAddress);
        var createdFilter = createdHandler.CreateFilterInput(
            (object[]?)null,
            (object[]?)null,
            new object[] { agentAddress },
            fromBP,
            toBP);
        var createdLogs = await createdHandler.GetAllChangesAsync(createdFilter);

        var jobIds = new HashSet<System.Numerics.BigInteger>();
        var fundedTimestamps     = new Dictionary<System.Numerics.BigInteger, DateTime>();
        var submittedTimestamps  = new Dictionary<System.Numerics.BigInteger, DateTime>();
        DateTime? lastSubmitted  = null;
        foreach (var log in createdLogs) jobIds.Add(log.Event.JobId);

        // 2. JobSubmitted filtered on provider (topic2) — gives T1 for response time.
        //    topic1 (jobId) = any, topic2 (provider) = agentAddress
        var submittedHandler = _web3.Eth.GetEvent<JobSubmittedEvent>(_contractAddress);
        var submittedFilter = submittedHandler.CreateFilterInput(
            (object[]?)null,
            new object[] { agentAddress },
            fromBP,
            toBP);
        var submittedLogs = await submittedHandler.GetAllChangesAsync(submittedFilter);
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
            var fundedBatch = await fundedHandler.GetAllChangesAsync(
                fundedHandler.CreateFilterInput(
                    topicJobIds,
                    (object[]?)null,
                    fromBP,
                    toBP));
            foreach (var log in fundedBatch)
            {
                fundedTimestamps[log.Event.JobId] =
                    await GetBlockTimeAsync((long)log.Log.BlockNumber.Value, ct);
            }

            // JobCompleted: topic1 = jobId (indexed only)
            var completedBatch = await completedHandler.GetAllChangesAsync(
                completedHandler.CreateFilterInput(
                    topicJobIds,
                    fromBP,
                    toBP));
            foreach (var log in completedBatch)
            {
                completed++;
                var ts = await GetBlockTimeAsync((long)log.Log.BlockNumber.Value, ct);
                if (ts >= thirtyDaysAgo) completedLast30d++;
            }

            // JobRejected: topic1 = jobId (indexed), topic2 = rejector (indexed)
            var rejectedBatch = await rejectedHandler.GetAllChangesAsync(
                rejectedHandler.CreateFilterInput(
                    topicJobIds,
                    (object[]?)null,
                    fromBP,
                    toBP));
            foreach (var log in rejectedBatch)
            {
                // Exclude self-rejections (agent rejecting buyer's spec).
                if (string.Equals(log.Event.Rejector, agentAddress, StringComparison.OrdinalIgnoreCase))
                    continue;
                rejected++;
            }

            // JobExpired: topic1 = jobId (indexed only)
            var expiredBatch = await expiredHandler.GetAllChangesAsync(
                expiredHandler.CreateFilterInput(
                    topicJobIds,
                    fromBP,
                    toBP));
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

    private async Task<DateTime> GetBlockTimeAsync(long blockNumber, CancellationToken ct)
    {
        if (_blockTimestamps.TryGetValue(blockNumber, out var cached)) return cached;
        var block = await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(
            new HexBigInteger(blockNumber));
        var ts = DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value).UtcDateTime;
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
