using System.Globalization;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public record UpsertResult(long Id, bool IsNew, bool ContentChanged);

public record UpsertItem(
    string AgentAddress, string AgentName, string OfferingName,
    string Description, string? RequirementSchemaJson, double PriceUsdc,
    string PriceType, bool IsPrivate, string Chain, string ContentHash,
    long UsageCount, long AgentJobCount,
    string MarketplaceVersion = "v1");

public record UpsertSummary(int Added, int Updated, int Unchanged);

public class OfferingRepository
{
    private readonly Db _db;

    public OfferingRepository(Db db) => _db = db;

    public async Task<UpsertResult> UpsertAsync(
        string agentAddress, string agentName, string offeringName,
        string description, string? requirementSchemaJson, double priceUsdc,
        string priceType, bool isPrivate, string chain, string contentHash,
        long usageCount, long agentJobCount, DateTime nowUtc,
        string marketplaceVersion = "v1")
    {
        await using var conn = _db.OpenConnection();
        var nowIso = nowUtc.ToString("O", CultureInfo.InvariantCulture);

        // Check existing — keyed on the v1.3 composite (mv, addr, name).
        await using (var get = conn.CreateCommand())
        {
            get.CommandText = "SELECT id, content_hash FROM offerings WHERE marketplace_version = $mv AND agent_address = $a AND offering_name = $n;";
            get.Parameters.AddWithValue("$mv", marketplaceVersion);
            get.Parameters.AddWithValue("$a", agentAddress);
            get.Parameters.AddWithValue("$n", offeringName);
            await using var reader = await get.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var existingId = reader.GetInt64(0);
                var existingHash = reader.GetString(1);
                await reader.CloseAsync();

                if (existingHash == contentHash)
                {
                    // Same content — just bump last_seen_at + popularity counters
                    // (counters change every fetch but aren't part of the hash;
                    // tracking them in-band avoids a second update path).
                    await using var touch = conn.CreateCommand();
                    touch.CommandText = @"
                        UPDATE offerings
                        SET last_seen_at    = $now,
                            usage_count     = $usage,
                            agent_job_count = $agentJobs
                        WHERE id = $id;";
                    touch.Parameters.AddWithValue("$now", nowIso);
                    touch.Parameters.AddWithValue("$usage", usageCount);
                    touch.Parameters.AddWithValue("$agentJobs", agentJobCount);
                    touch.Parameters.AddWithValue("$id", existingId);
                    await touch.ExecuteNonQueryAsync();
                    return new UpsertResult(existingId, IsNew: false, ContentChanged: false);
                }

                // Content changed — update fields
                await using var upd = conn.CreateCommand();
                upd.CommandText = @"
                    UPDATE offerings
                    SET agent_name              = $agentName,
                        description             = $desc,
                        requirement_schema_json = $schema,
                        price_usdc              = $price,
                        price_type              = $pType,
                        is_private              = $priv,
                        chain                   = $chain,
                        content_hash            = $hash,
                        last_seen_at            = $now,
                        usage_count             = $usage,
                        agent_job_count         = $agentJobs
                    WHERE id = $id;";
                upd.Parameters.AddWithValue("$agentName", agentName);
                upd.Parameters.AddWithValue("$desc", description);
                upd.Parameters.AddWithValue("$schema", (object?)requirementSchemaJson ?? DBNull.Value);
                upd.Parameters.AddWithValue("$price", priceUsdc);
                upd.Parameters.AddWithValue("$pType", priceType);
                upd.Parameters.AddWithValue("$priv", isPrivate ? 1 : 0);
                upd.Parameters.AddWithValue("$chain", chain);
                upd.Parameters.AddWithValue("$hash", contentHash);
                upd.Parameters.AddWithValue("$now", nowIso);
                upd.Parameters.AddWithValue("$usage", usageCount);
                upd.Parameters.AddWithValue("$agentJobs", agentJobCount);
                upd.Parameters.AddWithValue("$id", existingId);
                await upd.ExecuteNonQueryAsync();
                return new UpsertResult(existingId, IsNew: false, ContentChanged: true);
            }
        }

        // Insert
        await using var ins = conn.CreateCommand();
        ins.CommandText = @"
            INSERT INTO offerings (
                agent_address, agent_name, offering_name, description,
                requirement_schema_json, price_usdc, price_type, is_private,
                chain, content_hash, first_seen_at, last_seen_at,
                usage_count, agent_job_count, marketplace_version)
            VALUES (
                $a, $agentName, $n, $desc,
                $schema, $price, $pType, $priv,
                $chain, $hash, $now, $now,
                $usage, $agentJobs, $mv);
            SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$a", agentAddress);
        ins.Parameters.AddWithValue("$agentName", agentName);
        ins.Parameters.AddWithValue("$n", offeringName);
        ins.Parameters.AddWithValue("$desc", description);
        ins.Parameters.AddWithValue("$schema", (object?)requirementSchemaJson ?? DBNull.Value);
        ins.Parameters.AddWithValue("$price", priceUsdc);
        ins.Parameters.AddWithValue("$pType", priceType);
        ins.Parameters.AddWithValue("$priv", isPrivate ? 1 : 0);
        ins.Parameters.AddWithValue("$chain", chain);
        ins.Parameters.AddWithValue("$hash", contentHash);
        ins.Parameters.AddWithValue("$now", nowIso);
        ins.Parameters.AddWithValue("$usage", usageCount);
        ins.Parameters.AddWithValue("$agentJobs", agentJobCount);
        ins.Parameters.AddWithValue("$mv", marketplaceVersion);
        var newId = (long)(await ins.ExecuteScalarAsync() ?? 0L);
        return new UpsertResult(newId, IsNew: true, ContentChanged: true);
    }

    // Bulk version of UpsertAsync. Opens one connection, pre-fetches the
    // existing-row index in a single SELECT, runs the entire loop inside one
    // transaction, and reuses three prepared commands. ~50-100x faster than
    // calling UpsertAsync per row because it avoids fsync-per-statement.
    public async Task<UpsertSummary> UpsertManyAsync(IReadOnlyList<UpsertItem> items, DateTime nowUtc)
    {
        var nowIso = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        int added = 0, updated = 0, unchanged = 0;

        await using var conn = _db.OpenConnection();

        // Pre-fetch existing rows: (mv, agent_address, offering_name) ->
        // (id, content_hash). Composite key matches the v1.3 UNIQUE constraint
        // so v1 and v2 rows for the same (addr, name) don't collide here.
        var existing = new Dictionary<string, (long Id, string Hash)>(
            capacity: items.Count, StringComparer.Ordinal);
        await using (var pre = conn.CreateCommand())
        {
            pre.CommandText = "SELECT marketplace_version, agent_address, offering_name, id, content_hash FROM offerings;";
            await using var reader = await pre.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var key = reader.GetString(0) + "|" + reader.GetString(1) + "|" + reader.GetString(2);
                existing[key] = (reader.GetInt64(3), reader.GetString(4));
            }
        }

        await using var tx = conn.BeginTransaction();

        await using var touch = conn.CreateCommand();
        touch.Transaction = tx;
        touch.CommandText = @"
            UPDATE offerings
            SET last_seen_at    = $now,
                usage_count     = $usage,
                agent_job_count = $agentJobs
            WHERE id = $id;";
        var tNow = touch.Parameters.Add("$now", SqliteType.Text);
        tNow.Value = nowIso;
        var tUsage = touch.Parameters.Add("$usage", SqliteType.Integer);
        var tAgentJobs = touch.Parameters.Add("$agentJobs", SqliteType.Integer);
        var tId = touch.Parameters.Add("$id", SqliteType.Integer);

        await using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = @"
            UPDATE offerings
            SET agent_name              = $agentName,
                description             = $desc,
                requirement_schema_json = $schema,
                price_usdc              = $price,
                price_type              = $pType,
                is_private              = $priv,
                chain                   = $chain,
                content_hash            = $hash,
                last_seen_at            = $now,
                usage_count             = $usage,
                agent_job_count         = $agentJobs
            WHERE id = $id;";
        var uAgentName = upd.Parameters.Add("$agentName", SqliteType.Text);
        var uDesc = upd.Parameters.Add("$desc", SqliteType.Text);
        var uSchema = upd.Parameters.Add("$schema", SqliteType.Text);
        var uPrice = upd.Parameters.Add("$price", SqliteType.Real);
        var uPType = upd.Parameters.Add("$pType", SqliteType.Text);
        var uPriv = upd.Parameters.Add("$priv", SqliteType.Integer);
        var uChain = upd.Parameters.Add("$chain", SqliteType.Text);
        var uHash = upd.Parameters.Add("$hash", SqliteType.Text);
        var uNow = upd.Parameters.Add("$now", SqliteType.Text);
        uNow.Value = nowIso;
        var uUsage = upd.Parameters.Add("$usage", SqliteType.Integer);
        var uAgentJobs = upd.Parameters.Add("$agentJobs", SqliteType.Integer);
        var uId = upd.Parameters.Add("$id", SqliteType.Integer);

        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
            INSERT INTO offerings (
                agent_address, agent_name, offering_name, description,
                requirement_schema_json, price_usdc, price_type, is_private,
                chain, content_hash, first_seen_at, last_seen_at,
                usage_count, agent_job_count, marketplace_version)
            VALUES (
                $a, $agentName, $n, $desc,
                $schema, $price, $pType, $priv,
                $chain, $hash, $now, $now,
                $usage, $agentJobs, $mv);";
        var iA = ins.Parameters.Add("$a", SqliteType.Text);
        var iAgentName = ins.Parameters.Add("$agentName", SqliteType.Text);
        var iN = ins.Parameters.Add("$n", SqliteType.Text);
        var iDesc = ins.Parameters.Add("$desc", SqliteType.Text);
        var iSchema = ins.Parameters.Add("$schema", SqliteType.Text);
        var iPrice = ins.Parameters.Add("$price", SqliteType.Real);
        var iPType = ins.Parameters.Add("$pType", SqliteType.Text);
        var iPriv = ins.Parameters.Add("$priv", SqliteType.Integer);
        var iChain = ins.Parameters.Add("$chain", SqliteType.Text);
        var iHash = ins.Parameters.Add("$hash", SqliteType.Text);
        var iNow = ins.Parameters.Add("$now", SqliteType.Text);
        iNow.Value = nowIso;
        var iUsage = ins.Parameters.Add("$usage", SqliteType.Integer);
        var iAgentJobs = ins.Parameters.Add("$agentJobs", SqliteType.Integer);
        var iMv = ins.Parameters.Add("$mv", SqliteType.Text);

        foreach (var item in items)
        {
            var key = item.MarketplaceVersion + "|" + item.AgentAddress + "|" + item.OfferingName;
            if (existing.TryGetValue(key, out var ex))
            {
                if (ex.Hash == item.ContentHash)
                {
                    tUsage.Value = item.UsageCount;
                    tAgentJobs.Value = item.AgentJobCount;
                    tId.Value = ex.Id;
                    await touch.ExecuteNonQueryAsync();
                    unchanged++;
                }
                else
                {
                    uAgentName.Value = item.AgentName;
                    uDesc.Value = item.Description;
                    uSchema.Value = (object?)item.RequirementSchemaJson ?? DBNull.Value;
                    uPrice.Value = item.PriceUsdc;
                    uPType.Value = item.PriceType;
                    uPriv.Value = item.IsPrivate ? 1 : 0;
                    uChain.Value = item.Chain;
                    uHash.Value = item.ContentHash;
                    uUsage.Value = item.UsageCount;
                    uAgentJobs.Value = item.AgentJobCount;
                    uId.Value = ex.Id;
                    await upd.ExecuteNonQueryAsync();
                    updated++;
                }
            }
            else
            {
                iA.Value = item.AgentAddress;
                iAgentName.Value = item.AgentName;
                iN.Value = item.OfferingName;
                iDesc.Value = item.Description;
                iSchema.Value = (object?)item.RequirementSchemaJson ?? DBNull.Value;
                iPrice.Value = item.PriceUsdc;
                iPType.Value = item.PriceType;
                iPriv.Value = item.IsPrivate ? 1 : 0;
                iChain.Value = item.Chain;
                iHash.Value = item.ContentHash;
                iUsage.Value = item.UsageCount;
                iAgentJobs.Value = item.AgentJobCount;
                iMv.Value = item.MarketplaceVersion;
                await ins.ExecuteNonQueryAsync();
                added++;
            }
        }

        await tx.CommitAsync();
        return new UpsertSummary(added, updated, unchanged);
    }

    public async Task UpsertEmbeddingAsync(long offeringId, string model, int dimension, byte[] blob, DateTime nowUtc)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offering_embeddings (offering_id, model, dimension, embedding_blob, embedded_at)
            VALUES ($id, $m, $d, $b, $t)
            ON CONFLICT(offering_id) DO UPDATE SET
                model          = excluded.model,
                dimension      = excluded.dimension,
                embedding_blob = excluded.embedding_blob,
                embedded_at    = excluded.embedded_at;";
        cmd.Parameters.AddWithValue("$id", offeringId);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$d", dimension);
        cmd.Parameters.AddWithValue("$b", blob);
        cmd.Parameters.AddWithValue("$t", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Offering?> GetByIdAsync(long id)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, agent_address, agent_name, offering_name, description,
                   requirement_schema_json, price_usdc, price_type, is_private,
                   chain, content_hash, first_seen_at, last_seen_at,
                   usage_count, agent_job_count, marketplace_version
            FROM offerings WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return MapOffering(reader);
    }

    public async Task<List<Offering>> ListNeedingEmbeddingAsync(int limit, int dimension, string model)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT o.id, o.agent_address, o.agent_name, o.offering_name, o.description,
                   o.requirement_schema_json, o.price_usdc, o.price_type, o.is_private,
                   o.chain, o.content_hash, o.first_seen_at, o.last_seen_at,
                   o.usage_count, o.agent_job_count, o.marketplace_version
            FROM offerings o
            LEFT JOIN offering_embeddings e ON e.offering_id = o.id
            WHERE e.offering_id IS NULL
               OR e.dimension != $d
               OR e.model != $m
            ORDER BY o.id ASC
            LIMIT $lim;";
        cmd.Parameters.AddWithValue("$d", dimension);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$lim", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Offering>();
        while (await reader.ReadAsync()) result.Add(MapOffering(reader));
        return result;
    }

    /// <summary>
    /// Returns ALL embedded offerings as (offering, embedding) pairs.
    /// Used for in-memory cosine search. Fine up to ~50K offerings.
    /// </summary>
    public async Task<List<(Offering Offering, float[] Embedding)>> ListAllWithEmbeddingsAsync(string requiredModel)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT o.id, o.agent_address, o.agent_name, o.offering_name, o.description,
                   o.requirement_schema_json, o.price_usdc, o.price_type, o.is_private,
                   o.chain, o.content_hash, o.first_seen_at, o.last_seen_at,
                   o.usage_count, o.agent_job_count, o.marketplace_version,
                   e.dimension, e.embedding_blob
            FROM offerings o
            INNER JOIN offering_embeddings e ON e.offering_id = o.id
            WHERE e.model = $m;";
        cmd.Parameters.AddWithValue("$m", requiredModel);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<(Offering, float[])>();
        while (await reader.ReadAsync())
        {
            var o = MapOffering(reader);
            var dim = reader.GetInt32(16);
            var blob = (byte[])reader[17];
            var emb = new float[dim];
            Buffer.BlockCopy(blob, 0, emb, 0, dim * sizeof(float));
            result.Add((o, emb));
        }
        return result;
    }

    /// <summary>
    /// FTS5 BM25 ranking over offerings_fts. Returns rowid + raw bm25 score
    /// (lower = better, per SQLite). Column weights: offering_name(3) >
    /// agent_name(2) > description(1). Caller decides fusion strategy with
    /// the dense ranking. Returns empty list on any FTS error so the
    /// SearchService can fall back to dense-only.
    /// </summary>
    public async Task<IReadOnlyList<(long Id, double Bm25)>> SearchBm25Async(string query, int limit)
    {
        var match = SanitizeFtsQuery(query);
        if (match is null) return Array.Empty<(long, double)>();

        try
        {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT rowid, bm25(offerings_fts, 3.0, 2.0, 1.0) AS r
                FROM offerings_fts
                WHERE offerings_fts MATCH $q
                ORDER BY r
                LIMIT $lim;";
            cmd.Parameters.AddWithValue("$q", match);
            cmd.Parameters.AddWithValue("$lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<(long, double)>(limit);
            while (await reader.ReadAsync())
            {
                result.Add((reader.GetInt64(0), reader.GetDouble(1)));
            }
            return result;
        }
        catch (SqliteException)
        {
            // FTS5 not enabled, malformed query, or transient lock — let the
            // caller proceed with dense-only ordering.
            return Array.Empty<(long, double)>();
        }
    }

    /// <summary>
    /// Strips FTS5 operators and wraps multi-token queries in quotes for
    /// paste-safety. Hex contract addresses, tickers with punctuation, and
    /// queries containing colons/parentheses survive intact as phrase matches.
    /// Returns null when the input collapses to whitespace.
    /// </summary>
    private static string? SanitizeFtsQuery(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Drop FTS5-meaningful characters; keep alphanumerics, underscore,
        // hyphen, period (covers domain names + most identifiers).
        var stripped = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' || ch == ' ')
                stripped.Append(ch);
            else
                stripped.Append(' ');
        }
        var collapsed = System.Text.RegularExpressions.Regex.Replace(
            stripped.ToString().Trim(), @"\s+", " ");
        if (collapsed.Length == 0) return null;
        // Wrap as a phrase if multi-token, otherwise a prefix-tolerant bare
        // term. Phrase quoting also defeats accidental NEAR/AND keywords.
        return collapsed.Contains(' ') ? "\"" + collapsed + "\"" : collapsed;
    }

    public async Task<List<Offering>> ListAllAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, agent_address, agent_name, offering_name, description,
                   requirement_schema_json, price_usdc, price_type, is_private,
                   chain, content_hash, first_seen_at, last_seen_at,
                   usage_count, agent_job_count, marketplace_version
            FROM offerings;";
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Offering>();
        while (await reader.ReadAsync()) result.Add(MapOffering(reader));
        return result;
    }

    public async Task<List<Offering>> ListByAgentAsync(string agentAddress)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, agent_address, agent_name, offering_name, description,
                   requirement_schema_json, price_usdc, price_type, is_private,
                   chain, content_hash, first_seen_at, last_seen_at,
                   usage_count, agent_job_count, marketplace_version
            FROM offerings
            WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Offering>();
        while (await reader.ReadAsync()) result.Add(MapOffering(reader));
        return result;
    }

    // Returns the ids of offerings that are NOT stale relative to a cutoff
    // date. An offering is "fresh" if its current usage_count is greater than
    // the snapshot for the cutoff date — i.e. it was hired at least once
    // during the lookback window. When no snapshot row exists for the cutoff
    // (historic data hasn't accumulated yet), the filter degrades to
    // "exclude offerings that have never been hired at all".
    public async Task<List<long>> ListFreshOfferingIdsAsync(string cutoffDate)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT o.id
            FROM offerings o
            LEFT JOIN agent_reputation_snapshots s
              ON s.offering_id = o.id AND s.snapshot_date = $cutoff
            WHERE
              (s.snapshot_date IS NOT NULL AND o.usage_count > s.usage_count)
              OR
              (s.snapshot_date IS NULL AND o.usage_count > 0);";
        cmd.Parameters.AddWithValue("$cutoff", cutoffDate);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<long>();
        while (await reader.ReadAsync()) result.Add(reader.GetInt64(0));
        return result;
    }

    public async Task<List<Offering>> ListNewSinceAsync(DateTime sinceUtc, int limit)
    {
        var sinceIso = sinceUtc.ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, agent_address, agent_name, offering_name, description,
                   requirement_schema_json, price_usdc, price_type, is_private,
                   chain, content_hash, first_seen_at, last_seen_at,
                   usage_count, agent_job_count, marketplace_version
            FROM offerings
            WHERE first_seen_at >= $since
            ORDER BY usage_count DESC, first_seen_at DESC
            LIMIT $lim;";
        cmd.Parameters.AddWithValue("$since", sinceIso);
        cmd.Parameters.AddWithValue("$lim", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Offering>();
        while (await reader.ReadAsync()) result.Add(MapOffering(reader));
        return result;
    }

    // Returns offerings whose usage_count grew most between the snapshot at
    // snapshotDate and the current row. Empty when no rows exist for that date.
    public async Task<List<OfferingGainer>> ListGainersAsync(string snapshotDate, int limit)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT o.id, o.agent_name, o.agent_address, o.offering_name,
                   s.usage_count AS hires_then,
                   o.usage_count AS hires_now,
                   o.marketplace_version
            FROM offerings o
            INNER JOIN agent_reputation_snapshots s
              ON s.offering_id = o.id AND s.snapshot_date = $d
            WHERE o.usage_count > s.usage_count
            ORDER BY (o.usage_count - s.usage_count) DESC
            LIMIT $lim;";
        cmd.Parameters.AddWithValue("$d", snapshotDate);
        cmd.Parameters.AddWithValue("$lim", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<OfferingGainer>();
        while (await reader.ReadAsync())
        {
            var hiresThen = reader.GetInt64(4);
            var hiresNow = reader.GetInt64(5);
            result.Add(new OfferingGainer(
                OfferingId: reader.GetInt64(0),
                AgentName: reader.GetString(1),
                AgentAddress: reader.GetString(2),
                OfferingName: reader.GetString(3),
                HiresThen: hiresThen,
                HiresNow: hiresNow,
                Delta: hiresNow - hiresThen,
                MarketplaceVersion: reader.GetString(6)));
        }
        return result;
    }

    public async Task<bool> SnapshotExistsAsync(string snapshotDate)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM agent_reputation_snapshots WHERE snapshot_date = $d LIMIT 1;";
        cmd.Parameters.AddWithValue("$d", snapshotDate);
        var exists = await cmd.ExecuteScalarAsync();
        return exists is not null;
    }

    public async Task<int> WriteSnapshotIfMissingAsync(string snapshotDate)
    {
        await using var conn = _db.OpenConnection();

        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM agent_reputation_snapshots WHERE snapshot_date = $d LIMIT 1;";
            check.Parameters.AddWithValue("$d", snapshotDate);
            var exists = await check.ExecuteScalarAsync();
            if (exists is not null) return 0;
        }

        // Single INSERT...SELECT runs in one statement so SQLite's implicit
        // transaction is sufficient. ~34K rows write in well under a second.
        await using var ins = conn.CreateCommand();
        ins.CommandText = @"
            INSERT INTO agent_reputation_snapshots
                (snapshot_date, offering_id, usage_count, agent_job_count)
            SELECT $d, id, usage_count, agent_job_count FROM offerings;";
        ins.Parameters.AddWithValue("$d", snapshotDate);
        return await ins.ExecuteNonQueryAsync();
    }

    public async Task<int> CountAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM offerings;";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<int> CountEmbeddedAsync(string model)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM offering_embeddings WHERE model = $m;";
        cmd.Parameters.AddWithValue("$m", model);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<string?> PickFirstAgentAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT agent_address FROM offerings LIMIT 1;";
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetString(0) : null;
    }

    public async Task<IReadOnlyDictionary<string, long>> SumJobCountsByAgentAsync()
    {
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        // agent_job_count is an agent-level metric duplicated across rows; MAX
        // is the canonical total (always equal across rows for the same agent
        // after a fresh indexer cycle).
        cmd.CommandText = @"
            SELECT agent_address, MAX(agent_job_count)
            FROM offerings
            GROUP BY agent_address;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            dict[reader.GetString(0)] = reader.GetInt64(1);
        }
        return dict;
    }

    private static Offering MapOffering(System.Data.Common.DbDataReader reader)
    {
        return new Offering(
            Id: reader.GetInt64(0),
            AgentAddress: reader.GetString(1),
            AgentName: reader.GetString(2),
            OfferingName: reader.GetString(3),
            Description: reader.GetString(4),
            RequirementSchemaJson: reader.IsDBNull(5) ? null : reader.GetString(5),
            PriceUsdc: reader.GetDouble(6),
            PriceType: reader.GetString(7),
            IsPrivate: reader.GetInt32(8) != 0,
            Chain: reader.GetString(9),
            ContentHash: reader.GetString(10),
            FirstSeenAt: DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastSeenAt: DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UsageCount: reader.GetInt64(13),
            AgentJobCount: reader.GetInt64(14),
            MarketplaceVersion: reader.GetString(15));
    }
}
