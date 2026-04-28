using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public class LifetimeSnapshotRepository
{
    private readonly Db _db;
    public LifetimeSnapshotRepository(Db db) => _db = db;

    private static string FormatDate(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task UpsertAsync(string agentAddress, DateTime snapshotDate, long totalJobs)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_lifetime_snapshot (agent_address, snapshot_date, total_jobs)
            VALUES ($a, $d, $j)
            ON CONFLICT(agent_address, snapshot_date) DO UPDATE SET
                total_jobs = excluded.total_jobs;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        cmd.Parameters.AddWithValue("$d", FormatDate(snapshotDate));
        cmd.Parameters.AddWithValue("$j", totalJobs);
        await cmd.ExecuteNonQueryAsync();
    }

    // Returns the agent's total_jobs at the named date, or null if no row.
    public async Task<long?> GetAsync(string agentAddress, DateTime snapshotDate)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT total_jobs FROM agent_lifetime_snapshot WHERE agent_address = $a AND snapshot_date = $d;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        cmd.Parameters.AddWithValue("$d", FormatDate(snapshotDate));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetInt64(0) : null;
    }

    public async Task PruneOlderThanAsync(DateTime cutoff)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_lifetime_snapshot WHERE snapshot_date < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", FormatDate(cutoff));
        await cmd.ExecuteNonQueryAsync();
    }

    // Bulk UPSERT used by the daily snapshot service. Wraps in a transaction
    // so we either get all rows for the day or none.
    public async Task UpsertManyAsync(IReadOnlyDictionary<string, long> agentTotals, DateTime snapshotDate)
    {
        if (agentTotals.Count == 0) return;
        await using var conn = _db.OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO agent_lifetime_snapshot (agent_address, snapshot_date, total_jobs)
            VALUES ($a, $d, $j)
            ON CONFLICT(agent_address, snapshot_date) DO UPDATE SET
                total_jobs = excluded.total_jobs;";
        var pAddr = cmd.Parameters.Add("$a", SqliteType.Text);
        var pDate = cmd.Parameters.Add("$d", SqliteType.Text);
        var pJobs = cmd.Parameters.Add("$j", SqliteType.Integer);
        var dateStr = FormatDate(snapshotDate);
        foreach (var (addr, jobs) in agentTotals)
        {
            pAddr.Value = addr;
            pDate.Value = dateStr;
            pJobs.Value = jobs;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
