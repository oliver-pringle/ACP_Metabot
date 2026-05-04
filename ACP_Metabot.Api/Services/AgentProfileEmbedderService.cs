namespace ACP_Metabot.Api.Services;

using ACP_Metabot.Api.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class AgentProfileEmbedderService : BackgroundService
{
    private readonly AgentProfileRepository _repo;
    private readonly IEmbeddingProvider _embed;
    private readonly ILogger<AgentProfileEmbedderService> _log;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
    private const int DefaultBatch = 128;

    public AgentProfileEmbedderService(
        AgentProfileRepository repo,
        IEmbeddingProvider embed,
        ILogger<AgentProfileEmbedderService> log)
    {
        _repo = repo; _embed = embed; _log = log;
    }

    public async Task DrainOnceAsync(int batchSize, CancellationToken ct)
    {
        var dirty = await _repo.ListDirtyAsync(batchSize);
        if (dirty.Count == 0) return;

        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await _embed.EmbedAsync(dirty.Select(d => d.ProfileText).ToList(), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "agent profile embed batch failed; will retry next cycle");
            return;
        }

        for (int i = 0; i < dirty.Count; i++)
        {
            var blob = FloatsToBytes(vectors[i]);
            await _repo.MarkEmbeddedAsync(dirty[i].AgentAddress, _embed.ModelId, blob);
        }
        _log.LogInformation("agent profile embed batch: {Count} drained", dirty.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var seeded = await _repo.BackfillFromOfferingsAsync(profileTextCap: 2000);
            _log.LogInformation("agent_profiles cold-start backfill seeded {N} rows", seeded);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cold-start backfill failed; embedder will catch up via dirty queue");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainOnceAsync(DefaultBatch, stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "embedder drain cycle failed"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static byte[] FloatsToBytes(float[] xs)
    {
        var bytes = new byte[xs.Length * sizeof(float)];
        Buffer.BlockCopy(xs, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
