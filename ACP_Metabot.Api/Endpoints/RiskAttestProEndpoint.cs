// v1.0 riskAttestPro Task 8 — POST /v1/risk/attest-pro endpoint helper.
//
// Factored out of Program.cs so the cache-read / service-call / cache-write
// / 502-on-floor-breach behavior can be unit-tested without standing up a
// WebApplicationFactory. Program.cs registers a thin
// `app.MapPost("/v1/risk/attest-pro", ...)` that delegates straight to
// HandleAsync; tests construct a real Db + a counting service shim and call
// HandleAsync directly.
//
// Cache key: lower(wallet) + ":" + chain. TTL: 1h. Cache rows survive process
// restarts since they live in the same SQLite file as the rest of Metabot's
// state. The cache is bypassed when the request body sets `fresh: true`.

using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.AspNetCore.Http;

namespace ACP_Metabot.Api.Endpoints;

/// <summary>
/// Wire shape for <c>POST /v1/risk/attest-pro</c>.
///
/// <para><see cref="BuyerSignature"/> is surfaced forward for v1.1 strict-
/// mode binding (per spec Task 8: "currently unused — surface forward;
/// v1.1 makes it required in strict mode"). Endpoints accept it on v1.0
/// without acting on it so buyers can ship signing today and the gate
/// flips later without a schema change.</para>
/// </summary>
public sealed record RiskAttestProRequest(
    string? WalletAddress,
    string? Chain,
    string? BuyerSignature,
    bool? Fresh);

/// <summary>
/// Static handler over <see cref="RiskAttestProService"/>. The service
/// itself is injected as a delegate so tests can swap in a counting shim
/// without standing up the full peer-client + LLM + trajectory graph.
/// </summary>
public static class RiskAttestProEndpoint
{
    /// <summary>
    /// Executes the endpoint logic: validates input, checks the 1h
    /// wallet-response cache, invokes <paramref name="generate"/> on miss,
    /// writes the response back to <c>risk_attest_pro_cache</c>, and
    /// returns the body decorated with a <c>cacheHit</c> flag.
    /// </summary>
    /// <param name="req">Request body bound from JSON.</param>
    /// <param name="db">Metabot's SQLite handle.</param>
    /// <param name="generate">Bridge to <see cref="RiskAttestProService.GenerateAsync"/>;
    /// passed as a delegate so tests can count invocations / inject failures.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>An <see cref="IResult"/> ready for Minimal-API dispatch.</returns>
    public static async Task<IResult> HandleAsync(
        RiskAttestProRequest req,
        Db db,
        Func<string, string, CancellationToken, Task<RiskAttestProResult>> generate,
        CancellationToken ct)
    {
        // Required: walletAddress.
        if (string.IsNullOrWhiteSpace(req.WalletAddress))
            return JsonError(400,
                JsonSerializer.Serialize(new
                {
                    error = "invalid_address",
                    field = "walletAddress",
                    message = "walletAddress is required",
                }));
        var wallet = req.WalletAddress.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(wallet, "^0x[0-9a-f]{40}$"))
            return JsonError(400,
                JsonSerializer.Serialize(new
                {
                    error = "invalid_address",
                    field = "walletAddress",
                    message = "must be 0x followed by 40 hex chars",
                }));

        // Optional: chain. Mirrors the existing risk-family endpoints —
        // default 'base', only 'base' or 'ethereum' accepted.
        var chain = string.IsNullOrWhiteSpace(req.Chain)
            ? "base"
            : req.Chain.Trim().ToLowerInvariant();
        if (chain is not ("base" or "ethereum"))
            return JsonError(400,
                JsonSerializer.Serialize(new
                {
                    error = "chain must be 'base' or 'ethereum'",
                }));

        var walletChain = wallet + ":" + chain;
        var fresh = req.Fresh == true;

        // ── Cache hit fast-path ─────────────────────────────────────────────
        // The cache is keyed on (wallet, chain) and rows are valid for 1h.
        // Stale rows are NOT GC'd here — they're harmless (PK collision on
        // INSERT OR REPLACE) but a v1.1 sweep worker can prune by
        // expires_at < now if the table grows beyond cosmetic.
        if (!fresh)
        {
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT response_json
                FROM risk_attest_pro_cache
                WHERE wallet_chain = $w
                  AND expires_at > $now
                LIMIT 1;";
            cmd.Parameters.AddWithValue("$w", walletChain);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            var raw = await cmd.ExecuteScalarAsync(ct);
            if (raw is string cached && !string.IsNullOrEmpty(cached))
            {
                using var doc = JsonDocument.Parse(cached);
                var withFlag = AppendCacheHit(doc.RootElement, cacheHit: true);
                return new RawJsonResult(withFlag, statusCode: 200);
            }
        }

        // ── Generate ────────────────────────────────────────────────────────
        RiskAttestProResult result;
        try
        {
            result = await generate(wallet, chain, ct);
        }
        catch (InsufficientSignalsException ex)
        {
            return JsonError(502,
                JsonSerializer.Serialize(new
                {
                    error = "INSUFFICIENT_SIGNALS",
                    reason = ex.Message,
                }));
        }

        // Serialize the result to canonical JSON (excluding cacheHit, which we
        // inject below). We persist the SAME shape we're about to return so a
        // cache hit round-trips identically — see AppendCacheHit's
        // 'false-then-flip' contract.
        var resultJson = JsonSerializer.Serialize(result, JsonOpts);

        // ── Cache write ─────────────────────────────────────────────────────
        // INSERT OR REPLACE so re-warming the same wallet:chain (e.g. via
        // ?fresh=true) doesn't multiply rows. The attestation_uid column is
        // populated from result.attestation.uid when present; v1.0 ships
        // before the live EAS-publish wiring lands (see Task 7) so result
        // bodies typically lack that block — write empty string in that case.
        // We never throw on cache-write failure; the buyer already got their
        // result, persistence is best-effort.
        try
        {
            var attestationUid = ExtractAttestationUid(result);
            var generatedAt = DateTimeOffset.UtcNow;
            var expiresAt = generatedAt.AddHours(1);
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO risk_attest_pro_cache
                    (wallet_chain, response_json, attestation_uid, generated_at, expires_at)
                VALUES ($w, $r, $u, $g, $e);";
            cmd.Parameters.AddWithValue("$w", walletChain);
            cmd.Parameters.AddWithValue("$r", resultJson);
            cmd.Parameters.AddWithValue("$u", attestationUid ?? "");
            cmd.Parameters.AddWithValue("$g", generatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$e", expiresAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Best-effort persistence — never bubble.
        }

        // Decorate the success body with cacheHit:false and return.
        using var resultDoc = JsonDocument.Parse(resultJson);
        var body = AppendCacheHit(resultDoc.RootElement, cacheHit: false);
        return new RawJsonResult(body, statusCode: 200);
    }

    /// <summary>
    /// Minimal <see cref="IResult"/> that writes a pre-serialized JSON body
    /// to the response without touching the DI container. We need this
    /// because <see cref="Results.Content(string, string?, System.Text.Encoding?, int?)"/>
    /// and <see cref="Results.Json"/> both call <c>GetRequiredService</c> for
    /// response-stream infrastructure that's only available inside a real
    /// ASP.NET pipeline — which makes the unit tests harder than they need
    /// to be. This shim writes the body byte-for-byte and lets the test
    /// just read <c>ctx.Response.Body</c>.
    /// </summary>
    private sealed class RawJsonResult : IResult
    {
        private readonly string _json;
        private readonly int _statusCode;
        public RawJsonResult(string json, int statusCode = 200)
        {
            _json = json;
            _statusCode = statusCode;
        }
        public async Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = _statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var bytes = System.Text.Encoding.UTF8.GetBytes(_json);
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        }
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Test-friendly equivalent of <see cref="Results.Json"/> for non-200
    /// envelopes. Used for the 400 input-validation rejects + the 502
    /// INSUFFICIENT_SIGNALS envelope.
    /// </summary>
    private static IResult JsonError(int statusCode, string json)
        => new RawJsonResult(json, statusCode);

    /// <summary>
    /// Returns a JSON string of <paramref name="root"/> with one extra
    /// top-level boolean property <c>cacheHit</c>. We intentionally do NOT
    /// mutate the <see cref="RiskAttestProResult"/> record itself — adding a
    /// property would ripple across the existing Task 6 service tests and
    /// the marketplace deliverableSchema. An anonymous-wrapper serialization
    /// is the cheapest forward-compatible decoration.
    /// </summary>
    private static string AppendCacheHit(JsonElement root, bool cacheHit)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                // If the cached body already carries a cacheHit field (e.g.
                // because someone shipped v1.0.1 and re-cached after a flag
                // flip), we drop it — the endpoint owns the field.
                if (prop.NameEquals("cacheHit")) continue;
                prop.WriteTo(writer);
            }
            writer.WriteBoolean("cacheHit", cacheHit);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Pulls the real EAS <c>AttestationUid</c> off the result when v1.0.3
    /// publish succeeded; falls back to <c>ComponentsHash</c> so the cache
    /// row's attestation_uid column stays non-empty + queryable by ops even
    /// when the on-chain anchor is unavailable.
    /// </summary>
    private static string? ExtractAttestationUid(RiskAttestProResult result)
    {
        if (result.Attestation is { AttestationUid: { Length: > 0 } realUid })
            return realUid;
        return result.ComponentsHash;
    }
}
