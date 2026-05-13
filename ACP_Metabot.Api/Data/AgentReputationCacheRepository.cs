using System.Globalization;
using System.Text.Json;
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

    // v1.7.5: returns the agent's on-chain TotalJobs from any prior compute
    // (raw_counts_json.totalJobs), bypassing the 24h TTL — a stale count is
    // strictly better than 0 for marketplace ranking. Returns null when the
    // agent has never been warmed.
    //
    // The V2 marketplace API doesn't expose hire counts on offerings, and
    // ChainEventScanner only writes results to this cache (not back to
    // offerings.agent_job_count). AcpV2MarketplaceSource calls this per-wallet
    // during indexer passes so V2 offering rows get the agent-level total
    // populated, fixing agentReputation.agentTotalJobs, searchAgents agent
    // percentile, and the warmer's V2-active secondary sort.
    public async Task<long?> GetCachedTotalJobsAsync(string agentAddress)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw_counts_json FROM agent_reputation_cache WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var json = reader.GetString(0);
        try
        {
            var counts = JsonSerializer.Deserialize<RawCounts>(json);
            return counts?.TotalJobs;
        }
        catch (JsonException)
        {
            return null;
        }
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

    // Returns the top-N V2-active agent addresses by V2 hire count, deduplicated.
    // Used by the warmer to pick its daily target set.
    //
    // v1.7.3 fix: previously ordered by MAX(agent_job_count) DESC across all
    // marketplace versions. `agent_job_count` is populated by acpx.virtuals.io
    // (V1 marketplace) but is always 0 for V2 registrations, so the prior query
    // returned V1-only agents with high V1 job counts — but the ChainEventScanner
    // reads V2 contract events on Base mainnet. Every "top" V1-only agent
    // therefore scored as cold-start (totalJobs=0, isColdStart=true) and the
    // warmer's effort produced no useful reputation signal.
    //
    // New query filters to agents with at least one non-tombstoned V2 offering
    // and orders by V2 hire count (SUM of usage_count on V2 offerings), so
    // the warmer prioritises agents the chain scanner can actually find events
    // for. Tie-broken by V1 job count as a secondary signal (so cross-marketplace
    // agents float above V2-only-with-zero-hires agents).
    public async Task<IReadOnlyList<(string AgentAddress, string AgentName)>> ListWarmAgentsAsync(int topN)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                agent_address,
                MAX(agent_name) AS name,
                SUM(CASE WHEN marketplace_version = 'v2' THEN usage_count ELSE 0 END) AS v2_hires,
                MAX(agent_job_count) AS v1_jobs
            FROM offerings
            WHERE is_removed = 0
            GROUP BY agent_address
            HAVING SUM(CASE WHEN marketplace_version = 'v2' THEN 1 ELSE 0 END) > 0
            ORDER BY v2_hires DESC, v1_jobs DESC
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

    // Snapshot of (lower-cased agent_address -> agent_score) across every fresh
    // cache row. Used by SearchService to apply the minReputation filter
    // without per-request DB round-trips. Stale rows (>24h old) are skipped —
    // an unreliable snapshot is worse than no signal.
    public async Task<IReadOnlyDictionary<string, int>> ListAllAgentScoresAsync(DateTime nowUtc)
    {
        var cutoff = nowUtc.Subtract(Ttl).ToString("O", CultureInfo.InvariantCulture);
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_score
            FROM agent_reputation_cache
            WHERE computed_at >= $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            dict[reader.GetString(0).ToLowerInvariant()] = reader.GetInt32(1);
        }
        return dict;
    }

    // Returns the top-N fresh cache rows with score >= minScore, ordered by
    // score DESC. Used by the v1.6 ReputationFeedPublisherWorker to pick the
    // agents whose reputation gets a Chainlink AggregatorV3 feed published.
    public async Task<IReadOnlyList<CachedReputationRow>> ListTopByScoreAsync(
        int topN, int minScore, DateTime nowUtc)
    {
        var cutoff = nowUtc.Subtract(Ttl).ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, agent_score, sub_scores_json, raw_counts_json,
                   flags_json, computed_at, last_scanned_block, source
            FROM agent_reputation_cache
            WHERE computed_at >= $cutoff AND agent_score >= $min
            ORDER BY agent_score DESC
            LIMIT $n;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.Parameters.AddWithValue("$min", minScore);
        cmd.Parameters.AddWithValue("$n", topN);
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
