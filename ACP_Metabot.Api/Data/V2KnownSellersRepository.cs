using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

/// <summary>
/// Cache of distinct V2 ACP seller wallets observed on-chain. Populated
/// incrementally by ChainEventScanner (extracts the `provider` topic from
/// JobCreated events). Consumed by AcpV2MarketplaceSource as enumeration
/// Source A — the comprehensive set of V2 agents that have been hired
/// at least once on the configured chain.
/// </summary>
public class V2KnownSellersRepository
{
    private readonly Db _db;

    public V2KnownSellersRepository(Db db) => _db = db;

    /// <summary>Returns every cached V2 seller wallet, lowercased.</summary>
    public async Task<IReadOnlyList<string>> ListAllAsync()
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT agent_address FROM v2_known_sellers;";
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// Upserts the observed wallet. First-seen block/timestamp are set on
    /// insert; last-seen advance on every observation. Idempotent.
    /// </summary>
    public async Task UpsertObservedAsync(string agentAddress, long block, DateTime nowUtc)
    {
        var addr = agentAddress.Trim().ToLowerInvariant();
        if (addr.Length != 42 || !addr.StartsWith("0x")) return;
        var nowIso = nowUtc.ToString("O", CultureInfo.InvariantCulture);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO v2_known_sellers (agent_address, first_seen_block, first_seen_at, last_seen_block, last_seen_at)
            VALUES ($a, $blk, $now, $blk, $now)
            ON CONFLICT(agent_address) DO UPDATE SET
                last_seen_block = excluded.last_seen_block,
                last_seen_at    = excluded.last_seen_at;";
        cmd.Parameters.AddWithValue("$a", addr);
        cmd.Parameters.AddWithValue("$blk", block);
        cmd.Parameters.AddWithValue("$now", nowIso);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Bulk version for the indexer's chain-scan pass.</summary>
    public async Task UpsertManyAsync(IEnumerable<(string Address, long Block)> observations, DateTime nowUtc)
    {
        var nowIso = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();
        await using var tx = conn.BeginTransaction();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO v2_known_sellers (agent_address, first_seen_block, first_seen_at, last_seen_block, last_seen_at)
            VALUES ($a, $blk, $now, $blk, $now)
            ON CONFLICT(agent_address) DO UPDATE SET
                last_seen_block = excluded.last_seen_block,
                last_seen_at    = excluded.last_seen_at;";
        var pA = cmd.Parameters.Add("$a", SqliteType.Text);
        var pBlk = cmd.Parameters.Add("$blk", SqliteType.Integer);
        var pNow = cmd.Parameters.Add("$now", SqliteType.Text);
        pNow.Value = nowIso;

        foreach (var (addr, block) in observations)
        {
            var lower = addr.Trim().ToLowerInvariant();
            if (lower.Length != 42 || !lower.StartsWith("0x")) continue;
            pA.Value = lower;
            pBlk.Value = block;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<long> GetLastScannedBlockAsync(string key = "default")
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_scanned_block FROM v2_seller_scan_checkpoint WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        var result = await cmd.ExecuteScalarAsync();
        return result is null ? 0L : Convert.ToInt64(result);
    }

    public async Task SetLastScannedBlockAsync(long block, DateTime nowUtc, string key = "default")
    {
        var nowIso = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO v2_seller_scan_checkpoint (key, last_scanned_block, updated_at)
            VALUES ($k, $blk, $now)
            ON CONFLICT(key) DO UPDATE SET
                last_scanned_block = excluded.last_scanned_block,
                updated_at         = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$blk", block);
        cmd.Parameters.AddWithValue("$now", nowIso);
        await cmd.ExecuteNonQueryAsync();
    }
}
