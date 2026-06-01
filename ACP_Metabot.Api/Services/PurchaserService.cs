using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

/// <summary>Seam over AgentRiskScorer so the verdict logic is unit-testable.</summary>
public interface IAgentRiskSource
{
    Task<string> RiskTierAsync(string agentAddress, int chainId, CancellationToken ct);
}

/// <summary>Adapts the concrete AgentRiskScorer to IAgentRiskSource (DI default).</summary>
public sealed class AgentRiskScorerSource : IAgentRiskSource
{
    private readonly AgentRiskScorer _scorer;
    public AgentRiskScorerSource(AgentRiskScorer scorer) => _scorer = scorer;
    public async Task<string> RiskTierAsync(string agentAddress, int chainId, CancellationToken ct)
    {
        try { return (await _scorer.ScoreAsync(agentAddress, chainId, ct)).RiskTier; }
        catch { return "unknown"; }
    }
}

public record PurchaseQuoteResult(
    string TargetAgent, decimal DownstreamUsdc, decimal ServiceFeeUsdc,
    decimal TotalEscrowUsdc, bool FixedPrice, string RiskTier, string Verdict, IReadOnlyList<string> Reasons);

public record PrecheckResult(bool Ok, string? Reason, decimal DownstreamUsdc);

/// <summary>
/// ACPPurchaser Path A business tier: quote (pre-flight verdict), precheck
/// (gate + atomic cap reservation + audit), settle (audit + refund-on-reject).
/// C# never speaks ACP — the sidecar resolves the live downstream price and
/// orchestrates the hire; this service owns the money-safety logic only.
/// </summary>
public sealed class PurchaserService
{
    public const decimal ServiceFeeUsdc = 0.10m;
    private readonly Db _db;
    private readonly PurchaserBudgetService _budget;
    private readonly IAgentRiskSource _risk;
    private readonly ILogger<PurchaserService> _logger;

    public PurchaserService(Db db, PurchaserBudgetService budget, IAgentRiskSource risk, ILogger<PurchaserService> logger)
    {
        _db = db; _budget = budget; _risk = risk; _logger = logger;
    }

    private static (string verdict, List<string> reasons) Decide(bool fixedPrice, string tier)
    {
        var reasons = new List<string>();
        if (!fixedPrice) { reasons.Add("not_fixed_price"); return ("BLOCK", reasons); }
        if (tier == "critical") { reasons.Add("risk_critical"); return ("BLOCK", reasons); }
        if (tier == "high") { reasons.Add("risk_high"); return ("CAUTION", reasons); }
        reasons.Add("ok");
        return ("PROCEED", reasons);
    }

    public async Task<PurchaseQuoteResult> QuoteAsync(string targetAgent, decimal downstreamUsdc, bool fixedPrice, CancellationToken ct)
    {
        var tier = await _risk.RiskTierAsync(targetAgent, 8453, ct);
        var (verdict, reasons) = Decide(fixedPrice, tier);
        return new PurchaseQuoteResult(
            targetAgent, downstreamUsdc, ServiceFeeUsdc,
            decimal.Round(ServiceFeeUsdc + downstreamUsdc, 4), fixedPrice, tier, verdict, reasons);
    }

    public async Task<PrecheckResult> PrecheckAsync(
        string outerJobId, string buyerKey, string targetAgent, string targetOffering,
        decimal downstreamUsdc, decimal maxFundsUsdc, CancellationToken ct)
    {
        if (downstreamUsdc > maxFundsUsdc)
        {
            await WriteAuditAsync(outerJobId, buyerKey, targetAgent, targetOffering, downstreamUsdc, "REJECTED", "over_max_funds", null, ct);
            return new PrecheckResult(false, "over_max_funds", downstreamUsdc);
        }
        var tier = await _risk.RiskTierAsync(targetAgent, 8453, ct);
        if (tier == "critical")
        {
            await WriteAuditAsync(outerJobId, buyerKey, targetAgent, targetOffering, downstreamUsdc, "REJECTED", "risk_critical", null, ct);
            return new PrecheckResult(false, "risk_critical", downstreamUsdc);
        }
        var reserve = await _budget.TryReserveAsync(buyerKey, downstreamUsdc, ct);
        if (!reserve.Reserved)
        {
            await WriteAuditAsync(outerJobId, buyerKey, targetAgent, targetOffering, downstreamUsdc, "REJECTED", "daily_cap_exceeded", null, ct);
            return new PrecheckResult(false, "daily_cap_exceeded", downstreamUsdc);
        }
        await WriteAuditAsync(outerJobId, buyerKey, targetAgent, targetOffering, downstreamUsdc, "PRECHECK", null, null, ct);
        return new PrecheckResult(true, null, downstreamUsdc);
    }

    public async Task SettleAsync(string outerJobId, string buyerKey, string state, string? innerJobId, string? reason, decimal downstreamUsdc, CancellationToken ct)
    {
        if (state == "REJECTED")
            await _budget.RecordActualSpendAsync(buyerKey, -downstreamUsdc, ct); // refund the reservation
        await UpdateAuditAsync(outerJobId, state, innerJobId, reason, ct);
    }

    private async Task WriteAuditAsync(string? outerJobId, string buyerKey, string targetAgent, string targetOffering,
        decimal downstreamUsdc, string state, string? reason, string? innerJobId, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO acppurchaser_audit
            (outer_job_id, buyer_key, target_agent, target_offering, downstream_usd, service_fee_usd, inner_job_id, state, reason, created_at, updated_at)
            VALUES ($oj, $bk, $ta, $to, $du, $sf, $ij, $st, $rn, $now, $now);";
        cmd.Parameters.AddWithValue("$oj", (object?)outerJobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bk", buyerKey.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$ta", targetAgent.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$to", targetOffering);
        cmd.Parameters.AddWithValue("$du", (double)downstreamUsdc);
        cmd.Parameters.AddWithValue("$sf", (double)ServiceFeeUsdc);
        cmd.Parameters.AddWithValue("$ij", (object?)innerJobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", state);
        cmd.Parameters.AddWithValue("$rn", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateAuditAsync(string outerJobId, string state, string? innerJobId, string? reason, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE acppurchaser_audit
            SET state=$st, inner_job_id=COALESCE($ij, inner_job_id), reason=COALESCE($rn, reason), updated_at=$now
            WHERE id = (SELECT id FROM acppurchaser_audit WHERE outer_job_id=$oj ORDER BY id DESC LIMIT 1);";
        cmd.Parameters.AddWithValue("$st", state);
        cmd.Parameters.AddWithValue("$ij", (object?)innerJobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rn", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$oj", outerJobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
