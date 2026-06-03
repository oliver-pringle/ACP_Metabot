using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.Extensions.Logging;

namespace ACP_Metabot.Api.Services;

// Seams over the concrete LLM curator + offering index, so the money-safety
// logic is unit-testable without a chain or an LLM.
public interface IStackComposerSource
{
    Task<IReadOnlyList<StackEntry>> CurateAsync(string intent, decimal budgetUsdc, int maxSteps, CancellationToken ct);
}
public interface IStackOfferingSource
{
    Task<(string PriceType, string? RequirementSchemaJson)?> ResolveAsync(string agentAddress, string offeringName, CancellationToken ct);
}

// DI defaults: adapt StackComposerService + OfferingRepository.
public sealed class StackComposerAdapter : IStackComposerSource
{
    private readonly StackComposerService _c;
    public StackComposerAdapter(StackComposerService c) => _c = c;
    public async Task<IReadOnlyList<StackEntry>> CurateAsync(string intent, decimal budgetUsdc, int maxSteps, CancellationToken ct)
        => (await _c.ComposeAsync(intent, (double)budgetUsdc, maxSteps, null, null, ct)).Stack;
}
public sealed class StackOfferingAdapter : IStackOfferingSource
{
    private readonly OfferingRepository _repo;
    public StackOfferingAdapter(OfferingRepository repo) => _repo = repo;
    public async Task<(string PriceType, string? RequirementSchemaJson)?> ResolveAsync(string agentAddress, string offeringName, CancellationToken ct)
    {
        var offs = await _repo.ListByAgentAsync(agentAddress.ToLowerInvariant());
        var o = offs.FirstOrDefault(x => string.Equals(x.OfferingName, offeringName, StringComparison.Ordinal));
        return o is null ? null : (o.PriceType ?? "", o.RequirementSchemaJson);
    }
}

// Shared types used by the Stack Purchase Router.
public record StackPlanStep(
    string TargetAgent, string TargetOffering, string Role,
    decimal QuotedPriceUsdc, string RiskTier, Dictionary<string, object> InnerRequirement);
public record StackQuoteStep(string TargetAgent, string TargetOffering, string Role, decimal PriceUsdc, string RiskTier, string Verdict);
public record DroppedCandidate(string TargetAgent, string TargetOffering, string Reason);
public record StackQuoteResult(
    string QuoteId, string Subject, IReadOnlyList<StackQuoteStep> Steps,
    IReadOnlyList<DroppedCandidate> DroppedCandidates,
    decimal TotalDownstreamUsdc, decimal ExecuteFeeUsdc, decimal TotalEscrowUsdc,
    string Verdict, IReadOnlyList<string> Reasons, string ExpiresAt);
public record StackPrecheckResult(bool Ok, string? Reason, IReadOnlyList<StackPlanStep> Steps, decimal TotalDownstreamUsdc);

public sealed class StackPurchaserService
{
    public const decimal ExecuteFeeUsdc = 0.25m;
    private const int QuoteTtlMinutes = 15;
    private static readonly string[] AddressFieldHints =
        ["address", "wallet", "agent", "spender", "token", "account", "contract"];

    private readonly Db _db;
    private readonly StackQuoteStore _store;
    private readonly PurchaserBudgetService _budget;
    private readonly IAgentRiskSource _risk;
    private readonly IStackComposerSource _composer;
    private readonly IStackOfferingSource _offerings;
    private readonly Func<string> _idFactory;
    private readonly ILogger<StackPurchaserService> _logger;

    public StackPurchaserService(Db db, StackQuoteStore store, PurchaserBudgetService budget,
        IAgentRiskSource risk, IStackComposerSource composer, IStackOfferingSource offerings,
        Func<string> idFactory, ILogger<StackPurchaserService> logger)
    {
        _db = db; _store = store; _budget = budget; _risk = risk;
        _composer = composer; _offerings = offerings; _idFactory = idFactory; _logger = logger;
    }

    public async Task<StackQuoteResult> QuoteAsync(
        string subject, string intent, decimal maxFundsUsdc, int maxSteps, CancellationToken ct)
    {
        var subj = subject.Trim().ToLowerInvariant();
        var cap = Math.Clamp(maxSteps <= 0 ? 5 : maxSteps, 1, 5);
        var candidates = await _composer.CurateAsync(intent, maxFundsUsdc, cap, ct);

        var kept = new List<StackPlanStep>();
        var keptView = new List<StackQuoteStep>();
        var dropped = new List<DroppedCandidate>();

        foreach (var c in candidates.Take(cap))
        {
            var resolved = await _offerings.ResolveAsync(c.AgentAddress, c.OfferingName, ct);
            if (resolved is null) { dropped.Add(new(c.AgentAddress, c.OfferingName, "not_found")); continue; }
            if (!string.Equals(resolved.Value.PriceType, "fixed", StringComparison.OrdinalIgnoreCase))
            { dropped.Add(new(c.AgentAddress, c.OfferingName, "not_fixed_price")); continue; }

            var tier = await _risk.RiskTierAsync(c.AgentAddress, 8453, ct);
            if (tier == "critical") { dropped.Add(new(c.AgentAddress, c.OfferingName, "risk_critical")); continue; }

            var field = MapSubjectField(resolved.Value.RequirementSchemaJson);
            if (field is null) { dropped.Add(new(c.AgentAddress, c.OfferingName, "subject_unmappable")); continue; }

            var verdict = tier == "high" ? "CAUTION" : "PROCEED";
            var price = (decimal)c.PriceUsdc;
            kept.Add(new StackPlanStep(c.AgentAddress.ToLowerInvariant(), c.OfferingName, c.Role,
                price, tier, new Dictionary<string, object> { [field] = subj }));
            keptView.Add(new StackQuoteStep(c.AgentAddress.ToLowerInvariant(), c.OfferingName, c.Role, price, tier, verdict));
        }

        var total = kept.Sum(s => s.QuotedPriceUsdc);
        var reasons = new List<string>();

        if (kept.Count == 0)
        {
            reasons.Add("no_buyable_steps");
            return new StackQuoteResult("", subj, keptView, dropped, 0, ExecuteFeeUsdc, 0, "BLOCK", reasons, "");
        }
        if (total > maxFundsUsdc)
        {
            reasons.Add("over_max_funds");
            return new StackQuoteResult("", subj, keptView, dropped, total, ExecuteFeeUsdc,
                decimal.Round(total + ExecuteFeeUsdc, 4), "BLOCK", reasons, "");
        }

        var quoteId = _idFactory();
        var expires = DateTime.UtcNow.AddMinutes(QuoteTtlMinutes);
        await _store.SaveAsync(quoteId, buyerKey: "", subject: subj, kept, total, ExecuteFeeUsdc, expires, ct);
        reasons.Add("ok");
        return new StackQuoteResult(quoteId, subj, keptView, dropped, total, ExecuteFeeUsdc,
            decimal.Round(total + ExecuteFeeUsdc, 4),
            kept.Any(k => k.RiskTier == "high") ? "CAUTION" : "PROCEED",
            reasons, expires.ToString("O"));
    }

    public async Task<StackPrecheckResult> PrecheckAsync(
        string outerJobId, string buyerKey, string quoteId, string subject, CancellationToken ct)
    {
        var buyer = buyerKey.Trim().ToLowerInvariant();
        var subj = subject.Trim().ToLowerInvariant();
        var q = await _store.LoadActiveAsync(quoteId, DateTime.UtcNow, ct);
        if (q is null) return new StackPrecheckResult(false, "quote_expired_or_not_found", Array.Empty<StackPlanStep>(), 0);
        if (!string.Equals(q.Subject, subj, StringComparison.Ordinal))
            return new StackPrecheckResult(false, "subject_mismatch", Array.Empty<StackPlanStep>(), 0);
        if (!string.IsNullOrEmpty(q.BuyerKey) && !string.Equals(q.BuyerKey, buyer, StringComparison.Ordinal))
            return new StackPrecheckResult(false, "buyer_mismatch", Array.Empty<StackPlanStep>(), 0);

        if (string.IsNullOrEmpty(q.BuyerKey))
            await _store.SaveAsync(quoteId, buyer, q.Subject, q.Steps, q.TotalDownstreamUsd, q.ExecuteFeeUsd,
                DateTime.Parse(q.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind), ct);

        var reserve = await _budget.TryReserveAsync(buyer, q.TotalDownstreamUsd, ct);
        if (!reserve.Reserved)
        {
            await WriteAuditAsync(outerJobId, buyer, "(stack)", "stack_execute", q.TotalDownstreamUsd, "REJECTED", "daily_cap_exceeded", null, ct);
            return new StackPrecheckResult(false, "daily_cap_exceeded", Array.Empty<StackPlanStep>(), 0);
        }
        await WriteAuditAsync(outerJobId, buyer, "(stack)", "stack_execute", q.TotalDownstreamUsd, "PRECHECK", null, null, ct);
        return new StackPrecheckResult(true, null, q.Steps, q.TotalDownstreamUsd);
    }

    public async Task SettleAsync(string outerJobId, string buyerKey, string state,
        string? innerJobIds, string? reason, decimal totalDownstreamUsd, CancellationToken ct)
    {
        if (state == "REJECTED")
            await _budget.RecordActualSpendAsync(buyerKey.Trim().ToLowerInvariant(), -totalDownstreamUsd, ct);
        await UpdateAuditAsync(outerJobId, state, innerJobIds, reason, ct);
    }

    // Finds the single string property whose name hints at an address. Conservative:
    // returns null on zero or multiple matches (money-safety -- never guess).
    internal static string? MapSubjectField(string? requirementSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(requirementSchemaJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(requirementSchemaJson);
            if (!doc.RootElement.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                return null;
            string? match = null;
            foreach (var p in props.EnumerateObject())
            {
                var isString = p.Value.TryGetProperty("type", out var t) && t.GetString() == "string";
                if (!isString) continue;
                var name = p.Name.ToLowerInvariant();
                if (!AddressFieldHints.Any(h => name.Contains(h))) continue;
                if (match is not null) return null; // ambiguous -> drop
                match = p.Name;
            }
            return match;
        }
        catch { return null; }
    }

    private async Task WriteAuditAsync(string outerJobId, string buyerKey, string targetAgent,
        string targetOffering, decimal downstreamUsd, string state, string? reason, string? innerJobId, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO acppurchaser_audit
            (outer_job_id, buyer_key, target_agent, target_offering, downstream_usd, service_fee_usd,
             inner_job_id, state, reason, created_at, updated_at)
            VALUES ($ojid, $bk, $ta, $to, $d, $sf, $ij, $s, $r, $now, $now);";
        cmd.Parameters.AddWithValue("$ojid", outerJobId);
        cmd.Parameters.AddWithValue("$bk", buyerKey);
        cmd.Parameters.AddWithValue("$ta", targetAgent);
        cmd.Parameters.AddWithValue("$to", targetOffering);
        cmd.Parameters.AddWithValue("$d", (double)downstreamUsd);
        cmd.Parameters.AddWithValue("$sf", (double)ExecuteFeeUsdc);
        cmd.Parameters.AddWithValue("$ij", innerJobId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$r", reason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateAuditAsync(string outerJobId, string state, string? innerJobIds, string? reason, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE acppurchaser_audit SET state=$s, inner_job_id=$ij, reason=$r, updated_at=$now
            WHERE outer_job_id=$ojid AND state IN ('PRECHECK','PENDING');";
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$ij", innerJobIds ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$r", reason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$ojid", outerJobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
