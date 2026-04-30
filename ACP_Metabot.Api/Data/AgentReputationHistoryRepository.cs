using System.Globalization;
using System.Text.Json;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Data;

/// <summary>
/// Daily reputation snapshots. One row per (agent_address, snapshot_date UTC).
/// Same-day re-computes overwrite — the day's value is the most recent compute.
/// 90-day rolling retention pruned by the reputation warmer's post-pass.
/// </summary>
public class AgentReputationHistoryRepository
{
    private readonly Db _db;

    public AgentReputationHistoryRepository(Db db) => _db = db;

    public async Task UpsertAsync(string agentAddress, DateOnly snapshotDate,
        int agentScore, string subScoresJson, string rawCountsJson)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_reputation_history
                (agent_address, snapshot_date, agent_score, sub_scores_json, raw_counts_json)
            VALUES ($a, $d, $s, $ss, $rc)
            ON CONFLICT(agent_address, snapshot_date) DO UPDATE SET
                agent_score     = excluded.agent_score,
                sub_scores_json = excluded.sub_scores_json,
                raw_counts_json = excluded.raw_counts_json;";
        cmd.Parameters.AddWithValue("$a",  agentAddress);
        cmd.Parameters.AddWithValue("$d",  snapshotDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$s",  agentScore);
        cmd.Parameters.AddWithValue("$ss", subScoresJson);
        cmd.Parameters.AddWithValue("$rc", rawCountsJson);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Returns the most recent `days` of history for an agent, oldest → newest.
    /// Empty when the agent has no history yet.
    /// </summary>
    public async Task<IReadOnlyList<HistoryPoint>> GetTrajectoryAsync(string agentAddress, int days)
    {
        if (days < 1) days = 1;
        if (days > 365) days = 365;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-days)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT snapshot_date, agent_score, sub_scores_json
            FROM agent_reputation_history
            WHERE agent_address = $a AND snapshot_date >= $cutoff
            ORDER BY snapshot_date ASC;";
        cmd.Parameters.AddWithValue("$a",      agentAddress);
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        await using var reader = await cmd.ExecuteReaderAsync();

        var result = new List<HistoryPoint>();
        while (await reader.ReadAsync())
        {
            SubScoreSet? sub = null;
            try { sub = JsonSerializer.Deserialize<SubScoreSet>(reader.GetString(2)); }
            catch { /* corrupt row — emit headline score only */ }
            result.Add(new HistoryPoint(
                Date:       reader.GetString(0),
                AgentScore: reader.GetInt32(1),
                SubScores:  sub));
        }
        return result;
    }

    /// <summary>
    /// Deletes rows older than the cutoff. Called from the reputation warmer's
    /// post-pass. Returns the row-count deleted.
    /// </summary>
    public async Task<int> PruneOlderThanAsync(DateOnly cutoffExclusive)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_reputation_history WHERE snapshot_date < $d;";
        cmd.Parameters.AddWithValue("$d",
            cutoffExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return await cmd.ExecuteNonQueryAsync();
    }
}
