using System.Text.Json;
using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Data;

public record StoredStackQuote(
    string QuoteId, string BuyerKey, string Subject,
    IReadOnlyList<StackPlanStep> Steps, decimal TotalDownstreamUsd, decimal ExecuteFeeUsd, string ExpiresAt);

/// <summary>ADO.NET persistence for the Stack Purchase Router's price-bound plans.</summary>
public sealed class StackQuoteStore
{
    private readonly Db _db;
    public StackQuoteStore(Db db) => _db = db;

    public async Task SaveAsync(string quoteId, string buyerKey, string subject,
        IReadOnlyList<StackPlanStep> steps, decimal totalDownstreamUsd, decimal executeFeeUsd,
        DateTime expiresAtUtc, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO acppurchaser_stack_quotes
            (quote_id, buyer_key, subject, steps_json, total_downstream_usd, execute_fee_usd, expires_at, created_at)
            VALUES ($q, $b, $s, $j, $td, $fee, $exp, $now);";
        cmd.Parameters.AddWithValue("$q", quoteId);
        cmd.Parameters.AddWithValue("$b", buyerKey.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$s", subject.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(steps));
        cmd.Parameters.AddWithValue("$td", (double)totalDownstreamUsd);
        cmd.Parameters.AddWithValue("$fee", (double)executeFeeUsd);
        cmd.Parameters.AddWithValue("$exp", expiresAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task<StoredStackQuote?> LoadAsync(string quoteId, CancellationToken ct)
        => LoadCoreAsync(quoteId, null, ct);

    /// <summary>Returns null if missing OR expires_at &lt;= nowUtc.</summary>
    public Task<StoredStackQuote?> LoadActiveAsync(string quoteId, DateTime nowUtc, CancellationToken ct)
        => LoadCoreAsync(quoteId, nowUtc, ct);

    private async Task<StoredStackQuote?> LoadCoreAsync(string quoteId, DateTime? activeAsOf, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT buyer_key, subject, steps_json, total_downstream_usd, execute_fee_usd, expires_at
                            FROM acppurchaser_stack_quotes WHERE quote_id = $q;";
        cmd.Parameters.AddWithValue("$q", quoteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var expIso = r.GetString(5);
        if (activeAsOf is DateTime now && DateTime.Parse(expIso, null, System.Globalization.DateTimeStyles.RoundtripKind) <= now)
            return null;
        var steps = JsonSerializer.Deserialize<List<StackPlanStep>>(r.GetString(2)) ?? new();
        return new StoredStackQuote(quoteId, r.GetString(0), r.GetString(1), steps,
            (decimal)r.GetDouble(3), (decimal)r.GetDouble(4), expIso);
    }
}
