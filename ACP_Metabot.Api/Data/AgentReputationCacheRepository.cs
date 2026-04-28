using System.Globalization;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public class AgentReputationCacheRepository
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly Db _db;

    public AgentReputationCacheRepository(Db db) => _db = db;

    // Returns null if no row, or row older than 24h.
    public async Task<CachedReputationRow?> GetAsync(string agentAddress, DateTime nowUtc)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, agent_score, sub_scores_json, raw_counts_json,
                   flags_json, computed_at, last_scanned_block, source
            FROM agent_reputation_cache
            WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var computedAt = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        if (nowUtc - computedAt > Ttl) return null; // shadowed

        return new CachedReputationRow(
            AgentAddress:     reader.GetString(0),
            AgentName:        reader.GetString(1),
            AgentScore:       reader.GetInt32(2),
            SubScoresJson:    reader.GetString(3),
            RawCountsJson:    reader.GetString(4),
            FlagsJson:        reader.GetString(5),
            ComputedAt:       computedAt,
            LastScannedBlock: reader.GetInt64(7),
            Source:           reader.GetString(8));
    }

    // Returns the highest block scanned for this agent on any prior compute,
    // even if the row is now > 24h old. Used to make incremental rescans
    // cheap.
    public async Task<long?> GetLastScannedBlockAsync(string agentAddress)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_scanned_block FROM agent_reputation_cache WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetInt64(0) : null;
    }

    public async Task UpsertAsync(CachedReputationRow row)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_reputation_cache
                (agent_address, agent_name, agent_score, sub_scores_json, raw_counts_json,
                 flags_json, computed_at, last_scanned_block, source)
            VALUES ($a, $n, $s, $ss, $rc, $f, $c, $b, $src)
            ON CONFLICT(agent_address) DO UPDATE SET
                agent_name         = excluded.agent_name,
                agent_score        = excluded.agent_score,
                sub_scores_json    = excluded.sub_scores_json,
                raw_counts_json    = excluded.raw_counts_json,
                flags_json         = excluded.flags_json,
                computed_at        = excluded.computed_at,
                last_scanned_block = excluded.last_scanned_block,
                source             = excluded.source;";
        cmd.Parameters.AddWithValue("$a",   row.AgentAddress);
        cmd.Parameters.AddWithValue("$n",   row.AgentName);
        cmd.Parameters.AddWithValue("$s",   row.AgentScore);
        cmd.Parameters.AddWithValue("$ss",  row.SubScoresJson);
        cmd.Parameters.AddWithValue("$rc",  row.RawCountsJson);
        cmd.Parameters.AddWithValue("$f",   row.FlagsJson);
        cmd.Parameters.AddWithValue("$c",   row.ComputedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$b",   row.LastScannedBlock);
        cmd.Parameters.AddWithValue("$src", row.Source);
        await cmd.ExecuteNonQueryAsync();
    }

    // Returns the top-N agent addresses by lifetime job count, deduplicated.
    // Used by the warmer to pick its daily target set.
    public async Task<IReadOnlyList<(string AgentAddress, string AgentName)>> ListWarmAgentsAsync(int topN)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, MAX(agent_name), MAX(agent_job_count) AS jobs
            FROM offerings
            GROUP BY agent_address
            ORDER BY jobs DESC
            LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", topN);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<(string, string)>(capacity: topN);
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        return list;
    }

    // Loads every fresh (≤ 24h old) cache row for the percentile rebuild pass.
    public async Task<IReadOnlyList<CachedReputationRow>> ListAllForPercentilesAsync(DateTime nowUtc)
    {
        var cutoff = nowUtc.Subtract(Ttl).ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, agent_score, sub_scores_json, raw_counts_json,
                   flags_json, computed_at, last_scanned_block, source
            FROM agent_reputation_cache
            WHERE computed_at >= $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<CachedReputationRow>();
        while (await reader.ReadAsync())
        {
            list.Add(new CachedReputationRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt64(7), reader.GetString(8)));
        }
        return list;
    }
}
