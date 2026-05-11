using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

/// <summary>
/// R7-IDEA-C: Storage for per-agent ACP v2 Resources (AcpAgentResource:
/// name + url + params + description). Mirrored 1:1 from upstream V2 agent
/// profiles by <see cref="ACP_Metabot.Api.Services.MarketplaceSource.AcpV2MarketplaceSource"/>
/// during its per-wallet fetch pass.
///
/// v1 reconciliation pattern: UPSERT-only. Resources that disappear upstream
/// stay in the index with a stale last_seen_at. A future v1.1 may add
/// tombstone behaviour matching the v1.5 offerings lifecycle.
/// </summary>
public sealed class AgentResourcesRepository
{
    private readonly Db _db;

    public AgentResourcesRepository(Db db) => _db = db;

    public sealed record AgentResourceRow(
        long Id,
        string AgentAddress,
        string AgentName,
        string Name,
        string Url,
        string? ParamsJson,
        string Description,
        string MarketplaceVersion,
        DateTime FirstSeenAt,
        DateTime LastSeenAt);

    /// <summary>
    /// UPSERT all observed resources for an agent in a single transaction.
    /// On (marketplace_version, agent_address, name) conflict: refresh
    /// url / params_json / description / agent_name / last_seen_at; preserve
    /// the original first_seen_at.
    /// </summary>
    public async Task<int> UpsertManyForAgentAsync(
        string agentAddress,
        string agentName,
        string marketplaceVersion,
        IReadOnlyList<(string Name, string Url, string? ParamsJson, string Description)> resources,
        CancellationToken ct = default)
    {
        if (resources.Count == 0) return 0;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using var conn = _db.OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO agent_resources
                  (agent_address, agent_name, name, url, params_json, description,
                   marketplace_version, first_seen_at, last_seen_at)
                VALUES
                  ($agent, $agentName, $name, $url, $params, $description,
                   $mv, $now, $now)
                ON CONFLICT(marketplace_version, agent_address, name) DO UPDATE SET
                    agent_name   = excluded.agent_name,
                    url          = excluded.url,
                    params_json  = excluded.params_json,
                    description  = excluded.description,
                    last_seen_at = excluded.last_seen_at;";

            // Pre-create parameter slots once; rebind per row.
            var pAgent       = cmd.Parameters.Add("$agent", SqliteType.Text);
            var pAgentName   = cmd.Parameters.Add("$agentName", SqliteType.Text);
            var pName        = cmd.Parameters.Add("$name", SqliteType.Text);
            var pUrl         = cmd.Parameters.Add("$url", SqliteType.Text);
            var pParams      = cmd.Parameters.Add("$params", SqliteType.Text);
            var pDescription = cmd.Parameters.Add("$description", SqliteType.Text);
            var pMv          = cmd.Parameters.Add("$mv", SqliteType.Text);
            var pNow         = cmd.Parameters.Add("$now", SqliteType.Text);

            int written = 0;
            foreach (var r in resources)
            {
                ct.ThrowIfCancellationRequested();
                pAgent.Value       = agentAddress.ToLowerInvariant();
                pAgentName.Value   = agentName ?? "";
                pName.Value        = r.Name;
                pUrl.Value         = r.Url;
                pParams.Value      = (object?)r.ParamsJson ?? DBNull.Value;
                pDescription.Value = r.Description ?? "";
                pMv.Value          = marketplaceVersion;
                pNow.Value         = now;
                written += await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return written;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* swallow */ }
            throw;
        }
    }

    public async Task<IReadOnlyList<AgentResourceRow>> ListByAgentAsync(
        string agentAddress, CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, agent_address, agent_name, name, url, params_json, description,
                   marketplace_version, first_seen_at, last_seen_at
            FROM agent_resources
            WHERE agent_address = $a COLLATE NOCASE
            ORDER BY name ASC;";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());

        var list = new List<AgentResourceRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) list.Add(Read(rdr));
        return list;
    }

    /// <summary>
    /// LIKE-based search across name + description + agent_name. v1 is
    /// case-insensitive substring match; v1.1 may upgrade to FTS5 once the
    /// dataset exceeds a few hundred rows.
    /// </summary>
    public async Task<IReadOnlyList<AgentResourceRow>> SearchAsync(
        string query, int limit, string? marketplaceFilter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return Array.Empty<AgentResourceRow>();
        var pattern = "%" + query.Trim() + "%";

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var mvFilter = string.IsNullOrWhiteSpace(marketplaceFilter)
            ? ""
            : "AND marketplace_version = $mv ";
        cmd.CommandText = @"
            SELECT id, agent_address, agent_name, name, url, params_json, description,
                   marketplace_version, first_seen_at, last_seen_at
            FROM agent_resources
            WHERE (name LIKE $q OR description LIKE $q OR agent_name LIKE $q) " + mvFilter + @"
            ORDER BY last_seen_at DESC
            LIMIT $lim;";
        cmd.Parameters.AddWithValue("$q", pattern);
        cmd.Parameters.AddWithValue("$lim", limit);
        if (!string.IsNullOrWhiteSpace(marketplaceFilter))
            cmd.Parameters.AddWithValue("$mv", marketplaceFilter);

        var list = new List<AgentResourceRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) list.Add(Read(rdr));
        return list;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agent_resources;";
        var v = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(v ?? 0);
    }

    public async Task<int> DistinctAgentCountAsync(CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT agent_address) FROM agent_resources;";
        var v = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(v ?? 0);
    }

    private static AgentResourceRow Read(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.GetString(6),
        r.GetString(7),
        DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
