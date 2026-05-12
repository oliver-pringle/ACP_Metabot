using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public record AgentArenaParticipation(
    string AgentAddress,
    bool IsParticipant,
    int? RankLifetime,
    int? Rank30d,
    double? PnlLifetimeUsd,
    double? Pnl30dUsd,
    bool? LastWeekPick,
    DateTime? FirstSeenInArenaAt,
    DateTime LastObservedAt,
    string Source
);

public record CouncilPickCacheRow(
    DateTime WeekStart,
    string AgentAddress,
    int PickRank,
    DateTime RefreshedAt
);

public class AgentArenaParticipationRepository
{
    private readonly Db _db;
    public AgentArenaParticipationRepository(Db db) => _db = db;

    public async Task UpsertAsync(AgentArenaParticipation row)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_arena_participation
              (agent_address, is_participant, rank_lifetime, rank_30d,
               pnl_lifetime_usd, pnl_30d_usd, last_week_pick,
               first_seen_in_arena_at, last_observed_at, source)
            VALUES ($a, $ip, $rl, $r30, $pl, $p30, $lwp, $fs, $lo, $src)
            ON CONFLICT(agent_address) DO UPDATE SET
              is_participant         = excluded.is_participant,
              rank_lifetime          = excluded.rank_lifetime,
              rank_30d               = excluded.rank_30d,
              pnl_lifetime_usd       = excluded.pnl_lifetime_usd,
              pnl_30d_usd            = excluded.pnl_30d_usd,
              last_week_pick         = excluded.last_week_pick,
              first_seen_in_arena_at = COALESCE(excluded.first_seen_in_arena_at, agent_arena_participation.first_seen_in_arena_at),
              last_observed_at       = excluded.last_observed_at,
              source                 = excluded.source;
        ";
        cmd.Parameters.AddWithValue("$a",   row.AgentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$ip",  row.IsParticipant ? 1 : 0);
        cmd.Parameters.AddWithValue("$rl",  (object?)row.RankLifetime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r30", (object?)row.Rank30d ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pl",  (object?)row.PnlLifetimeUsd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p30", (object?)row.Pnl30dUsd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lwp", row.LastWeekPick.HasValue ? (object)(row.LastWeekPick.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$fs",  row.FirstSeenInArenaAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$lo",  row.LastObservedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$src", row.Source);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<AgentArenaParticipation?> GetByAddressAsync(string address)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT agent_address, is_participant, rank_lifetime, rank_30d,
                                   pnl_lifetime_usd, pnl_30d_usd, last_week_pick,
                                   first_seen_in_arena_at, last_observed_at, source
                            FROM agent_arena_participation WHERE agent_address = $a LIMIT 1;";
        cmd.Parameters.AddWithValue("$a", address.ToLowerInvariant());
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (!await rdr.ReadAsync()) return null;
        return Map(rdr);
    }

    public async Task<IReadOnlyList<AgentArenaParticipation>> ListAsync(int? limit = null)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = limit.HasValue
            ? "SELECT agent_address, is_participant, rank_lifetime, rank_30d, pnl_lifetime_usd, pnl_30d_usd, last_week_pick, first_seen_in_arena_at, last_observed_at, source FROM agent_arena_participation WHERE is_participant = 1 ORDER BY rank_30d ASC NULLS LAST LIMIT $lim;"
            : "SELECT agent_address, is_participant, rank_lifetime, rank_30d, pnl_lifetime_usd, pnl_30d_usd, last_week_pick, first_seen_in_arena_at, last_observed_at, source FROM agent_arena_participation WHERE is_participant = 1 ORDER BY rank_30d ASC NULLS LAST;";
        if (limit.HasValue) cmd.Parameters.AddWithValue("$lim", limit.Value);
        await using var rdr = await cmd.ExecuteReaderAsync();
        var list = new List<AgentArenaParticipation>();
        while (await rdr.ReadAsync()) list.Add(Map(rdr));
        return list;
    }

    public async Task<int> CountParticipantsAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agent_arena_participation WHERE is_participant = 1;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public async Task<DateTime?> GetLastObservedAtAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(last_observed_at) FROM agent_arena_participation;";
        var v = await cmd.ExecuteScalarAsync();
        return v is null || v is DBNull ? null : DateTime.Parse((string)v).ToUniversalTime();
    }

    public async Task UpsertCouncilCacheAsync(DateTime weekStart, IEnumerable<(string addr, int rank)> picks)
    {
        await using var conn = _db.OpenConnection();
        await using var tx = conn.BeginTransaction();
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM arena_council_pick_cache WHERE week_start = $w;";
            del.Parameters.AddWithValue("$w", weekStart.ToString("O"));
            await del.ExecuteNonQueryAsync();
        }
        foreach (var (addr, rank) in picks)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO arena_council_pick_cache(week_start, agent_address, pick_rank, refreshed_at) VALUES($w, $a, $r, $now);";
            ins.Parameters.AddWithValue("$w", weekStart.ToString("O"));
            ins.Parameters.AddWithValue("$a", addr.ToLowerInvariant());
            ins.Parameters.AddWithValue("$r", rank);
            ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await ins.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<CouncilPickCacheRow>> GetRecentCouncilCacheAsync(int weeks)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT week_start, agent_address, pick_rank, refreshed_at
            FROM arena_council_pick_cache
            WHERE week_start IN (SELECT DISTINCT week_start FROM arena_council_pick_cache ORDER BY week_start DESC LIMIT $lim)
            ORDER BY week_start DESC, pick_rank ASC;";
        cmd.Parameters.AddWithValue("$lim", weeks);
        await using var rdr = await cmd.ExecuteReaderAsync();
        var list = new List<CouncilPickCacheRow>();
        while (await rdr.ReadAsync())
        {
            list.Add(new CouncilPickCacheRow(
                WeekStart:    DateTime.Parse(rdr.GetString(0)).ToUniversalTime(),
                AgentAddress: rdr.GetString(1),
                PickRank:     rdr.GetInt32(2),
                RefreshedAt:  DateTime.Parse(rdr.GetString(3)).ToUniversalTime()
            ));
        }
        return list;
    }

    private static AgentArenaParticipation Map(SqliteDataReader r) => new(
        AgentAddress:        r.GetString(0),
        IsParticipant:       r.GetInt32(1) == 1,
        RankLifetime:        r.IsDBNull(2) ? null : r.GetInt32(2),
        Rank30d:             r.IsDBNull(3) ? null : r.GetInt32(3),
        PnlLifetimeUsd:      r.IsDBNull(4) ? null : r.GetDouble(4),
        Pnl30dUsd:           r.IsDBNull(5) ? null : r.GetDouble(5),
        LastWeekPick:        r.IsDBNull(6) ? null : r.GetInt32(6) == 1,
        FirstSeenInArenaAt:  r.IsDBNull(7) ? null : DateTime.Parse(r.GetString(7)).ToUniversalTime(),
        LastObservedAt:      DateTime.Parse(r.GetString(8)).ToUniversalTime(),
        Source:              r.GetString(9)
    );
}
