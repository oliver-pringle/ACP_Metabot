using ACP_Metabot.Api.Data;
using Microsoft.Extensions.Hosting;

namespace ACP_Metabot.Api.Services;

// v1.0 riskAttestPro Task 7 — idempotent EAS schema bootstrap.
//
// On StartAsync, check risk_attest_pro_bootstrap_state. If a schema_uid is
// already persisted, log and skip. Otherwise call the registration function
// (live wiring deferred to v1.0.1 — production DI factory will plumb this to
// the EASIssuer cross-bot lane, easissuer-api:5000/v1/internal/schema POST
// with the attest_schema $1.00 internal call). On success, persist the UID
// via INSERT OR IGNORE so two startups racing the same DB still leave a
// single row. On failure, log a warning and skip — next boot retries.
//
// Tests inject `registerInjector` directly to dodge the live HTTP lane.
public sealed class RiskAttestProSchemaBootstrapWorker : IHostedService
{
    // Canonical riskAttestPro AgentRisk attestation schema. Field order is
    // load-bearing: changing it would require a new schema UID and break
    // every previously-anchored attestation's verifier lookup.
    private const string SchemaString =
        "address wallet, uint8 scorePro, string verdict, uint64 generatedAt, bytes32 componentsHash, string summaryHash";

    private readonly Db _db;
    private readonly ILogger<RiskAttestProSchemaBootstrapWorker> _log;
    private readonly Func<string, Task<string>>? _injector;

    public RiskAttestProSchemaBootstrapWorker(
        Db db,
        ILogger<RiskAttestProSchemaBootstrapWorker> log,
        Func<string, Task<string>>? registerInjector = null)
    {
        _db = db;
        _log = log;
        _injector = registerInjector;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var existing = await ReadExistingUidAsync(ct);
        if (existing is not null)
        {
            _log.LogInformation("riskAttestPro schema already registered UID={Uid}", existing);
            return;
        }

        var register = _injector ?? RegisterViaEasIssuer;
        try
        {
            var newUid = await register(SchemaString);
            await PersistAsync(newUid, ct);
            _log.LogInformation("riskAttestPro schema registered UID={Uid}", newUid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "riskAttestPro schema registration deferred (will retry next boot)");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

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

    // v1.0 placeholder — production wiring lands in v1.0.1 when the EASIssuer
    // attest_schema cross-bot lane is plumbed. The NotImplementedException
    // is caught upstream + reduces to a LogWarning so the bot still boots.
    private static Task<string> RegisterViaEasIssuer(string schemaString) =>
        throw new NotImplementedException(
            "EAS schema registration wiring deferred to v1.0.1; tests use injector");
}
