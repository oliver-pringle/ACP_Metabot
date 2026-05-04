using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public record AgentProfileRow(
    string AgentAddress,
    string AgentName,
    string ProfileText,
    string? EmbeddingModel,
    byte[]? Embedding,
    string? EmbeddedAt,
    string LastChangeAt);

public class AgentProfileRepository
{
    private readonly Db _db;
    public AgentProfileRepository(Db db) { _db = db; }

    private static string NowUtcIso() => DateTime.UtcNow.ToString("O");

    /// <summary>
    /// Upsert by lowercase address. INSERT writes last_change_at = now,
    /// embedded_at = null. UPDATE rewrites profile_text + bumps
    /// last_change_at; existing embedding stays (the embedder will replace
    /// it on the next cycle).
    /// </summary>
    public async Task UpsertAsync(string agentAddress, string agentName, string profileText)
    {
        var key = agentAddress.ToLowerInvariant();
        var now = NowUtcIso();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_profiles (agent_address, agent_name, profile_text, last_change_at)
            VALUES ($a, $n, $p, $t)
            ON CONFLICT(agent_address) DO UPDATE SET
                agent_name = excluded.agent_name,
                profile_text = excluded.profile_text,
                last_change_at = excluded.last_change_at";
        cmd.Parameters.AddWithValue("$a", key);
        cmd.Parameters.AddWithValue("$n", agentName);
        cmd.Parameters.AddWithValue("$p", profileText);
        cmd.Parameters.AddWithValue("$t", now);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Bumps last_change_at for an existing agent. Used by the offering
    /// ingest path when an agent's offering set changes. No-op if the row
    /// doesn't exist (cold-start backfill will pick it up).
    /// </summary>
    public async Task BumpLastChangeAtAsync(string agentAddress)
    {
        var key = agentAddress.ToLowerInvariant();
        var now = NowUtcIso();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE agent_profiles SET last_change_at = $t WHERE agent_address = $a";
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$a", key);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Read up to <paramref name="limit"/> rows where embedded_at is null
    /// or last_change_at &gt; embedded_at. Order by last_change_at ASC so
    /// older dirty rows drain first.
    /// </summary>
    public async Task<IReadOnlyList<AgentProfileRow>> ListDirtyAsync(int limit)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, profile_text, embedding_model,
                   embedding, embedded_at, last_change_at
            FROM agent_profiles
            WHERE embedded_at IS NULL OR last_change_at > embedded_at
            ORDER BY last_change_at ASC
            LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);

        var rows = new List<AgentProfileRow>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            byte[]? emb = r.IsDBNull(4) ? null : (byte[])r.GetValue(4);
            rows.Add(new AgentProfileRow(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                emb,
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetString(6)));
        }
        return rows;
    }

    public async Task MarkEmbeddedAsync(string agentAddress, string model, byte[] embedding)
    {
        var key = agentAddress.ToLowerInvariant();
        var now = NowUtcIso();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE agent_profiles
            SET embedding = $e, embedding_model = $m, embedded_at = $t
            WHERE agent_address = $a";
        cmd.Parameters.AddWithValue("$e", embedding);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$a", key);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<AgentProfileRow?> GetAsync(string agentAddress)
    {
        var key = agentAddress.ToLowerInvariant();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, profile_text, embedding_model,
                   embedding, embedded_at, last_change_at
            FROM agent_profiles WHERE agent_address = $a";
        cmd.Parameters.AddWithValue("$a", key);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        byte[]? emb = r.IsDBNull(4) ? null : (byte[])r.GetValue(4);
        return new AgentProfileRow(
            r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            emb,
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetString(6));
    }
}
