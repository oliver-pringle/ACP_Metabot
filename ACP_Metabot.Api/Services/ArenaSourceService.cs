using System.Text.Json;
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// Bundle A of Metabot v1.7. Pulls Arena participant data from ArenaBot's free
// Resources surface and writes into agent_arena_participation. Runs out of
// ArenaSourceWorker on a periodic schedule.
//
// This is a CROSS-CHECK pattern: ArenaBot owns the raw leaderboard + Council
// + Hyperliquid data; Metabot only mirrors what's needed to enrich agent
// reputation responses. No paid offering hires are involved.
public class ArenaSourceService
{
    private readonly TheArenaBotClient                 _arena;
    private readonly AgentArenaParticipationRepository _repo;
    private readonly ILogger<ArenaSourceService>       _logger;

    public ArenaSourceService(
        TheArenaBotClient arena,
        AgentArenaParticipationRepository repo,
        ILogger<ArenaSourceService> logger)
    {
        _arena = arena; _repo = repo; _logger = logger;
    }

    public async Task<(int participants, int councilRows)> RefreshAsync(CancellationToken ct)
    {
        if (!_arena.Enabled)
        {
            _logger.LogDebug("ArenaSourceService skipped — Arena:BaseUrl not configured");
            return (0, 0);
        }

        var participants = 0;
        var councilRows = 0;

        using var leaderboard = await _arena.GetLeaderboardStatusAsync(ct);
        if (leaderboard is not null)
        {
            var now = DateTime.UtcNow;
            var lifeTop = ReadTopList(leaderboard.RootElement, "lifetime");
            var w30dTop = ReadTopList(leaderboard.RootElement, "window30d");

            // Build by-address index across the two windows.
            var byAddr = new Dictionary<string, (int? rl, int? r30, double? pl, double? p30)>();
            foreach (var (rank, addr, pnl) in lifeTop)
            {
                if (!byAddr.TryGetValue(addr, out var cur)) cur = (null, null, null, null);
                byAddr[addr] = (rank, cur.r30, pnl, cur.p30);
            }
            foreach (var (rank, addr, pnl) in w30dTop)
            {
                if (!byAddr.TryGetValue(addr, out var cur)) cur = (null, null, null, null);
                byAddr[addr] = (cur.rl, rank, cur.pl, pnl);
            }

            foreach (var (addr, t) in byAddr)
            {
                await _repo.UpsertAsync(new AgentArenaParticipation(
                    AgentAddress:        addr,
                    IsParticipant:       true,
                    RankLifetime:        t.rl,
                    Rank30d:             t.r30,
                    PnlLifetimeUsd:      t.pl,
                    Pnl30dUsd:           t.p30,
                    LastWeekPick:        null,
                    FirstSeenInArenaAt:  null,
                    LastObservedAt:      now,
                    Source:              "arenabot"
                ));
                participants++;
            }
        }

        _logger.LogInformation("ArenaSourceService refreshed: participants={P} councilRows={C}", participants, councilRows);
        return (participants, councilRows);
    }

    private static List<(int rank, string addr, double pnl)> ReadTopList(JsonElement root, string key)
    {
        var list = new List<(int, string, double)>();
        if (!root.TryGetProperty(key, out var window)) return list;
        if (!window.TryGetProperty("top10", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in arr.EnumerateArray())
        {
            var rank = el.TryGetProperty("rank", out var r)    ? r.GetInt32() : 0;
            var addr = el.TryGetProperty("address", out var a) ? (a.GetString() ?? "").ToLowerInvariant() : "";
            var pnl  = el.TryGetProperty("pnlUsd", out var p)  ? p.GetDouble() : 0;
            if (rank > 0 && addr.Length == 42) list.Add((rank, addr, pnl));
        }
        return list;
    }
}
