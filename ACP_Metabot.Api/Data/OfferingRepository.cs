using System.Globalization;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public record UpsertResult(long Id, bool IsNew, bool ContentChanged);

public record UpsertItem(
    string AgentAddress, string AgentName, string OfferingName,
    string Description, string? RequirementSchemaJson, double PriceUsdc,
    string PriceType, bool IsPrivate, string Chain, string ContentHash,
    long UsageCount, long AgentJobCount,
    string MarketplaceVersion = "v1",
    // v1.10 Phase 2 T3a: deliverable schema persisted alongside the
    // requirement schema. V2 sources populate this from
    // AcpAgentOffering.deliverable; V1 sources pass null.
    string? DeliverableSchemaJson = null);

public record UpsertSummary(int Added, int Updated, int Unchanged);

public class OfferingRepository
{
    private readonly Db _db;
    private readonly AgentProfileRepository? _agentProfiles;

    public OfferingRepository(Db db, AgentProfileRepository? agentProfiles = null)
    {
        _db = db;
        _agentProfiles = agentProfiles;
    }

    public async Task<UpsertResult> UpsertAsync(
        string agentAddress, string agentName, string offeringName,
        string description, string? requirementSchemaJson, double priceUsdc,
        string priceType, bool isPrivate, string chain, string contentHash,
        long usageCount, long agentJobCount, DateTime nowUtc,
        string marketplaceVersion = "v1",
        // v1.10 Phase 2 T3a: deliverable schema. Trails the existing
        // requirement parameter so any older caller that doesn't pass it
        // still compiles — both default to null (V1-source semantics).
        string? deliverableSchemaJson = null)
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
                    // Pure touch — no profile change, no dirty bump.
                    return new UpsertResult(existingId, IsNew: false, ContentChanged: false);
                }

                // Content changed — update fields
                await using var upd = conn.CreateCommand();
                upd.CommandText = @"
                    UPDATE offerings
                    SET agent_name              = $agentName,
                        description             = $desc,
                        requirement_schema_json = $schema,
                        deliverable_schema_json = $delSchema,
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
                upd.Parameters.AddWithValue("$delSchema", (object?)deliverableSchemaJson ?? DBNull.Value);
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

                // v1.10 Phase 2: refresh schema_facets for this offering. Schema
                // may have changed, so clear stale facets first then re-extract
                // from BOTH the requirement and deliverable schemas. Idempotent
                // via INSERT OR IGNORE on (offering_id, field_name, role). The
                // deleteFirst pass on the requirement call clears every facet
                // (irrespective of role), so the deliverable call deliberately
                // skips the delete to avoid wiping the requirement rows just
                // written.
                await WriteSchemaFacetsAsync(conn, tx: null, existingId,
                    requirementSchemaJson, role: "requirement", deleteFirst: true);
                await WriteSchemaFacetsAsync(conn, tx: null, existingId,
                    deliverableSchemaJson, role: "deliverable", deleteFirst: false);

                // Content changed — profile changed, bump dirty flag.
                if (_agentProfiles is not null)
                    await _agentProfiles.BumpLastChangeAtAsync(agentAddress);
                return new UpsertResult(existingId, IsNew: false, ContentChanged: true);
            }
        }

        // Insert
        await using var ins = conn.CreateCommand();
        ins.CommandText = @"
            INSERT INTO offerings (
                agent_address, agent_name, offering_name, description,
                requirement_schema_json, deliverable_schema_json,
                price_usdc, price_type, is_private,
                chain, content_hash, first_seen_at, last_seen_at,
                usage_count, agent_job_count, marketplace_version)
            VALUES (
                $a, $agentName, $n, $desc,
                $schema, $delSchema,
                $price, $pType, $priv,
                $chain, $hash, $now, $now,
                $usage, $agentJobs, $mv);
            SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$a", agentAddress);
        ins.Parameters.AddWithValue("$agentName", agentName);
        ins.Parameters.AddWithValue("$n", offeringName);
        ins.Parameters.AddWithValue("$desc", description);
        ins.Parameters.AddWithValue("$schema", (object?)requirementSchemaJson ?? DBNull.Value);
        ins.Parameters.AddWithValue("$delSchema", (object?)deliverableSchemaJson ?? DBNull.Value);
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

        // v1.10 Phase 2: extract + persist schema_facets for the freshly
        // inserted offering across BOTH roles. No deleteFirst on either —
        // there's nothing to clean up on the insert path.
        await WriteSchemaFacetsAsync(conn, tx: null, newId,
            requirementSchemaJson, role: "requirement", deleteFirst: false);
        await WriteSchemaFacetsAsync(conn, tx: null, newId,
            deliverableSchemaJson, role: "deliverable", deleteFirst: false);

        // New offering inserted — profile changed, bump dirty flag.
        if (_agentProfiles is not null)
            await _agentProfiles.BumpLastChangeAtAsync(agentAddress);
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
        // (id, content_hash, is_removed). Composite key matches the v1.3 UNIQUE
        // constraint so v1 and v2 rows for the same (addr, name) don't collide.
        // is_removed is needed so we can detect touch-reactivations (where the
        // content_hash is unchanged but a tombstoned offering reappears).
        var existing = new Dictionary<string, (long Id, string Hash, bool IsRemoved)>(
            capacity: items.Count, StringComparer.Ordinal);
        await using (var pre = conn.CreateCommand())
        {
            pre.CommandText = "SELECT marketplace_version, agent_address, offering_name, id, content_hash, is_removed FROM offerings;";
            await using var reader = await pre.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var key = reader.GetString(0) + "|" + reader.GetString(1) + "|" + reader.GetString(2);
                existing[key] = (reader.GetInt64(3), reader.GetString(4), reader.GetInt32(5) != 0);
            }
        }

        await using var tx = conn.BeginTransaction();

        // Touch (content unchanged) — reactivate any tombstoned row at the same
        // time. A reappearance in upstream is the strongest possible signal
        // that the seller put it back.
        await using var touch = conn.CreateCommand();
        touch.Transaction = tx;
        touch.CommandText = @"
            UPDATE offerings
            SET last_seen_at    = $now,
                usage_count     = $usage,
                agent_job_count = $agentJobs,
                is_removed      = 0,
                removed_at      = NULL
            WHERE id = $id;";
        var tNow = touch.Parameters.Add("$now", SqliteType.Text);
        tNow.Value = nowIso;
        var tUsage = touch.Parameters.Add("$usage", SqliteType.Integer);
        var tAgentJobs = touch.Parameters.Add("$agentJobs", SqliteType.Integer);
        var tId = touch.Parameters.Add("$id", SqliteType.Integer);

        // Update (content changed) — same reactivation, plus we drop any
        // existing embedding so the next indexer cycle re-embeds the new
        // description. Without this, BM25 would reflect the new text but
        // the dense corpus would still rank against the stale embedding.
        await using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = @"
            UPDATE offerings
            SET agent_name              = $agentName,
                description             = $desc,
                requirement_schema_json = $schema,
                deliverable_schema_json = $delSchema,
                price_usdc              = $price,
                price_type              = $pType,
                is_private              = $priv,
                chain                   = $chain,
                content_hash            = $hash,
                last_seen_at            = $now,
                usage_count             = $usage,
                agent_job_count         = $agentJobs,
                is_removed              = 0,
                removed_at              = NULL
            WHERE id = $id;";

        // Embedding invalidation — runs alongside `upd` whenever the content
        // hash changes. ON DELETE CASCADE on the offering row would have
        // handled this for outright deletes; an UPDATE has to do it manually.
        await using var delEmb = conn.CreateCommand();
        delEmb.Transaction = tx;
        delEmb.CommandText = "DELETE FROM offering_embeddings WHERE offering_id = $id;";
        var dEmbId = delEmb.Parameters.Add("$id", SqliteType.Integer);
        var uAgentName = upd.Parameters.Add("$agentName", SqliteType.Text);
        var uDesc = upd.Parameters.Add("$desc", SqliteType.Text);
        var uSchema = upd.Parameters.Add("$schema", SqliteType.Text);
        // v1.10 Phase 2 T3a: deliverable schema slot on the bulk UPDATE.
        var uDelSchema = upd.Parameters.Add("$delSchema", SqliteType.Text);
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
                requirement_schema_json, deliverable_schema_json,
                price_usdc, price_type, is_private,
                chain, content_hash, first_seen_at, last_seen_at,
                usage_count, agent_job_count, marketplace_version)
            VALUES (
                $a, $agentName, $n, $desc,
                $schema, $delSchema,
                $price, $pType, $priv,
                $chain, $hash, $now, $now,
                $usage, $agentJobs, $mv);
            SELECT last_insert_rowid();";
        var iA = ins.Parameters.Add("$a", SqliteType.Text);
        var iAgentName = ins.Parameters.Add("$agentName", SqliteType.Text);
        var iN = ins.Parameters.Add("$n", SqliteType.Text);
        var iDesc = ins.Parameters.Add("$desc", SqliteType.Text);
        var iSchema = ins.Parameters.Add("$schema", SqliteType.Text);
        // v1.10 Phase 2 T3a: deliverable schema slot on the bulk INSERT.
        var iDelSchema = ins.Parameters.Add("$delSchema", SqliteType.Text);
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

        // v1.10 Phase 2: facet write path. Two prepared commands reused per
        // item — DELETE stale facets on the update path, INSERT OR IGNORE
        // each extracted field name. Scoped to tx so a rollback discards the
        // facet writes too. Same idempotency guarantee as the boot-time
        // backfill (T3b) via the UNIQUE(offering_id, field_name, role)
        // constraint on schema_facets. T3a generalises the INSERT to bind
        // role as a parameter so the same prepared command serves both
        // requirement and deliverable facets.
        await using var delFacets = conn.CreateCommand();
        delFacets.Transaction = tx;
        delFacets.CommandText = "DELETE FROM schema_facets WHERE offering_id = $id;";
        var dfId = delFacets.Parameters.Add("$id", SqliteType.Integer);

        await using var insFacets = conn.CreateCommand();
        insFacets.Transaction = tx;
        insFacets.CommandText = @"
            INSERT OR IGNORE INTO schema_facets(offering_id, field_name, role)
            VALUES ($id, $name, $role);";
        var ifId = insFacets.Parameters.Add("$id", SqliteType.Integer);
        var ifName = insFacets.Parameters.Add("$name", SqliteType.Text);
        var ifRole = insFacets.Parameters.Add("$role", SqliteType.Text);

        // Collect agent addresses whose profiles genuinely changed so we can
        // bump agent_profiles.last_change_at after the transaction commits.
        // Pure touch (same content_hash, not previously tombstoned) is NOT a
        // profile change — applying the v1.2 trigger-storm lesson at the app level.
        var profileChanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    // Touch-reactivation: offering was tombstoned but reappeared
                    // with the same content_hash — that IS a profile change.
                    if (ex.IsRemoved)
                        profileChanged.Add(item.AgentAddress.ToLowerInvariant());
                }
                else
                {
                    uAgentName.Value = item.AgentName;
                    uDesc.Value = item.Description;
                    uSchema.Value = (object?)item.RequirementSchemaJson ?? DBNull.Value;
                    uDelSchema.Value = (object?)item.DeliverableSchemaJson ?? DBNull.Value;
                    uPrice.Value = item.PriceUsdc;
                    uPType.Value = item.PriceType;
                    uPriv.Value = item.IsPrivate ? 1 : 0;
                    uChain.Value = item.Chain;
                    uHash.Value = item.ContentHash;
                    uUsage.Value = item.UsageCount;
                    uAgentJobs.Value = item.AgentJobCount;
                    uId.Value = ex.Id;
                    await upd.ExecuteNonQueryAsync();
                    // Drop the old embedding so EmbedPendingAsync re-fires
                    // with the new description on the next indexer tick.
                    dEmbId.Value = ex.Id;
                    await delEmb.ExecuteNonQueryAsync();
                    // v1.10 Phase 2: same lifecycle for schema_facets — clear
                    // stale facets and re-extract from BOTH the (possibly
                    // renamed) requirement schema AND the deliverable schema.
                    // Single DELETE up front since the constraint covers both
                    // roles via UNIQUE(offering_id, field_name, role).
                    dfId.Value = ex.Id;
                    await delFacets.ExecuteNonQueryAsync();
                    ifId.Value = ex.Id;
                    var updReqFacets = SchemaFacetExtractor.Extract(item.RequirementSchemaJson);
                    if (updReqFacets.Count > 0)
                    {
                        ifRole.Value = "requirement";
                        foreach (var n in updReqFacets)
                        {
                            ifName.Value = n;
                            await insFacets.ExecuteNonQueryAsync();
                        }
                    }
                    var updDelFacets = SchemaFacetExtractor.Extract(item.DeliverableSchemaJson);
                    if (updDelFacets.Count > 0)
                    {
                        ifRole.Value = "deliverable";
                        foreach (var n in updDelFacets)
                        {
                            ifName.Value = n;
                            await insFacets.ExecuteNonQueryAsync();
                        }
                    }
                    updated++;
                    // Mirrored fields changed — profile changed.
                    profileChanged.Add(item.AgentAddress.ToLowerInvariant());
                }
            }
            else
            {
                iA.Value = item.AgentAddress;
                iAgentName.Value = item.AgentName;
                iN.Value = item.OfferingName;
                iDesc.Value = item.Description;
                iSchema.Value = (object?)item.RequirementSchemaJson ?? DBNull.Value;
                iDelSchema.Value = (object?)item.DeliverableSchemaJson ?? DBNull.Value;
                iPrice.Value = item.PriceUsdc;
                iPType.Value = item.PriceType;
                iPriv.Value = item.IsPrivate ? 1 : 0;
                iChain.Value = item.Chain;
                iHash.Value = item.ContentHash;
                iUsage.Value = item.UsageCount;
                iAgentJobs.Value = item.AgentJobCount;
                iMv.Value = item.MarketplaceVersion;
                var newId = (long)(await ins.ExecuteScalarAsync() ?? 0L);
                // v1.10 Phase 2: persist schema facets for the new offering
                // across BOTH roles. No DELETE — nothing to clean up on the
                // fresh-insert path.
                ifId.Value = newId;
                var insReqFacets = SchemaFacetExtractor.Extract(item.RequirementSchemaJson);
                if (insReqFacets.Count > 0)
                {
                    ifRole.Value = "requirement";
                    foreach (var n in insReqFacets)
                    {
                        ifName.Value = n;
                        await insFacets.ExecuteNonQueryAsync();
                    }
                }
                var insDelFacets = SchemaFacetExtractor.Extract(item.DeliverableSchemaJson);
                if (insDelFacets.Count > 0)
                {
                    ifRole.Value = "deliverable";
                    foreach (var n in insDelFacets)
                    {
                        ifName.Value = n;
                        await insFacets.ExecuteNonQueryAsync();
                    }
                }
                added++;
                // New offering — profile changed.
                profileChanged.Add(item.AgentAddress.ToLowerInvariant());
            }
        }

        await tx.CommitAsync();

        // Bump dirty flag for each agent whose offering set changed.
        // Runs after commit so the bump is never rolled back with the tx.
        if (_agentProfiles is not null)
        {
            foreach (var addr in profileChanged)
                await _agentProfiles.BumpLastChangeAtAsync(addr);
        }

        return new UpsertSummary(added, updated, unchanged);
    }

    /// <summary>
    /// v1.10 Phase 2: extract top-level field names from a schema (either
    /// the requirement schema or the deliverable schema, distinguished by
    /// <paramref name="role"/>) and persist them into <c>schema_facets</c>
    /// for the given offering id. Idempotent via the
    /// UNIQUE(offering_id, field_name, role) constraint — the INSERT OR IGNORE
    /// on this table is what makes the boot-time backfill (T3b) and
    /// steady-state UpsertAsync writes safely co-exist without dedupe logic
    /// at the caller.
    ///
    /// Set <paramref name="deleteFirst"/> on the content-changed update path
    /// so stale field names from a renamed property don't survive the schema
    /// rewrite. On a fresh insert there's nothing to clean up. The delete is
    /// scoped to the OFFERING (not by role) because update calls clear all
    /// facets up-front and re-insert both roles back-to-back — splitting the
    /// delete per role would require two passes against the same index.
    ///
    /// <paramref name="role"/> must be either <c>"requirement"</c> or
    /// <c>"deliverable"</c> — the CHECK constraint on schema_facets enforces
    /// the same.
    /// </summary>
    private static async Task WriteSchemaFacetsAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        long offeringId, string? schemaJson, string role, bool deleteFirst)
    {
        if (deleteFirst)
        {
            await using var del = conn.CreateCommand();
            if (tx is not null) del.Transaction = tx;
            del.CommandText = "DELETE FROM schema_facets WHERE offering_id = $id;";
            del.Parameters.AddWithValue("$id", offeringId);
            await del.ExecuteNonQueryAsync();
        }

        var facets = SchemaFacetExtractor.Extract(schemaJson);
        if (facets.Count == 0) return;

        await using var ins = conn.CreateCommand();
        if (tx is not null) ins.Transaction = tx;
        ins.CommandText = @"
            INSERT OR IGNORE INTO schema_facets(offering_id, field_name, role)
            VALUES ($id, $name, $role);";
        var idP = ins.Parameters.Add("$id", SqliteType.Integer);
        var nameP = ins.Parameters.Add("$name", SqliteType.Text);
        var roleP = ins.Parameters.Add("$role", SqliteType.Text);
        idP.Value = offeringId;
        roleP.Value = role;
        foreach (var n in facets)
        {
            nameP.Value = n;
            await ins.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Marks rows as removed when their <c>last_seen_at</c> is older than
    /// <paramref name="staleCutoffUtc"/>, scoped to a single marketplace
    /// version. Returns the number of rows newly tombstoned.
    ///
    /// Reactivation happens automatically: the next time the row reappears in
    /// an upstream fetch, <see cref="UpsertManyAsync"/>'s touch / update path
    /// resets <c>is_removed = 0</c>.
    ///
    /// Caller (the indexer) is expected to skip this whenever a source's
    /// fetch failed or returned an abnormally small result set, to avoid mass
    /// tombstoning on transient upstream outages.
    /// </summary>
    public async Task<int> MarkStaleAsRemovedAsync(string marketplaceVersion,
        DateTime staleCutoffUtc, DateTime nowUtc)
    {
        var cutoff = staleCutoffUtc.ToString("O", CultureInfo.InvariantCulture);
        var nowIso = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();

        // Collect the distinct agent addresses that are about to be tombstoned
        // so we can bump their dirty flags after the UPDATE commits.
        var tombstonedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_agentProfiles is not null)
        {
            await using var sel = conn.CreateCommand();
            sel.CommandText = @"
                SELECT DISTINCT agent_address
                FROM offerings
                WHERE marketplace_version = $mv
                  AND is_removed = 0
                  AND last_seen_at < $cutoff;";
            sel.Parameters.AddWithValue("$mv", marketplaceVersion);
            sel.Parameters.AddWithValue("$cutoff", cutoff);
            await using var reader = await sel.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tombstonedAgents.Add(reader.GetString(0).ToLowerInvariant());
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE offerings
            SET is_removed = 1, removed_at = $now
            WHERE marketplace_version = $mv
              AND is_removed = 0
              AND last_seen_at < $cutoff;";
        cmd.Parameters.AddWithValue("$mv", marketplaceVersion);
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.Parameters.AddWithValue("$now", nowIso);
        var rowsAffected = await cmd.ExecuteNonQueryAsync();

        // Bump dirty flag for each agent whose offering set shrank.
        foreach (var addr in tombstonedAgents)
            await _agentProfiles!.BumpLastChangeAtAsync(addr);

        return rowsAffected;
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
                   requirement_schema_json, deliverable_schema_json,
                   price_usdc, price_type, is_private,
                   chain, content_hash, first_seen_at, last_seen_at,
                   usage_count, agent_job_count, marketplace_version,
                   is_removed, removed_at
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
                   o.requirement_schema_json, o.deliverable_schema_json,
                   o.price_usdc, o.price_type, o.is_private,
                   o.chain, o.content_hash, o.first_seen_at, o.last_seen_at,
                   o.usage_count, o.agent_job_count, o.marketplace_version,
                   o.is_removed, o.removed_at
            FROM offerings o
            LEFT JOIN offering_embeddings e ON e.offering_id = o.id
            WHERE o.is_removed = 0
              AND (e.offering_id IS NULL
                   OR e.dimension != $d
                   OR e.model != $m)
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
                   o.requirement_schema_json, o.deliverable_schema_json,
                   o.price_usdc, o.price_type, o.is_private,
                   o.chain, o.content_hash, o.first_seen_at, o.last_seen_at,
                   o.usage_count, o.agent_job_count, o.marketplace_version,
                   o.is_removed, o.removed_at,
                   e.dimension, e.embedding_blob
            FROM offerings o
            INNER JOIN offering_embeddings e ON e.offering_id = o.id
            WHERE e.model = $m
              AND o.is_removed = 0;";
        cmd.Parameters.AddWithValue("$m", requiredModel);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<(Offering, float[])>();
        while (await reader.ReadAsync())
        {
            var o = MapOffering(reader);
            // Embedding cols sit AFTER the v1.5 tombstone cols. v1.10 Phase 2
            // T3a inserted deliverable_schema_json between requirement_schema_json
            // and price_usdc, shifting every offering col +1 → embedding cols
            // moved from 18/19 to 19/20.
            var dim = reader.GetInt32(19);
            var blob = (byte[])reader[20];
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
    /// Agent-level search: groups offering-level BM25 hits by agent and
    /// returns the top-N agents, each with their best BM25 score, total
    /// number of active offerings, and up to three matching offering names
    /// for context. Distinct from <see cref="SearchBm25Async"/> which
    /// returns offering-level rankings.
    /// </summary>
    public async Task<IReadOnlyList<AgentSearchHit>> SearchAgentsAsync(
        string query, int limit, string? marketplaceFilter)
    {
        var match = SanitizeFtsQuery(query);
        if (match is null) return Array.Empty<AgentSearchHit>();

        // Wide net for the grouping pass — we need enough hits per agent to
        // get a representative top-3 offerings list. 200 candidates is cheap
        // because we never embed and the FTS index is small.
        const int FtsHitPoolSize = 200;

        try
        {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            var sql = @"
                SELECT o.agent_address, o.agent_name, o.offering_name,
                       o.usage_count, o.agent_job_count,
                       bm25(offerings_fts, 3.0, 2.0, 1.0) AS bm,
                       o.marketplace_version
                FROM offerings_fts
                JOIN offerings o ON o.id = offerings_fts.rowid
                WHERE offerings_fts MATCH $q
                  AND o.is_removed = 0";
            if (marketplaceFilter is not null)
            {
                sql += " AND o.marketplace_version = $mv";
            }
            sql += " ORDER BY bm LIMIT $lim;";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$q", match);
            cmd.Parameters.AddWithValue("$lim", FtsHitPoolSize);
            if (marketplaceFilter is not null)
                cmd.Parameters.AddWithValue("$mv", marketplaceFilter);

            // BM25 in SQLite is "lower = better" (negative-leaning). Group by
            // agent, keep the best (lowest) bm score, count hits, and the
            // first-3 offering names in BM25 order.
            var byAgent = new Dictionary<string,
                (string Name, double BestBm, int Hits, List<string> Offerings, long Jobs)>(
                StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var addr = reader.GetString(0);
                var name = reader.GetString(1);
                var offering = reader.GetString(2);
                var jobs = reader.GetInt64(4);
                var bm = reader.GetDouble(5);
                if (!byAgent.TryGetValue(addr, out var ex))
                {
                    byAgent[addr] = (name, bm, 1, new List<string> { offering }, jobs);
                }
                else
                {
                    if (bm < ex.BestBm) ex.BestBm = bm;
                    ex.Hits += 1;
                    if (ex.Offerings.Count < 3) ex.Offerings.Add(offering);
                    if (jobs > ex.Jobs) ex.Jobs = jobs;
                    byAgent[addr] = ex;
                }
            }

            if (byAgent.Count == 0) return Array.Empty<AgentSearchHit>();

            // Total active offerings per surviving agent (independent of the
            // FTS query — "the agent has 12 offerings, 4 of which match").
            var addrs = byAgent.Keys.ToList();
            var totalOffersByAgent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await using (var counter = conn.CreateCommand())
            {
                // IN clause via parameter list. Fine up to limit*N ~ 50; any
                // larger and we'd switch to a temp table.
                var paramNames = new List<string>();
                for (int i = 0; i < addrs.Count; i++)
                {
                    var p = "$a" + i;
                    paramNames.Add(p);
                    counter.Parameters.AddWithValue(p, addrs[i]);
                }
                var inList = string.Join(",", paramNames);
                counter.CommandText = $@"
                    SELECT agent_address, COUNT(*)
                    FROM offerings
                    WHERE is_removed = 0
                      AND agent_address IN ({inList})";
                if (marketplaceFilter is not null)
                {
                    counter.CommandText += " AND marketplace_version = $mv";
                    counter.Parameters.AddWithValue("$mv", marketplaceFilter);
                }
                counter.CommandText += " GROUP BY agent_address;";
                await using var counterReader = await counter.ExecuteReaderAsync();
                while (await counterReader.ReadAsync())
                {
                    totalOffersByAgent[counterReader.GetString(0)] = (int)counterReader.GetInt64(1);
                }
            }

            return byAgent
                .OrderByDescending(kv => kv.Value.Hits)
                .ThenBy(kv => kv.Value.BestBm)
                .Take(limit)
                .Select(kv => new AgentSearchHit(
                    AgentAddress: kv.Key,
                    AgentName: kv.Value.Name,
                    // Map BM25 (lower=better, often negative) to [0,1] where 1
                    // is most relevant. abs() since bm is typically <= 0.
                    Score: 1.0 / (1.0 + Math.Abs(kv.Value.BestBm)),
                    TotalOfferings: totalOffersByAgent.TryGetValue(kv.Key, out var c) ? c : kv.Value.Hits,
                    // v1.7: wrap legacy string list as minimal AgentSearchHitOffering records.
                    // Phase 5.2 AgentSearchService will populate price/version properly.
                    TopOfferings: kv.Value.Offerings
                        .Select(name => new AgentSearchHitOffering(name, 0.0, ""))
                        .ToArray(),
                    TotalJobs: kv.Value.Jobs,
                    TopOfferingNames: kv.Value.Offerings,
                    Marketplaces: Array.Empty<string>(),
                    DominantMarketplace: "none",
                    AgentScore: null))
                .ToArray();
        }
        catch (SqliteException)
        {
            return Array.Empty<AgentSearchHit>();
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

    /// <summary>
    /// Active rows only (is_removed = 0). Used by the reputation rebuild and
    /// the search corpus refresh; both want a "currently on the marketplace"
    /// view. For tombstoned + active rows together, see ListAllIncludingRemovedAsync.
    /// </summary>
    public async Task<List<Offering>> ListAllAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, agent_address, agent_name, offering_name, description,
                   requirement_schema_json, deliverable_schema_json,
                   price_usdc, price_type, is_private,
                   chain, content_hash, first_seen_at, last_seen_at,
                   usage_count, agent_job_count, marketplace_version,
                   is_removed, removed_at
            FROM offerings
            WHERE is_removed = 0;";
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<Offering>();
        while (await reader.ReadAsync()) result.Add(MapOffering(reader));
        return result;
    }

    /// <summary>
    /// Active rows only for the given agent. Removed (tombstoned) offerings
    /// are filtered out so /v1/agent/{address} doesn't surface listings the
    /// agent has taken down.
    /// </summary>
    public async Task<List<Offering>> ListByAgentAsync(string agentAddress)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, agent_address, agent_name, offering_name, description,
                   requirement_schema_json, deliverable_schema_json,
                   price_usdc, price_type, is_private,
                   chain, content_hash, first_seen_at, last_seen_at,
                   usage_count, agent_job_count, marketplace_version,
                   is_removed, removed_at
            FROM offerings
            WHERE agent_address = $a
              AND is_removed = 0;";
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
              o.is_removed = 0
              AND (
                (s.snapshot_date IS NOT NULL AND o.usage_count > s.usage_count)
                OR
                (s.snapshot_date IS NULL AND o.usage_count > 0)
              );";
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
                   requirement_schema_json, deliverable_schema_json,
                   price_usdc, price_type, is_private,
                   chain, content_hash, first_seen_at, last_seen_at,
                   usage_count, agent_job_count, marketplace_version,
                   is_removed, removed_at
            FROM offerings
            WHERE first_seen_at >= $since
              AND is_removed = 0
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
              AND o.is_removed = 0
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

    /// <summary>
    /// Distinct agent addresses with at least one non-tombstoned offering seen
    /// in the last <paramref name="windowDays"/>. Used by cross-bot consumers
    /// (notably ACP_ChainlinkBot) to know which agents to score on-chain.
    /// Lowercased, deduplicated, ordered by address for stable output.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListActiveAgentAddressesAsync(
        int windowDays, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-windowDays).ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT LOWER(agent_address)
            FROM offerings
            WHERE is_removed = 0
              AND last_seen_at >= $cutoff
            ORDER BY 1;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    // ── Digest helpers (v1.7) ─────────────────────────────────────────────────

    /// <summary>
    /// Returns total count of new agents whose first offering was seen after
    /// <paramref name="sinceUtc"/>, plus the top <paramref name="topLimit"/>
    /// agents ordered by first_seen_at DESC.
    /// </summary>
    public async Task<(int TotalCount, IReadOnlyList<NewAgentRow> Top)> ListNewAgentsSinceAsync(
        DateTime sinceUtc, string? marketplaceFilter, HashSet<string>? chainFilter,
        double? priceMaxUsdc, int topLimit = 10)
    {
        var sinceIso = sinceUtc.ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();

        // Count total distinct new agents in window
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = BuildNewAgentsCountSql(marketplaceFilter, chainFilter, priceMaxUsdc);
        countCmd.Parameters.AddWithValue("$since", sinceIso);
        if (marketplaceFilter is not null) countCmd.Parameters.AddWithValue("$mv", marketplaceFilter);
        if (priceMaxUsdc is double pm) countCmd.Parameters.AddWithValue("$pmax", pm);
        var countObj = await countCmd.ExecuteScalarAsync();
        var total = Convert.ToInt32(countObj ?? 0);

        // Fetch top rows
        await using var topCmd = conn.CreateCommand();
        topCmd.CommandText = BuildNewAgentsTopSql(marketplaceFilter, chainFilter, priceMaxUsdc);
        topCmd.Parameters.AddWithValue("$since", sinceIso);
        topCmd.Parameters.AddWithValue("$lim", topLimit);
        if (marketplaceFilter is not null) topCmd.Parameters.AddWithValue("$mv", marketplaceFilter);
        if (priceMaxUsdc is double pm2) topCmd.Parameters.AddWithValue("$pmax", pm2);
        await using var reader = await topCmd.ExecuteReaderAsync();
        var rows = new List<NewAgentRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new NewAgentRow(
                Address: reader.GetString(0),
                Name: reader.GetString(1),
                Marketplace: reader.GetString(2),
                FirstSeenAt: reader.GetString(3),
                OfferingCount: reader.GetInt32(4)));
        }
        return (total, rows);
    }

    private static string BuildNewAgentsCountSql(string? mv, HashSet<string>? chain, double? price)
    {
        var where = BuildNewAgentsWhere(mv, chain, price);
        return $@"
            SELECT COUNT(DISTINCT agent_address)
            FROM offerings
            WHERE first_seen_at >= $since
              AND is_removed = 0
              {where};";
    }

    private static string BuildNewAgentsTopSql(string? mv, HashSet<string>? chain, double? price)
    {
        var where = BuildNewAgentsWhere(mv, chain, price);
        return $@"
            SELECT
                agent_address,
                MAX(agent_name)   AS agent_name,
                marketplace_version,
                MIN(first_seen_at) AS first_seen,
                COUNT(*)           AS offering_count
            FROM offerings
            WHERE first_seen_at >= $since
              AND is_removed = 0
              {where}
            GROUP BY agent_address
            HAVING MIN(first_seen_at) >= $since
            ORDER BY first_seen DESC
            LIMIT $lim;";
    }

    private static string BuildNewAgentsWhere(string? mv, HashSet<string>? chain, double? price)
    {
        var sb = new System.Text.StringBuilder();
        if (mv is not null) sb.Append("AND marketplace_version = $mv ");
        if (chain is { Count: > 0 })
        {
            var inList = string.Join(",", chain.Select(c => $"'{c.Replace("'", "''")}'"));
            sb.Append($"AND LOWER(chain) IN ({inList}) ");
        }
        if (price is not null) sb.Append("AND price_usdc <= $pmax ");
        return sb.ToString();
    }

    /// <summary>
    /// Computes churn: agents that had ≥1 active offering at window start but
    /// have 0 active offerings now.
    /// </summary>
    public async Task<(int Baseline, int Churned)> ComputeChurnAsync(
        DateTime windowStartUtc, string? marketplace, HashSet<string>? chainFilter, double? priceMaxUsdc)
    {
        var windowStartIso = windowStartUtc.ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();

        // Baseline: agents with ≥1 offering active at windowStart (seen before + not yet removed)
        await using var baseCmd = conn.CreateCommand();
        baseCmd.CommandText = @"
            SELECT DISTINCT agent_address FROM offerings
            WHERE first_seen_at <= $ws
              AND (is_removed = 0 OR removed_at > $ws);";
        baseCmd.Parameters.AddWithValue("$ws", windowStartIso);
        var baselineAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var br = await baseCmd.ExecuteReaderAsync();
        while (await br.ReadAsync()) baselineAgents.Add(br.GetString(0));

        // Currently active agents (has ≥1 is_removed=0 offering)
        await using var activeCmd = conn.CreateCommand();
        activeCmd.CommandText = @"
            SELECT DISTINCT agent_address FROM offerings
            WHERE is_removed = 0;";
        var activeAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var ar = await activeCmd.ExecuteReaderAsync();
        while (await ar.ReadAsync()) activeAgents.Add(ar.GetString(0));

        // Apply optional filters to restrict which agents are considered
        // (chain/price filtering on baseline — use active offerings for address set)
        // For simplicity, filters scope the baseline set by intersection with
        // agents that have at least one offering matching the filter criteria.
        if (marketplace is not null || chainFilter is not null || priceMaxUsdc is not null)
        {
            await using var filterCmd = conn.CreateCommand();
            var chainIn = chainFilter is { Count: > 0 }
                ? string.Join(",", chainFilter.Select(c => $"'{c.Replace("'", "''")}'"))
                : null;
            var fWhere = new System.Text.StringBuilder("WHERE is_removed = 0 ");
            if (marketplace is not null) fWhere.Append("AND marketplace_version = $mv ");
            if (chainIn is not null) fWhere.Append($"AND LOWER(chain) IN ({chainIn}) ");
            if (priceMaxUsdc is not null) fWhere.Append("AND price_usdc <= $pmax ");
            filterCmd.CommandText = $"SELECT DISTINCT agent_address FROM offerings {fWhere};";
            if (marketplace is not null) filterCmd.Parameters.AddWithValue("$mv", marketplace);
            if (priceMaxUsdc is double pm) filterCmd.Parameters.AddWithValue("$pmax", pm);
            var filteredAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var fr = await filterCmd.ExecuteReaderAsync();
            while (await fr.ReadAsync()) filteredAgents.Add(fr.GetString(0));
            baselineAgents.IntersectWith(filteredAgents);
        }

        var churned = baselineAgents.Count(a => !activeAgents.Contains(a));
        return (baselineAgents.Count, churned);
    }

    // ── Cohort Survival helpers ───────────────────────────────────────────────

    public record CohortBucket(
        string WeekIso, DateTime WeekStart, DateTime WeekEnd, int Size);

    /// <summary>
    /// Returns per ISO-week buckets for agents whose first offering appeared in the window.
    /// </summary>
    public async Task<IReadOnlyList<CohortBucket>> ListCohortBucketsAsync(
        DateTime sinceUtc, string? marketplace, HashSet<string>? chainFilter, double? priceMaxUsdc)
    {
        var sinceIso = sinceUtc.ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();

        var chainIn = chainFilter is { Count: > 0 }
            ? string.Join(",", chainFilter.Select(c => $"'{c.Replace("'", "''")}'"))
            : null;
        var where = new System.Text.StringBuilder("WHERE is_removed = 0 AND first_seen_at >= $since ");
        if (marketplace is not null) where.Append("AND marketplace_version = $mv ");
        if (chainIn is not null) where.Append($"AND LOWER(chain) IN ({chainIn}) ");
        if (priceMaxUsdc is not null) where.Append("AND price_usdc <= $pmax ");

        cmd.CommandText = $@"
            SELECT agent_address, MIN(first_seen_at) AS first_seen
            FROM offerings
            {where}
            GROUP BY agent_address
            HAVING MIN(first_seen_at) >= $since
            ORDER BY first_seen ASC;";
        cmd.Parameters.AddWithValue("$since", sinceIso);
        if (marketplace is not null) cmd.Parameters.AddWithValue("$mv", marketplace);
        if (priceMaxUsdc is double pm) cmd.Parameters.AddWithValue("$pmax", pm);

        var agents = new List<(string addr, DateTime firstSeen)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var fs = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            agents.Add((reader.GetString(0), fs));
        }

        // Group by ISO week
        var byWeek = new Dictionary<string, (DateTime WeekStart, int Count)>(StringComparer.Ordinal);
        foreach (var (_, firstSeen) in agents)
        {
            var year = ISOWeek.GetYear(firstSeen);
            var week = ISOWeek.GetWeekOfYear(firstSeen);
            var key = $"{year}-W{week:D2}";
            var weekStart = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
            if (byWeek.TryGetValue(key, out var existing))
                byWeek[key] = (existing.WeekStart, existing.Count + 1);
            else
                byWeek[key] = (weekStart, 1);
        }

        return byWeek
            .OrderBy(kv => kv.Value.WeekStart)
            .Select(kv => new CohortBucket(
                kv.Key,
                kv.Value.WeekStart,
                kv.Value.WeekStart.AddDays(7),
                kv.Value.Count))
            .ToList();
    }

    /// <summary>
    /// Counts agents in the cohort [weekStart, weekEnd) that are still "surviving":
    /// has ≥1 currently active offering (is_removed = 0).
    /// </summary>
    public async Task<int> CountSurvivingInCohortAsync(
        DateTime weekStart, DateTime weekEnd,
        string? marketplace, HashSet<string>? chainFilter, double? priceMaxUsdc)
    {
        var wsIso = weekStart.ToString("O", CultureInfo.InvariantCulture);
        var weIso = weekEnd.ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();

        // Get agents first seen in cohort week
        await using var cohortCmd = conn.CreateCommand();
        cohortCmd.CommandText = @"
            SELECT DISTINCT agent_address FROM offerings
            WHERE first_seen_at >= $ws AND first_seen_at < $we;";
        cohortCmd.Parameters.AddWithValue("$ws", wsIso);
        cohortCmd.Parameters.AddWithValue("$we", weIso);
        var cohortAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cr = await cohortCmd.ExecuteReaderAsync();
        while (await cr.ReadAsync()) cohortAgents.Add(cr.GetString(0));

        if (cohortAgents.Count == 0) return 0;

        // Of those, count how many have ≥1 active offering today
        await using var activeCmd = conn.CreateCommand();
        activeCmd.CommandText = @"
            SELECT DISTINCT agent_address FROM offerings
            WHERE is_removed = 0;";
        var activeAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var ar = await activeCmd.ExecuteReaderAsync();
        while (await ar.ReadAsync()) activeAgents.Add(ar.GetString(0));

        return cohortAgents.Count(a => activeAgents.Contains(a));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static Offering MapOffering(System.Data.Common.DbDataReader reader)
    {
        // Optional tombstone columns near the tail of the projection. Present
        // in every SELECT below. v1.10 Phase 2 T3a inserted
        // deliverable_schema_json at index 6, shifting every column from
        // price_usdc (was 6, now 7) onward by +1.
        //
        // The FieldCount guard is preserved for legacy callers that read the
        // pre-v1.5 16-column shape, but every shipping SELECT now projects
        // through removed_at — the guards still hold even at the new offsets.
        bool isRemoved = false;
        DateTime? removedAt = null;
        if (reader.FieldCount > 17)
        {
            if (!reader.IsDBNull(17)) isRemoved = reader.GetInt32(17) != 0;
            if (reader.FieldCount > 18 && !reader.IsDBNull(18))
                removedAt = DateTime.Parse(reader.GetString(18),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        return new Offering(
            Id: reader.GetInt64(0),
            AgentAddress: reader.GetString(1),
            AgentName: reader.GetString(2),
            OfferingName: reader.GetString(3),
            Description: reader.GetString(4),
            RequirementSchemaJson: reader.IsDBNull(5) ? null : reader.GetString(5),
            // v1.10 Phase 2 T3a: deliverable schema at index 6 (between the
            // requirement schema and price_usdc).
            PriceUsdc: reader.GetDouble(7),
            PriceType: reader.GetString(8),
            IsPrivate: reader.GetInt32(9) != 0,
            Chain: reader.GetString(10),
            ContentHash: reader.GetString(11),
            FirstSeenAt: DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastSeenAt: DateTime.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UsageCount: reader.GetInt64(14),
            AgentJobCount: reader.GetInt64(15),
            MarketplaceVersion: reader.GetString(16),
            IsRemoved: isRemoved,
            RemovedAt: removedAt,
            DeliverableSchemaJson: reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    /// <summary>
    /// v1.10 Phase 2 (T3b): total <c>schema_facets</c> row count. Used by the
    /// boot path in Program.cs to decide whether to run the one-shot backfill
    /// (<see cref="BackfillSchemaFacetsAsync"/>). When this returns 0 (fresh
    /// database) or a very small number (legacy DB pre-T1), the backfill runs;
    /// otherwise it skips. Cheap COUNT(*) — sub-millisecond even on the
    /// production corpus.
    /// </summary>
    public async Task<long> CountSchemaFacetsAsync(CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_facets;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToInt64(result);
    }

    /// <summary>
    /// v1.10 Phase 2 (T3b): one-time backfill — iterate every offering and
    /// write its facets to <c>schema_facets</c> for BOTH the requirement and
    /// deliverable roles. Idempotent via the UNIQUE(offering_id, field_name,
    /// role) constraint on <c>schema_facets</c> established by T1, so
    /// re-running on a warm DB is a no-op.
    ///
    /// Batched (500 offerings per transaction) via keyset pagination
    /// (<c>WHERE id &gt; $lastId ORDER BY id LIMIT $lim</c>) to bound memory
    /// on the production corpus and keep each write transaction short enough
    /// that incoming UpsertAsync calls don't pile up on the writer lock.
    ///
    /// Returns an upper-bound estimate of the facet rows written: 2 per
    /// offering visited (one per role). The actual inserts may be fewer
    /// because INSERT OR IGNORE silently drops duplicates and offerings with
    /// no schema JSON contribute zero facets — see the comment on the
    /// `processed` accumulator. This is good enough for the boot log line;
    /// downstream tests measure outcomes by querying <c>schema_facets</c>
    /// directly.
    /// </summary>
    public async Task<long> BackfillSchemaFacetsAsync(CancellationToken ct = default)
    {
        long processed = 0;
        await using var conn = _db.OpenConnection();
        const int BatchSize = 500;
        long lastId = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = new List<(long Id, string? Req, string? Del)>();
            await using (var read = conn.CreateCommand())
            {
                read.CommandText = @"
                    SELECT id, requirement_schema_json, deliverable_schema_json
                    FROM offerings
                    WHERE id > $lastId
                    ORDER BY id
                    LIMIT $lim;";
                read.Parameters.AddWithValue("$lastId", lastId);
                read.Parameters.AddWithValue("$lim", BatchSize);
                await using var rdr = await read.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    batch.Add((
                        rdr.GetInt64(0),
                        rdr.IsDBNull(1) ? null : rdr.GetString(1),
                        rdr.IsDBNull(2) ? null : rdr.GetString(2)));
                }
            }
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            foreach (var (id, req, del) in batch)
            {
                // Reuse the T3a helper. deleteFirst=false because the backfill
                // is purely additive — the UNIQUE constraint handles dedupe and
                // we don't want to wipe rows that arrived via the steady-state
                // UpsertAsync path between boot and this loop's commit.
                await WriteSchemaFacetsAsync(conn, tx, id, req, "requirement", deleteFirst: false);
                await WriteSchemaFacetsAsync(conn, tx, id, del, "deliverable", deleteFirst: false);
            }
            await tx.CommitAsync(ct);
            processed += batch.Count * 2;
        }
        return processed;
    }
}
