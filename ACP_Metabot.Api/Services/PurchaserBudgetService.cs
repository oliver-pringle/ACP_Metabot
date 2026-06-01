using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Services;

public record BudgetCheckResult(bool Reserved, decimal DayTotalUsd, decimal CapUsd, string DayIso);

/// <summary>
/// Atomic per-buyer/day USDC cap for ACPPurchaser. Lifted from
/// ACP_PrivateTrader's ClaudeBudgetService and keyed per (buyer_key, day_iso).
/// A backstop bounding self-funded overage + abuse — the primary protection is
/// the escrow-locked Require-Funds reimbursement. Cap from
/// env ACPPURCHASER_DAILY_CAP_USDC (default 50). WAL + busy_timeout=5000 (Db.cs)
/// gives correct concurrent behaviour.
/// </summary>
public sealed class PurchaserBudgetService
{
    private readonly Db _db;
    private readonly decimal _cap;
    private readonly ILogger<PurchaserBudgetService> _logger;

    public PurchaserBudgetService(Db db, IConfiguration config, ILogger<PurchaserBudgetService> logger)
    {
        _db = db;
        _logger = logger;
        _cap = config.GetValue<decimal?>("ACPPURCHASER_DAILY_CAP_USDC") ?? 50m;
    }

    private static string CurrentDayIso() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public async Task<BudgetCheckResult> TryReserveAsync(string buyerKey, decimal estimatedCostUsd, CancellationToken ct)
    {
        if (estimatedCostUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedCostUsd));
        var key = (buyerKey ?? string.Empty).Trim().ToLowerInvariant();
        var day = CurrentDayIso();

        await using var conn = _db.OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = @"INSERT OR IGNORE INTO acppurchaser_daily_spend (buyer_key, day_iso, total_usd, updated_at)
                                    VALUES ($k, $day, 0.0, $now);";
                ins.Parameters.AddWithValue("$k", key);
                ins.Parameters.AddWithValue("$day", day);
                ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await ins.ExecuteNonQueryAsync(ct);
            }

            decimal current;
            await using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = @"SELECT total_usd FROM acppurchaser_daily_spend WHERE buyer_key=$k AND day_iso=$day;";
                sel.Parameters.AddWithValue("$k", key);
                sel.Parameters.AddWithValue("$day", day);
                var raw = await sel.ExecuteScalarAsync(ct);
                current = raw is null or DBNull ? 0m : Convert.ToDecimal(raw);
            }

            var projected = current + estimatedCostUsd;
            if (projected > _cap)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning("[acp-purchaser-budget] REJECT buyer={Key} cur={Cur} est={Est} cap={Cap}", key, current, estimatedCostUsd, _cap);
                return new BudgetCheckResult(false, current, _cap, day);
            }

            await using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = @"UPDATE acppurchaser_daily_spend SET total_usd=$p, updated_at=$now WHERE buyer_key=$k AND day_iso=$day;";
                upd.Parameters.AddWithValue("$p", (double)projected);
                upd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                upd.Parameters.AddWithValue("$k", key);
                upd.Parameters.AddWithValue("$day", day);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return new BudgetCheckResult(true, projected, _cap, day);
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
            throw;
        }
    }

    public async Task RecordActualSpendAsync(string buyerKey, decimal deltaUsd, CancellationToken ct)
    {
        if (deltaUsd == 0m) return;
        var key = (buyerKey ?? string.Empty).Trim().ToLowerInvariant();
        var day = CurrentDayIso();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO acppurchaser_daily_spend (buyer_key, day_iso, total_usd, updated_at)
                            VALUES ($k, $day, $d, $now)
                            ON CONFLICT(buyer_key, day_iso) DO UPDATE SET
                              total_usd = MAX(0, total_usd + excluded.total_usd),
                              updated_at = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$d", (double)deltaUsd);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<decimal> GetTodaysSpendAsync(string buyerKey, CancellationToken ct)
    {
        var key = (buyerKey ?? string.Empty).Trim().ToLowerInvariant();
        var day = CurrentDayIso();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT total_usd FROM acppurchaser_daily_spend WHERE buyer_key=$k AND day_iso=$day;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$day", day);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null or DBNull ? 0m : Convert.ToDecimal(raw);
    }
}
