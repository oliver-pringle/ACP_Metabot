using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// v1.10 Phase 1: lazy-backfills embeddings for agent_resources rows that
/// don't yet have a resources_embeddings entry. Default OFF — flip
/// Resources:EmbedWorker:Enabled (env: RESOURCES_EMBED_WORKER_ENABLED) to
/// true on the droplet when ready to start consuming Voyage quota for
/// Resources. Polls every 30 minutes by default; processes up to 50 rows
/// per tick.
///
/// Phase 1 ships the plumbing; embedding-based Resource ranking remains a
/// Phase 2+ ambition. The FTS-based SearchHybridAsync path (T6) is the
/// primary Resource-search lane in Phase 1.
///
/// BLOB format MUST match offering_embeddings — little-endian float32 via
/// Buffer.BlockCopy(float[], …, byte[], …, len). Phase 2 cosine compare
/// across the two tables will silently produce garbage if the encodings
/// diverge.
/// </summary>
public sealed class ResourcesEmbeddingsWorker : BackgroundService
{
    private readonly Db _db;
    private readonly IEmbeddingProvider _embedder;
    private readonly ILogger<ResourcesEmbeddingsWorker> _logger;
    private readonly int _intervalSeconds;
    private const int BatchSize = 50;

    public ResourcesEmbeddingsWorker(
        Db db, IEmbeddingProvider embedder,
        IConfiguration config,
        ILogger<ResourcesEmbeddingsWorker> logger)
    {
        _db = db;
        _embedder = embedder;
        _logger = logger;
        _intervalSeconds = Math.Max(60,
            config.GetValue<int?>("Resources:EmbedWorker:IntervalSeconds") ?? 1800);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[resources-embed] starting; interval={s}s, batchSize={b}, model={m}",
            _intervalSeconds, BatchSize, _embedder.ModelId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await EmbedNextBatchAsync(stoppingToken);
                if (processed > 0)
                    _logger.LogInformation("[resources-embed] embedded {n} rows", processed);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[resources-embed] tick failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task<int> EmbedNextBatchAsync(CancellationToken ct)
    {
        var rows = new List<(long Id, string Text)>();
        await using (var conn = _db.OpenConnection())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT r.id, r.name || ' — ' || COALESCE(r.description, '')
                FROM agent_resources r
                LEFT JOIN resources_embeddings e ON e.resource_id = r.id
                WHERE e.id IS NULL
                ORDER BY r.last_seen_at DESC
                LIMIT $lim;";
            cmd.Parameters.AddWithValue("$lim", BatchSize);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
                rows.Add((rdr.GetInt64(0), rdr.GetString(1)));
        }
        if (rows.Count == 0) return 0;

        var texts = rows.Select(r => r.Text).ToList();
        IReadOnlyList<float[]> embeddings;
        try
        {
            embeddings = await _embedder.EmbedAsync(texts, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[resources-embed] embedder failed for {n} rows; will retry next cycle",
                rows.Count);
            return 0;
        }

        if (embeddings.Count != rows.Count)
        {
            _logger.LogWarning(
                "[resources-embed] embedder returned {got} embeddings for {asked} rows",
                embeddings.Count, rows.Count);
            return 0;
        }

        var now = DateTime.UtcNow.ToString("O");
        await using (var conn = _db.OpenConnection())
        await using (var tx = await conn.BeginTransactionAsync(ct))
        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            ins.CommandText = @"
                INSERT INTO resources_embeddings(resource_id, embedding, embedded_at)
                VALUES ($rid, $emb, $now);";
            ins.Parameters.Add("$rid", Microsoft.Data.Sqlite.SqliteType.Integer);
            ins.Parameters.Add("$emb", Microsoft.Data.Sqlite.SqliteType.Blob);
            ins.Parameters.Add("$now", Microsoft.Data.Sqlite.SqliteType.Text);
            for (int i = 0; i < rows.Count; i++)
            {
                ins.Parameters["$rid"].Value = rows[i].Id;
                ins.Parameters["$emb"].Value = FloatsToBytes(embeddings[i]);
                ins.Parameters["$now"].Value = now;
                await ins.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return rows.Count;
    }

    /// <summary>
    /// float[] → BLOB encoding. MUST match the format used by
    /// AgentProfileEmbedderService.FloatsToBytes so Phase 2 cosine-compare
    /// across resources_embeddings and offering_embeddings stays correct.
    /// </summary>
    private static byte[] FloatsToBytes(float[] xs)
    {
        var bytes = new byte[xs.Length * sizeof(float)];
        Buffer.BlockCopy(xs, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
