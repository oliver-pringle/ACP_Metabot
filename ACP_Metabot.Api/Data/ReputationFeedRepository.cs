using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public sealed record ReputationFeedRow(
    string AgentAddress,
    string AggregatorAddress,
    string MethodologyHash,
    int Decimals,
    double? LatestScore,
    DateTime DeployedAt,
    DateTime FirstSeenAt,
    long? LastPushedRound,
    DateTime? LastPushedAt,
    string? LastError);

// reputation_feeds — persistence for ChainlinkBot-deployed AggregatorV3 feeds
// that publish each high-reputation agent's score on-chain. Keyed by lower-
// cased agent_address. The publisher worker (ReputationFeedPublisherWorker)
// is the writer; admin GET endpoints + the future v0.2 score-push worker are
// readers. Insert path is idempotent via ON CONFLICT — re-running the daily
// publisher is a no-op for agents already published.
public class ReputationFeedRepository
{
    private readonly Db _db;
    public ReputationFeedRepository(Db db) => _db = db;

    public async Task<ReputationFeedRow?> GetAsync(string agentAddress)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, aggregator_address, methodology_hash, decimals,
                   latest_score, deployed_at, first_seen_at,
                   last_pushed_round, last_pushed_at, last_error
            FROM reputation_feeds
            WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return Hydrate(reader);
    }

    public async Task<IReadOnlyList<ReputationFeedRow>> ListAllAsync(int limit = 500)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, aggregator_address, methodology_hash, decimals,
                   latest_score, deployed_at, first_seen_at,
                   last_pushed_round, last_pushed_at, last_error
            FROM reputation_feeds
            ORDER BY deployed_at DESC
            LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<ReputationFeedRow>(capacity: limit);
        while (await reader.ReadAsync()) rows.Add(Hydrate(reader));
        return rows;
    }

    // Returns agent addresses (lower-cased) that don't yet have a feed row.
    // The publisher worker uses this to know whom to ask ChainlinkBot about.
    // Caller should pre-lower the input list.
    public async Task<HashSet<string>> FilterAlreadyPublishedAsync(IEnumerable<string> candidateLowerAddresses)
    {
        var set = new HashSet<string>(candidateLowerAddresses, StringComparer.Ordinal);
        if (set.Count == 0) return set;
        var placeholders = string.Join(",", set.Select((_, i) => $"$a{i}"));
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT agent_address FROM reputation_feeds WHERE agent_address IN ({placeholders});";
        var i = 0;
        foreach (var a in set) cmd.Parameters.AddWithValue($"$a{i++}", a);
        await using var reader = await cmd.ExecuteReaderAsync();
        var already = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) already.Add(reader.GetString(0));
        // Return only the not-yet-published subset.
        set.ExceptWith(already);
        return set;
    }

    public async Task UpsertDeployedAsync(
        string agentAddress, string aggregatorAddress, string methodologyHash,
        int decimals, double? latestScore, DateTime deployedAt, DateTime firstSeenAt)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO reputation_feeds
                (agent_address, aggregator_address, methodology_hash, decimals,
                 latest_score, deployed_at, first_seen_at)
            VALUES ($a, $agg, $m, $d, $s, $dep, $fs)
            ON CONFLICT(agent_address) DO UPDATE SET
                aggregator_address = excluded.aggregator_address,
                methodology_hash   = excluded.methodology_hash,
                decimals           = excluded.decimals,
                latest_score       = COALESCE(excluded.latest_score, reputation_feeds.latest_score),
                last_error         = NULL;";
        cmd.Parameters.AddWithValue("$a",   agentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$agg", aggregatorAddress);
        cmd.Parameters.AddWithValue("$m",   methodologyHash);
        cmd.Parameters.AddWithValue("$d",   decimals);
        cmd.Parameters.AddWithValue("$s",   (object?)latestScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dep", deployedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$fs",  firstSeenAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RecordErrorAsync(string agentAddress, string error)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO reputation_feeds
                (agent_address, aggregator_address, methodology_hash, decimals,
                 deployed_at, first_seen_at, last_error)
            VALUES ($a, '', '', 0, $now, $now, $e)
            ON CONFLICT(agent_address) DO UPDATE SET
                last_error = excluded.last_error;";
        cmd.Parameters.AddWithValue("$a",   agentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$e",   error);
        await cmd.ExecuteNonQueryAsync();
    }

    // Returns aggregator addresses that are deployed (non-empty). The sync
    // worker (v0.2) uses this to know which feeds to poll ChainlinkBot for.
    public async Task<IReadOnlyList<(string AgentAddress, string AggregatorAddress)>>
        ListDeployedAsync(int limit = 500)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, aggregator_address
            FROM reputation_feeds
            WHERE aggregator_address != ''
            ORDER BY deployed_at DESC
            LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string)>(capacity: limit);
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    // v0.2 sync — record what we observed from ChainlinkBot's score push log.
    // Updates only the sync columns (latest_score, last_pushed_round/at) and
    // clears last_error on success. Does NOT touch aggregator_address or
    // deployed_at — those are the publisher worker's territory.
    public async Task UpdateSyncedScoreAsync(
        string agentAddress, double latestScore, long roundId, DateTime pushedAt)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE reputation_feeds SET
                latest_score      = $s,
                last_pushed_round = $r,
                last_pushed_at    = $p,
                last_error        = NULL
            WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$s", latestScore);
        cmd.Parameters.AddWithValue("$r", roundId);
        cmd.Parameters.AddWithValue("$p", pushedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountPublishedAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM reputation_feeds WHERE aggregator_address != '';";
        var raw = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(raw);
    }

    private static ReputationFeedRow Hydrate(SqliteDataReader r)
    {
        return new ReputationFeedRow(
            AgentAddress:      r.GetString(0),
            AggregatorAddress: r.GetString(1),
            MethodologyHash:   r.GetString(2),
            Decimals:          r.GetInt32(3),
            LatestScore:       r.IsDBNull(4) ? null : r.GetDouble(4),
            DeployedAt:        DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            FirstSeenAt:       DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastPushedRound:   r.IsDBNull(7) ? null : r.GetInt64(7),
            LastPushedAt:      r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LastError:         r.IsDBNull(9) ? null : r.GetString(9));
    }
}
