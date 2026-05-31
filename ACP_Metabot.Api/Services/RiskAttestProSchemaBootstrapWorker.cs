using System.Text.Json;
using ACP_Metabot.Api.Data;
using Microsoft.Extensions.Hosting;

namespace ACP_Metabot.Api.Services;

// v1.0 riskAttestPro Task 7 — idempotent EAS schema bootstrap, wired in v1.0.2.
//
// On StartAsync:
//   1. Read local risk_attest_pro_bootstrap_state. If schema_uid is present, skip.
//   2. Look up the schema in EASIssuer's local DB via
//      GET easissuer-api:5000/v1/internal/schema/by-string. If found (a prior
//      boot or buyer hire already paid for the on-chain register), persist
//      the discovered UID locally + done. This is read-only, no gas burn.
//   3. If still missing AND RISK_ATTEST_PRO_ENABLE_SCHEMA_REGISTER=true,
//      POST easissuer-api:5000/v1/schema/register. This BURNS mainnet ETH
//      gas paid from EAS_OPERATOR_PRIVATE_KEY (~$0.50 of ETH on Base). Persist
//      the resulting UID. Default OFF — operators must opt-in by env-flag
//      because cold-boot loops would otherwise auto-burn gas.
//   4. If still missing AND flag is OFF, log + return. Next boot retries.
//
// Tests inject `registerInjector` directly to dodge the live HTTP lane.
// `peerClients` is an optional 4th ctor param so existing tests keep their
// 3-arg construction. DI auto-provides it when the bot boots.
public sealed class RiskAttestProSchemaBootstrapWorker : IHostedService
{
    // Canonical riskAttestPro AgentRisk attestation schema. Field order is
    // load-bearing: changing it would require a new schema UID and break
    // every previously-anchored attestation's verifier lookup.
    private const string SchemaString =
        "address wallet, uint8 scorePro, string verdict, uint64 generatedAt, bytes32 componentsHash, string summaryHash";

    private static readonly bool RegisterEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("RISK_ATTEST_PRO_ENABLE_SCHEMA_REGISTER"),
            "true", StringComparison.OrdinalIgnoreCase);

    private readonly Db _db;
    private readonly ILogger<RiskAttestProSchemaBootstrapWorker> _log;
    private readonly Func<string, Task<string>>? _injector;
    private readonly IRiskPeerClients? _peer;

    public RiskAttestProSchemaBootstrapWorker(
        Db db,
        ILogger<RiskAttestProSchemaBootstrapWorker> log,
        Func<string, Task<string>>? registerInjector = null,
        IRiskPeerClients? peerClients = null)
    {
        _db = db;
        _log = log;
        _injector = registerInjector;
        _peer = peerClients;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var existing = await ReadExistingUidAsync(ct);
        if (existing is not null)
        {
            _log.LogInformation("riskAttestPro schema already in local state UID={Uid}", existing);
            return;
        }

        // Step 1: cheap lookup against EASIssuer's local DB.
        // Skipped when no peer client is wired (unit tests) — they exercise
        // the register path via the injector instead.
        if (_peer is not null)
        {
            try
            {
                using var doc = await _peer.LookupEasSchemaByStringAsync(SchemaString, ct);
                var found = ExtractFoundSchemaUid(doc);
                if (found is not null)
                {
                    await PersistAsync(found, ct);
                    _log.LogInformation(
                        "riskAttestPro schema discovered via EASIssuer lookup UID={Uid}", found);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "riskAttestPro EASIssuer lookup failed; will try register if enabled");
            }
        }

        // Step 2: register. Only when explicitly opted-in via env, OR when a
        // test injector is supplied (tests bypass the env guard).
        if (_injector is null && !RegisterEnabled)
        {
            _log.LogInformation(
                "riskAttestPro schema not registered; set RISK_ATTEST_PRO_ENABLE_SCHEMA_REGISTER=true to opt-in (one-time gas burn)");
            return;
        }

        try
        {
            var newUid = _injector is not null
                ? await _injector(SchemaString)
                : await RegisterViaPeerAsync(ct);
            await PersistAsync(newUid, ct);
            _log.LogInformation("riskAttestPro schema registered UID={Uid}", newUid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "riskAttestPro schema registration deferred (will retry next boot)");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<string> RegisterViaPeerAsync(CancellationToken ct)
    {
        if (_peer is null)
            throw new InvalidOperationException(
                "IRiskPeerClients not available; cannot register schema");
        using var doc = await _peer.RegisterEasSchemaAsync(SchemaString, ct);
        if (doc is null)
            throw new InvalidOperationException(
                "EASIssuer /v1/schema/register returned no body (HTTP error masked to null)");
        if (!doc.RootElement.TryGetProperty("schemaUid", out var uidEl) || uidEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(
                "EASIssuer /v1/schema/register response missing schemaUid");
        var uid = uidEl.GetString();
        if (string.IsNullOrWhiteSpace(uid))
            throw new InvalidOperationException(
                "EASIssuer /v1/schema/register returned empty schemaUid");
        return uid!;
    }

    private static string? ExtractFoundSchemaUid(JsonDocument? doc)
    {
        if (doc is null) return null;
        if (!doc.RootElement.TryGetProperty("found", out var foundEl)) return null;
        if (foundEl.ValueKind != JsonValueKind.True) return null;
        if (!doc.RootElement.TryGetProperty("schemaUid", out var uidEl)) return null;
        if (uidEl.ValueKind != JsonValueKind.String) return null;
        var uid = uidEl.GetString();
        return string.IsNullOrWhiteSpace(uid) ? null : uid;
    }

    private async Task<string?> ReadExistingUidAsync(CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT schema_uid FROM risk_attest_pro_bootstrap_state LIMIT 1;";
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    private async Task PersistAsync(string uid, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO risk_attest_pro_bootstrap_state (schema_uid, registered_at)
            VALUES ($u, strftime('%Y-%m-%dT%H:%M:%fZ','now'));";
        cmd.Parameters.AddWithValue("$u", uid);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
