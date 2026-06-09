# On-demand `acp_security_scan` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the operator run SecurityBot's scan against any particular marketplace bot **on demand** — from the `acp-find` plugin (`acp_security_scan`) → Metabot's X-API-Key-gated `POST /admin/securityScan` → a shared `SecurityScanService.ScanAndPersistAsync` that scans, upserts the latest-verdict cache, and appends a full history row, returning the verdict + per-finding detail. The on-demand path and the background `SecurityScanWorker` go through the **same** persist method (one way a verdict is written).

**Architecture:** Extract the per-agent "scan → upsert cache → append history" currently inlined in `SecurityScanWorker.TickOnceAsync` into a new DI singleton `SecurityScanService` (depends on `ITheSecurityBotClient` only; repos passed in by the caller from its own scope). The worker is refactored to call it per agent (behaviour identical). A new operator-only `POST /admin/securityScan` (gated by Metabot's existing inline X-API-Key middleware, since `/admin/*` is not in the bypass list — precedent `/admin/pulse/tick-now`) validates `{agentAddress}`, calls `ScanAndPersistAsync`, and returns the full projection (incl. `findings[]` parsed from the persisted `RawFindingsJson`, never `lastError`). The plugin adds an `acp_security_scan` tool that calls the gated endpoint via the existing `callGateway` (sends `X-API-Key` from `ACP_API_KEY`).

**Tech Stack:** C# / .NET 10, ADO.NET + `Microsoft.Data.Sqlite` (WAL), ASP.NET minimal APIs, `IHttpClientFactory`, xUnit. Node/JS only for the plugin tool. No EF Core / no other ORM.

**Approved design:** `docs/superpowers/specs/2026-06-09-metabot-ondemand-security-scan-design.md`

**Builds on (already SHIPPED + verified in-tree):** `docs/superpowers/plans/2026-06-08-metabot-security-vetted-today.md` — `SecurityScanWorker`, `SecurityVerdictRepository`, `SecurityScanHistoryRepository`, `TheSecurityBotClient`/`ScanResult`, the `security_verdicts` + `security_scan_history` tables, and their tests all exist on disk today. This plan reuses those exact signatures verbatim.

**Standing constraints for the executor:**
- **No `git push`, no droplet deploy.** All commits are LOCAL-only. The ops/deploy + plugin-republish steps are documented in Task 6 for a later, manual session.
- The endpoint is **operator-only** — gated by Metabot's `INTERNAL_API_KEY` via the existing X-API-Key middleware. No new env var on Metabot (reuses the already-wired `THESECURITYBOT_API_KEY` for the cross-bot call).
- Commit messages end with the trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage only the named files per commit — **never `git add -A`** (the repos carry unrelated WIP).
- The Metabot C# work runs from working dir `C:\code_crypto\ACP\ACP_Metabot\ACP_Metabot`. The plugin work (Task 5) runs from the `acp-find-plugin` repo root.

---

## File Structure

**Create:**
- `ACP_Metabot.Api/Services/SecurityScanService.cs` — the shared scan-and-persist seam: `ScanAndPersistAsync(string agentAddress, SecurityVerdictRepository repo, SecurityScanHistoryRepository historyRepo, CancellationToken ct) -> Task<ScanResult>`. DI singleton, depends on `ITheSecurityBotClient` only.
- `ACP_Metabot.Api/Endpoints/SecurityScanEndpoint.cs` — request DTO + static `HandleAsync` helper (validate → `ScanAndPersistAsync` → project verdict + findings, drop `lastError`) that returns a custom `RawJsonResult : IResult` (plus `JsonError(int,string)` + camelCase `JsonOpts`), all lifted verbatim from `RiskAttestProEndpoint`. Factored out + custom-IResult so it is unit-testable against a bare `DefaultHttpContext` without a WebApplicationFactory (`Results.Ok`/`Results.BadRequest` throw `ArgumentNullException` there).
- `ACP_Metabot.Api.Tests/SecurityScanServiceTests.cs` — fake client; asserts upsert + one history row + returns the `ScanResult`; re-run appends a 2nd history row while the cache stays one row.
- `ACP_Metabot.Api.Tests/SecurityScanEndpointTests.cs` — handler-level tests (400 bad address, 200 verdict+findings + both tables written + no `lastError` leak, `error`/`not_auditable` → 200). 401-without-key is covered by the middleware (documented; the handler is never reached unauthenticated).

**Modify:**
- `ACP_Metabot.Api/Services/SecurityScanWorker.cs` — `TickOnceAsync` resolves `SecurityScanService` from its per-tick scope and calls `ScanAndPersistAsync(stale[i], repo, historyRepo, ct)` instead of the inlined scan+upsert+append; the now-unused `ITheSecurityBotClient client` ctor param + `_client` field are removed (the service is the sole client caller now).
- `ACP_Metabot.Api.Tests/SecurityScanWorkerTests.cs` — register `SecurityScanService` (+ the per-test FakeClient) in the fixture's `ServiceCollection` so the worker scope resolves it; `MakeWorker` constructs the worker with no client arg; the 6 existing worker tests stay green.
- `ACP_Metabot.Api/Program.cs` — register `SecurityScanService` as a singleton next to the security repos; add the `POST /admin/securityScan` route (thin wrapper → `SecurityScanEndpoint.HandleAsync`).
- `acp-find-plugin/mcp-server/server.js` — add the `acp_security_scan` TOOLS entry + HANDLERS function (`callGateway` POST `/admin/securityScan`, sends X-API-Key via the existing `ACP_API_KEY`/`SEND_API_KEY`; clear "operator key required" error when unset; `agentAddress` required + normalized; add `marketplaceUrl`).

**Why these boundaries:** `SecurityScanService` is the single write-path the spec mandates — the worker (timing/batching) and the endpoint (operator trigger) both delegate to it. `SecurityScanEndpoint` follows the codebase's proven static-helper test seam (`ACP_Metabot.Api/Endpoints/RiskAttestProEndpoint.cs`) — the test project has **no** WebApplicationFactory plumb (see `RiskAttestProEndpointTests` header comment), so endpoint logic is unit-tested by calling `HandleAsync` directly and round-tripping the `IResult` through a `DefaultHttpContext`. To make that round-trip work, `HandleAsync` returns a custom `RawJsonResult : IResult` (copied verbatim from `RiskAttestProEndpoint`) that serializes the body and writes bytes straight to `ctx.Response.Body` — NOT `Results.Ok`/`Results.BadRequest`, which throw `ArgumentNullException (Parameter 'provider')` against a bare `DefaultHttpContext` (null `RequestServices`).

---

### Task 1: `SecurityScanService` — the shared scan-and-persist seam

**Files:**
- Create: `ACP_Metabot.Api/Services/SecurityScanService.cs`
- Test: `ACP_Metabot.Api.Tests/SecurityScanServiceTests.cs`

This extracts the exact three-call sequence the worker runs today (scan → upsert cache → append history), in the same order with the same arguments, into one reusable method. The repos are passed IN by the caller (the worker from its per-tick scope, the endpoint from the request scope) so the service stays free of scope concerns and matches the existing repo-as-singleton wiring.

- [ ] **Step 1: Write the failing tests**

Create `ACP_Metabot.Api.Tests/SecurityScanServiceTests.cs`:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class SecurityScanServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;
    private readonly SecurityScanHistoryRepository _histRepo;

    public SecurityScanServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_secsvc_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new SecurityVerdictRepository(_db);
        _histRepo = new SecurityScanHistoryRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    // Fake client: records each scanned address, returns a scanned verdict + raw
    // findings JSON + raw verdict discriminator (same ScanResult shape the worker uses).
    private sealed class FakeClient : ITheSecurityBotClient
    {
        public readonly List<string> Scanned = new();
        public Func<string, SecurityVerdict>? Map;
        public string? RawFindingsJson = "[{\"patternId\":\"P9\",\"severity\":\"High\"}]";
        public string? RawVerdict = "PASS";
        public Task<ScanResult> ScanAsync(string agentAddress, CancellationToken ct = default)
        {
            Scanned.Add(agentAddress);
            var v = Map?.Invoke(agentAddress)
                ?? new SecurityVerdict(agentAddress, SecurityStatus.Scanned, 90, "A", 11, 1, "{\"High\":1}",
                    DateTime.UtcNow.ToString("O"), null, null);
            return Task.FromResult(new ScanResult(v, RawFindingsJson, RawVerdict));
        }
    }

    [Fact]
    public async Task ScanAndPersist_UpsertsCache_AppendsOneHistoryRow_ReturnsResult()
    {
        var client = new FakeClient();
        var svc = new SecurityScanService(client);

        var result = await svc.ScanAndPersistAsync("0xabc", _repo, _histRepo, CancellationToken.None);

        // Returns the ScanResult the client produced.
        Assert.NotNull(result);
        Assert.Equal(SecurityStatus.Scanned, result.Verdict.Status);
        Assert.Equal("[{\"patternId\":\"P9\",\"severity\":\"High\"}]", result.RawFindingsJson);
        Assert.Equal("PASS", result.RawVerdict);
        Assert.Single(client.Scanned);

        // (a) latest-verdict cache upserted.
        var cached = await _repo.GetByAgentAsync("0xabc");
        Assert.NotNull(cached);
        Assert.Equal(90, cached!.Score);

        // (b) exactly one history row appended, retaining the full findings JSON.
        var hist = await _histRepo.ListByAgentAsync("0xabc");
        Assert.Single(hist);
        Assert.Equal("[{\"patternId\":\"P9\",\"severity\":\"High\"}]", hist[0].FindingsJson);
        Assert.Equal("PASS", hist[0].Verdict);
    }

    [Fact]
    public async Task ScanAndPersist_Rerun_AppendsSecondHistoryRow_OneCacheRow()
    {
        var client = new FakeClient();
        var svc = new SecurityScanService(client);

        await svc.ScanAndPersistAsync("0xa", _repo, _histRepo, CancellationToken.None);
        await svc.ScanAndPersistAsync("0xa", _repo, _histRepo, CancellationToken.None);

        // append: two history rows.
        Assert.Equal(2, (await _histRepo.ListByAgentAsync("0xa")).Count);
        // upsert: single cache row (GetByAgent returns the latest, not a duplicate).
        Assert.NotNull(await _repo.GetByAgentAsync("0xa"));
    }

    [Fact]
    public async Task ScanAndPersist_ErrorVerdict_StillPersistsCacheAndHistory()
    {
        var client = new FakeClient
        {
            Map = a => new SecurityVerdict(a, SecurityStatus.Error, null, null, null, null, null,
                DateTime.UtcNow.ToString("O"), null, "HTTP 500"),
            RawFindingsJson = null,
            RawVerdict = null,
        };
        var svc = new SecurityScanService(client);

        var result = await svc.ScanAndPersistAsync("0xerr", _repo, _histRepo, CancellationToken.None);

        Assert.Equal(SecurityStatus.Error, result.Verdict.Status);
        var cached = await _repo.GetByAgentAsync("0xerr");
        Assert.Equal(SecurityStatus.Error, cached!.Status);
        var hist = await _histRepo.ListByAgentAsync("0xerr");
        Assert.Single(hist);
        Assert.Equal(SecurityStatus.Error, hist[0].Status);
        Assert.Null(hist[0].FindingsJson);
        Assert.Equal("HTTP 500", hist[0].LastError); // stored server-side in history
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityScanServiceTests"`
(working dir: `C:\code_crypto\ACP\ACP_Metabot\ACP_Metabot`)
Expected: FAIL to compile — `SecurityScanService` does not exist.

- [ ] **Step 3: Write the service**

Create `ACP_Metabot.Api/Services/SecurityScanService.cs`:

```csharp
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// The single write-path for a SecurityBot verdict. Extracted from
/// SecurityScanWorker.TickOnceAsync so the background worker (timing / batching /
/// stale-selection) and the on-demand operator endpoint (POST /admin/securityScan)
/// both persist a scan IDENTICALLY — there is exactly one way a verdict is written.
///
/// Depends on ITheSecurityBotClient only; the repos are passed IN by the caller,
/// which already resolves them from its own scope (the worker from its per-tick
/// scope, the endpoint from the request scope). This keeps the service free of
/// scope concerns and matches the existing repo-as-singleton wiring. Registered as
/// a DI singleton.
/// </summary>
public sealed class SecurityScanService
{
    private readonly ITheSecurityBotClient _client;

    public SecurityScanService(ITheSecurityBotClient client) => _client = client;

    /// <summary>
    /// Scan the target agent over acp-shared (free internal path), upsert the
    /// latest-verdict cache, append one immutable history row retaining the full
    /// result, and return the ScanResult so the caller can surface full detail.
    ///
    /// Cache FIRST (keeps the digest correct), then append; a crash strictly
    /// between the two re-captures next scan. ScanAsync NEVER throws — a non-2xx /
    /// transport / parse failure arrives as a status=error verdict, which is still
    /// persisted (honest outcome) and returned.
    /// </summary>
    public async Task<ScanResult> ScanAndPersistAsync(
        string agentAddress,
        SecurityVerdictRepository repo,
        SecurityScanHistoryRepository historyRepo,
        CancellationToken ct)
    {
        var result = await _client.ScanAsync(agentAddress, ct);
        var verdict = result.Verdict;

        // (a) latest-verdict cache (drives the digest join) — upsert/overwrite.
        await repo.UpsertAsync(verdict, ct);

        // (b) append-only history — one immutable row per scan, retaining the FULL
        // result incl. the raw findings JSON. Appended for scanned/not_auditable/
        // error alike so the timeline is complete (same arg order the worker uses).
        await historyRepo.AppendAsync(
            verdict.AgentAddress, verdict.ScannedAt, verdict.Status,
            verdict.Score, verdict.Grade, verdict.ObservableCount,
            verdict.FindingCount, verdict.SeverityCountsJson,
            result.RawVerdict, verdict.CorpusVersion,
            result.RawFindingsJson, verdict.LastError, ct);

        return result;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityScanServiceTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Services/SecurityScanService.cs ACP_Metabot.Api.Tests/SecurityScanServiceTests.cs
git commit -m "feat(metabot): SecurityScanService — shared scan+upsert+append seam

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Refactor `SecurityScanWorker.TickOnceAsync` to call the service

**Files:**
- Modify: `ACP_Metabot.Api/Services/SecurityScanWorker.cs` (the `TickOnceAsync` loop body)
- Modify: `ACP_Metabot.Api.Tests/SecurityScanWorkerTests.cs` (register `SecurityScanService` in the fixture's `ServiceCollection`)

Behaviour is identical — the worker keeps its batch/delay/stale-selection logic and resolves the service from its per-tick scope, replacing the inlined scan+upsert+append with one `ScanAndPersistAsync` call per agent. The worker no longer calls `ITheSecurityBotClient` directly (the service does), so the now-unused `ITheSecurityBotClient client` ctor param + `_client` field are REMOVED — this keeps the worker clean and avoids an unused-field risk to Task 6's "0 warnings" gate. The 6 existing worker tests must still pass.

- [ ] **Step 1: Update the test fixture FIRST (so the worker scope can resolve the service)**

In `ACP_Metabot.Api.Tests/SecurityScanWorkerTests.cs`, the constructor's `ServiceCollection` block currently reads:

```csharp
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(_db);
        services.AddSingleton(_repo);
        services.AddSingleton(_histRepo); // worker scope resolves it for the per-scan append
        _sp = services.BuildServiceProvider();
```

Replace it with (add the `SecurityScanService` registration — the worker scope now resolves it; the service is constructed from whatever `ITheSecurityBotClient` the test passes to `MakeWorker`, so it must be registered with the SAME client instance per test — see the `MakeWorker` change below):

```csharp
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(_db);
        services.AddSingleton(_repo);
        services.AddSingleton(_histRepo); // worker scope resolves it for the per-scan append
        // The worker resolves SecurityScanService from its per-tick scope. It is
        // constructed per-test from the FakeClient passed to MakeWorker (so the
        // service scans through the same fake the test inspects), then registered
        // as the scope-resolvable singleton.
        _services = services; // keep the collection so MakeWorker can register the per-test service
        _sp = services.BuildServiceProvider();
```

Add a field for the kept collection — change the fields block at the top of the class from:

```csharp
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;
    private readonly SecurityScanHistoryRepository _histRepo;
    private readonly ServiceProvider _sp;
```

to:

```csharp
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;
    private readonly SecurityScanHistoryRepository _histRepo;
    private readonly ServiceCollection _services;
    private ServiceProvider _sp;
```

Then change `MakeWorker` from:

```csharp
    private SecurityScanWorker MakeWorker(ITheSecurityBotClient client)
    {
        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var config = _sp.GetRequiredService<IConfiguration>();
        return new SecurityScanWorker(scopeFactory, client, config, NullLogger<SecurityScanWorker>.Instance);
    }
```

to (register the FakeClient + a `SecurityScanService` built from it, rebuild the provider, and construct the worker WITHOUT a client arg — the worker no longer takes one):

```csharp
    private SecurityScanWorker MakeWorker(ITheSecurityBotClient client)
    {
        // The worker no longer depends on ITheSecurityBotClient — its scope-resolved
        // SecurityScanService does. Register THIS test's FakeClient + the service so
        // the worker's per-tick scope resolves a service that scans through the same
        // fake the test inspects, then rebuild the provider.
        _sp.Dispose();
        _services.AddSingleton<ITheSecurityBotClient>(client);
        _services.AddSingleton<SecurityScanService>();
        _sp = _services.BuildServiceProvider();

        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var config = _sp.GetRequiredService<IConfiguration>();
        return new SecurityScanWorker(scopeFactory, config, NullLogger<SecurityScanWorker>.Instance);
    }
```

> **Fixture note:** xUnit constructs a FRESH test-class instance per `[Fact]` (the constructor runs once per test, so each test gets its own temp Db + `ServiceCollection` + provider), and each of the 6 tests calls `MakeWorker` exactly once. That is precisely what makes the single mutate-and-rebuild of the `_services` collection / `_sp` provider inside `MakeWorker` safe — there is no second `MakeWorker` call in any test to double-register `ITheSecurityBotClient`/`SecurityScanService`, and no cross-test state because the instance (and its collection) is discarded after each test. The FakeClient stays registered in the test `ServiceCollection` so the scope-resolved `SecurityScanService` still gets it; the worker itself is constructed with no client arg.

- [ ] **Step 2: Drop the now-unused client from the worker ctor + fields**

In `ACP_Metabot.Api/Services/SecurityScanWorker.cs`, the fields block currently reads (lines 22-24):

```csharp
    private readonly IServiceScopeFactory _scopes;
    private readonly ITheSecurityBotClient _client;
    private readonly ILogger<SecurityScanWorker> _log;
```

Remove the `_client` field so it becomes:

```csharp
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SecurityScanWorker> _log;
```

The constructor currently reads (lines 35-50):

```csharp
    public SecurityScanWorker(IServiceScopeFactory scopes, ITheSecurityBotClient client,
        IConfiguration config, ILogger<SecurityScanWorker> log)
    {
        _scopes = scopes;
        _client = client;
        _log = log;

        _enabled = config.GetValue<bool?>("SECURITY_SCAN_ENABLED") ?? false;
        _batch   = Math.Clamp(config.GetValue<int?>("SECURITY_SCAN_BATCH") ?? 10, 1, 100);
        _delay   = TimeSpan.FromSeconds(Math.Max(0, config.GetValue<int?>("SECURITY_SCAN_DELAY_SECONDS") ?? 5));
        _tick    = TimeSpan.FromSeconds(Math.Max(15, config.GetValue<int?>("SECURITY_SCAN_TICK_SECONDS") ?? 60));
        _activeWindowDays = Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_ACTIVE_WINDOW_DAYS") ?? 30);
        _scannedTtl      = TimeSpan.FromDays(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_TTL_DAYS") ?? 7));
        _notAuditableTtl = TimeSpan.FromDays(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_NOTAUDITABLE_TTL_DAYS") ?? 30));
        _errorTtl        = TimeSpan.FromHours(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_ERROR_TTL_HOURS") ?? 6));
    }
```

Drop the `ITheSecurityBotClient client` parameter and its assignment so it becomes (everything else unchanged):

```csharp
    public SecurityScanWorker(IServiceScopeFactory scopes,
        IConfiguration config, ILogger<SecurityScanWorker> log)
    {
        _scopes = scopes;
        _log = log;

        _enabled = config.GetValue<bool?>("SECURITY_SCAN_ENABLED") ?? false;
        _batch   = Math.Clamp(config.GetValue<int?>("SECURITY_SCAN_BATCH") ?? 10, 1, 100);
        _delay   = TimeSpan.FromSeconds(Math.Max(0, config.GetValue<int?>("SECURITY_SCAN_DELAY_SECONDS") ?? 5));
        _tick    = TimeSpan.FromSeconds(Math.Max(15, config.GetValue<int?>("SECURITY_SCAN_TICK_SECONDS") ?? 60));
        _activeWindowDays = Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_ACTIVE_WINDOW_DAYS") ?? 30);
        _scannedTtl      = TimeSpan.FromDays(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_TTL_DAYS") ?? 7));
        _notAuditableTtl = TimeSpan.FromDays(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_NOTAUDITABLE_TTL_DAYS") ?? 30));
        _errorTtl        = TimeSpan.FromHours(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_ERROR_TTL_HOURS") ?? 6));
    }
```

> Program.cs needs NO change for this: `AddHostedService<SecurityScanWorker>()` resolves the remaining ctor params (`IServiceScopeFactory`, `IConfiguration`, `ILogger<SecurityScanWorker>`) from DI as before — it simply no longer needs to supply `ITheSecurityBotClient`. The client is still registered (the scope-resolved `SecurityScanService` depends on it).

- [ ] **Step 3: Refactor the worker loop**

In `ACP_Metabot.Api/Services/SecurityScanWorker.cs`, the `TickOnceAsync` method currently reads (lines 72-108):

```csharp
    public async Task<int> TickOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<SecurityVerdictRepository>();
        var historyRepo = scope.ServiceProvider.GetRequiredService<SecurityScanHistoryRepository>();

        var stale = await repo.GetStaleAgentsAsync(
            DateTime.UtcNow, _activeWindowDays,
            _scannedTtl, _notAuditableTtl, _errorTtl, _batch, ct);
        if (stale.Count == 0) return 0;

        _log.LogInformation("[security-scan] {Count} stale agents this tick", stale.Count);
        int scanned = 0;
        for (int i = 0; i < stale.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _client.ScanAsync(stale[i], ct);
            var verdict = result.Verdict;
            // (a) latest-verdict cache (drives the digest join) — upsert/overwrite.
            await repo.UpsertAsync(verdict, ct);
            // (b) append-only history — one immutable row per scan, retaining the
            // FULL result incl. the raw findings JSON ("save the results of each
            // scan on a bot"). Appended for scanned/not_auditable/error alike so
            // the timeline is complete. Cache first (keeps the digest correct),
            // then append; a crash strictly between the two re-captures next scan.
            await historyRepo.AppendAsync(
                verdict.AgentAddress, verdict.ScannedAt, verdict.Status,
                verdict.Score, verdict.Grade, verdict.ObservableCount,
                verdict.FindingCount, verdict.SeverityCountsJson,
                result.RawVerdict, verdict.CorpusVersion,
                result.RawFindingsJson, verdict.LastError, ct);
            scanned++;
            if (_delay > TimeSpan.Zero && i < stale.Count - 1)
                await Task.Delay(_delay, ct);
        }
        return scanned;
    }
```

Replace the WHOLE method body with (resolve `SecurityScanService` from the scope; delegate the per-agent scan+persist to it; keep the batch/delay/stale-selection logic unchanged):

```csharp
    public async Task<int> TickOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<SecurityVerdictRepository>();
        var historyRepo = scope.ServiceProvider.GetRequiredService<SecurityScanHistoryRepository>();
        var scanService = scope.ServiceProvider.GetRequiredService<SecurityScanService>();

        var stale = await repo.GetStaleAgentsAsync(
            DateTime.UtcNow, _activeWindowDays,
            _scannedTtl, _notAuditableTtl, _errorTtl, _batch, ct);
        if (stale.Count == 0) return 0;

        _log.LogInformation("[security-scan] {Count} stale agents this tick", stale.Count);
        int scanned = 0;
        for (int i = 0; i < stale.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Single write-path: scan -> upsert latest-verdict cache -> append a
            // full history row (cache first, then append). Identical to the
            // on-demand POST /admin/securityScan path — see SecurityScanService.
            await scanService.ScanAndPersistAsync(stale[i], repo, historyRepo, ct);
            scanned++;
            if (_delay > TimeSpan.Zero && i < stale.Count - 1)
                await Task.Delay(_delay, ct);
        }
        return scanned;
    }
```

> The worker's `ExecuteAsync` + all the timing/batch/stale-selection fields are UNCHANGED. The only worker-class changes are: the dropped `ITheSecurityBotClient client` ctor param + `_client` field (Step 2), and the `TickOnceAsync` body now resolving `SecurityScanService` from the scope and delegating to it instead of calling the client directly. In production the scope-resolved `SecurityScanService` wraps the same singleton client the worker used to hold, so behaviour is identical — one write-path.

- [ ] **Step 4: Run the worker tests to verify they still pass**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityScanWorkerTests"`
Expected: PASS (6 tests — `Tick_ScansStaleAgents_UpsertsVerdicts`, `Tick_SkipsFreshlyScannedAgents`, `Tick_RespectsBatchLimit`, `Tick_PersistsErrorVerdict_WhenClientReportsError`, `Tick_AppendsHistoryRow_PerScan`, `Tick_RescanSameAgent_TwoHistoryRows_OneCacheRow`). No behaviour change.

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Services/SecurityScanWorker.cs ACP_Metabot.Api.Tests/SecurityScanWorkerTests.cs
git commit -m "refactor(metabot): SecurityScanWorker delegates per-agent scan to SecurityScanService

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Register `SecurityScanService` in `Program.cs`

**Files:**
- Modify: `ACP_Metabot.Api/Program.cs` (the security-repo registration block, lines 37-38)

No new test — covered by the worker tests (which exercise the resolution path) + the build gate. This registration is REQUIRED before Task 2's production path works (`scope.ServiceProvider.GetRequiredService<SecurityScanService>()`).

- [ ] **Step 1: Add the singleton registration**

In `ACP_Metabot.Api/Program.cs`, the security-repo block currently reads (lines 37-38):

```csharp
builder.Services.AddSingleton<SecurityVerdictRepository>();
builder.Services.AddSingleton<SecurityScanHistoryRepository>(); // worker scope resolves it; also the seam for the deferred read endpoint
```

Replace it with (append the service registration immediately after the history repo):

```csharp
builder.Services.AddSingleton<SecurityVerdictRepository>();
builder.Services.AddSingleton<SecurityScanHistoryRepository>(); // worker scope resolves it; also the seam for the deferred read endpoint
// Shared scan-and-persist seam. Depends on ITheSecurityBotClient (registered
// below at the thesecuritybot HttpClient block). Resolved by SecurityScanWorker's
// per-tick scope AND the on-demand POST /admin/securityScan endpoint — one write-path.
builder.Services.AddSingleton<SecurityScanService>();
```

> `ITheSecurityBotClient` is registered later in the file (the `thesecuritybot` HttpClient block, lines 95-102). Singleton ordering does not matter for DI resolution — `SecurityScanService` is only instantiated on first resolution (worker tick / endpoint request), by which point all registrations are in place.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build ACP_Metabot.Api`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add ACP_Metabot.Api/Program.cs
git commit -m "feat(metabot): register SecurityScanService singleton

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `POST /admin/securityScan` operator endpoint

**Files:**
- Create: `ACP_Metabot.Api/Endpoints/SecurityScanEndpoint.cs`
- Test: `ACP_Metabot.Api.Tests/SecurityScanEndpointTests.cs`
- Modify: `ACP_Metabot.Api/Program.cs` (register the thin `MapPost` wrapper near `/admin/pulse/tick-now`)

**Endpoint-test harness note:** the test project has **no** WebApplicationFactory / `Microsoft.AspNetCore.Mvc.Testing` reference (confirmed: `ACP_Metabot.Api.Tests.csproj` carries only xunit + Test.Sdk + coverlet; `RiskAttestProEndpointTests` documents this explicitly). The codebase's established pattern is to factor the endpoint logic into a static `HandleAsync` helper in `ACP_Metabot.Api/Endpoints/`, register a thin `MapPost` wrapper in `Program.cs`, and unit-test `HandleAsync` directly — round-tripping the returned `IResult` through a `DefaultHttpContext` to read status + JSON. **`HandleAsync` MUST return a custom `RawJsonResult : IResult`** (NOT `Results.Ok` / `Results.BadRequest` / `Results.Json`) — copied verbatim from `RiskAttestProEndpoint`. This is required, not stylistic: `Results.Ok(...)` and `Results.BadRequest(...)` call `GetRequiredService` on `ctx.RequestServices`, which throws `ArgumentNullException (Parameter 'provider')` when executed against a bare `new DefaultHttpContext()` (null `RequestServices`) — empirically confirmed, so the round-trip test only works because `RawJsonResult` serializes the body itself and writes bytes straight to `ctx.Response.Body` without touching `RequestServices`. This task follows that exact pattern. **401-without-key is enforced by Metabot's inline X-API-Key middleware** (`/admin/*` is NOT in the bypass list — only `/health`, `/llms.txt`, `/.well-known/*`, and `/v1/*` except `/v1/internal/*` + `/v1/agents/active` bypass; precedent: the operator-only `/admin/pulse/tick-now`). The handler is therefore never reached unauthenticated; the 401 path is a middleware concern documented here, not a handler unit test.

- [ ] **Step 1: Write the failing tests**

Create `ACP_Metabot.Api.Tests/SecurityScanEndpointTests.cs` (mirrors `RiskAttestProEndpointTests`: real `Db` temp file, a counting service shim passed as a delegate, and the `ExecuteAsync(IResult)` round-trip helper):

```csharp
using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Endpoints;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// POST /admin/securityScan endpoint behaviour. The endpoint is registered in
/// Program.cs as a thin wrapper around the static SecurityScanEndpoint.HandleAsync;
/// tests exercise the helper directly (no WebApplicationFactory in this project,
/// see RiskAttestProEndpointTests). 401-without-key is enforced by the X-API-Key
/// middleware (/admin/* is gated) and is not reachable at this layer.
/// </summary>
public class SecurityScanEndpointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;
    private readonly SecurityScanHistoryRepository _histRepo;

    public SecurityScanEndpointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_endpt_secscan_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new SecurityVerdictRepository(_db);
        _histRepo = new SecurityScanHistoryRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    // A scan delegate matching SecurityScanService.ScanAndPersistAsync's shape.
    // The shim writes through the real repos (so we can assert both tables) and
    // returns a synthetic ScanResult.
    private Func<string, SecurityVerdictRepository, SecurityScanHistoryRepository,
        CancellationToken, Task<ScanResult>> ScanDelegate(
            Func<string, ScanResult> produce)
        => async (addr, repo, hist, ct) =>
        {
            var result = produce(addr);
            var v = result.Verdict;
            await repo.UpsertAsync(v, ct);
            await hist.AppendAsync(v.AgentAddress, v.ScannedAt, v.Status, v.Score, v.Grade,
                v.ObservableCount, v.FindingCount, v.SeverityCountsJson, result.RawVerdict,
                v.CorpusVersion, result.RawFindingsJson, v.LastError, ct);
            return result;
        };

    private static ScanResult ScannedResult(string addr) => new(
        new SecurityVerdict(addr, SecurityStatus.Scanned, 88, "B", 11, 1, "{\"High\":1}",
            "2026-06-09T10:00:00.0000000Z", null, null),
        RawFindingsJson: "[{\"patternId\":\"P9\",\"title\":\"RPC url leak\",\"severity\":\"High\",\"verdict\":\"Present\",\"evidence\":\"alchemy key in log\",\"fixRef\":\"P9\"}]",
        RawVerdict: "PASS");

    private static async Task<(int StatusCode, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var bodyStream = new MemoryStream();
        ctx.Response.Body = bodyStream;
        await result.ExecuteAsync(ctx);
        bodyStream.Position = 0;
        if (bodyStream.Length == 0)
            return (ctx.Response.StatusCode == 0 ? 200 : ctx.Response.StatusCode,
                JsonDocument.Parse("{}").RootElement.Clone());
        var doc = await JsonDocument.ParseAsync(bodyStream);
        return (ctx.Response.StatusCode == 0 ? 200 : ctx.Response.StatusCode,
            doc.RootElement.Clone());
    }

    [Fact]
    public async Task BadAddress_returns_400()
    {
        var req = new AdminSecurityScanRequest("not-an-address");
        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(ScannedResult), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(400, status);
        Assert.Equal("invalid_address", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingAddress_returns_400()
    {
        var req = new AdminSecurityScanRequest(null);
        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(ScannedResult), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(400, status);
        Assert.Equal("invalid_address", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Scanned_returns_200_with_verdict_findings_and_writes_both_tables()
    {
        var addr = "0xABC0000000000000000000000000000000000ABC";
        var req = new AdminSecurityScanRequest(addr);

        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(ScannedResult), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status);
        Assert.Equal("0xabc0000000000000000000000000000000000abc", body.GetProperty("agentAddress").GetString());
        Assert.Equal(SecurityStatus.Scanned, body.GetProperty("status").GetString());
        Assert.Equal(88, body.GetProperty("score").GetInt32());
        Assert.Equal("B", body.GetProperty("grade").GetString());
        Assert.Equal(11, body.GetProperty("observableCount").GetInt32());
        Assert.Equal(1, body.GetProperty("findingCount").GetInt32());
        Assert.Equal("PASS", body.GetProperty("verdict").GetString());
        Assert.Equal("2026-06-09T10:00:00.0000000Z", body.GetProperty("scannedAt").GetString());

        // severityCounts is an OBJECT parsed from severity_counts JSON.
        var sev = body.GetProperty("severityCounts");
        Assert.Equal(JsonValueKind.Object, sev.ValueKind);
        Assert.Equal(1, sev.GetProperty("High").GetInt32());

        // findings is the parsed array from RawFindingsJson, full per-finding detail.
        var findings = body.GetProperty("findings");
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
        Assert.Equal(1, findings.GetArrayLength());
        var f0 = findings[0];
        Assert.Equal("P9", f0.GetProperty("patternId").GetString());
        Assert.Equal("alchemy key in log", f0.GetProperty("evidence").GetString());
        Assert.Equal("P9", f0.GetProperty("fixRef").GetString());

        // Both tables written (lower-cased).
        var cached = await _repo.GetByAgentAsync(addr);
        Assert.NotNull(cached);
        Assert.Equal(88, cached!.Score);
        var hist = await _histRepo.ListByAgentAsync(addr);
        Assert.Single(hist);

        // lastError must NEVER appear in the response body (P30/P63).
        var json = body.GetRawText();
        Assert.DoesNotContain("lastError", json);
    }

    [Fact]
    public async Task NullFindingsJson_returns_empty_findings_array()
    {
        var addr = "0xDEF0000000000000000000000000000000000DEF";
        var req = new AdminSecurityScanRequest(addr);
        ScanResult Produce(string a) => new(
            new SecurityVerdict(a, SecurityStatus.Scanned, 90, "A", 11, 0, "{}",
                "2026-06-09T11:00:00.0000000Z", null, null),
            RawFindingsJson: null, RawVerdict: "PASS");

        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(Produce), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status);
        var findings = body.GetProperty("findings");
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
        Assert.Equal(0, findings.GetArrayLength()); // null RawFindingsJson -> []
    }

    [Fact]
    public async Task NotAuditable_returns_200_with_status_and_empty_findings_no_lastError()
    {
        var addr = "0x1230000000000000000000000000000000000123";
        var req = new AdminSecurityScanRequest(addr);
        ScanResult Produce(string a) => new(
            new SecurityVerdict(a, SecurityStatus.NotAuditable, null, null, null, null, null,
                "2026-06-09T12:00:00.0000000Z", null, null),
            RawFindingsJson: null, RawVerdict: "NOT_AUDITABLE");

        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(Produce), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status); // honest outcome, not a 500
        Assert.Equal(SecurityStatus.NotAuditable, body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("findings").GetArrayLength());
        Assert.DoesNotContain("lastError", body.GetRawText());
    }

    [Fact]
    public async Task Error_returns_200_with_status_and_no_lastError_leak()
    {
        var addr = "0x4560000000000000000000000000000000000456";
        var req = new AdminSecurityScanRequest(addr);
        ScanResult Produce(string a) => new(
            new SecurityVerdict(a, SecurityStatus.Error, null, null, null, null, null,
                "2026-06-09T13:00:00.0000000Z", null, "HTTP 500 from securitybot"),
            RawFindingsJson: null, RawVerdict: null);

        var result = await SecurityScanEndpoint.HandleAsync(
            req, _repo, _histRepo, ScanDelegate(Produce), CancellationToken.None);

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(200, status); // never a 500
        Assert.Equal(SecurityStatus.Error, body.GetProperty("status").GetString());
        // last_error is stored in the history table (server-side) but NEVER in the response.
        var json = body.GetRawText();
        Assert.DoesNotContain("lastError", json);
        Assert.DoesNotContain("HTTP 500 from securitybot", json);
        var hist = await _histRepo.ListByAgentAsync(addr);
        Assert.Equal("HTTP 500 from securitybot", hist[0].LastError); // server-side only
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityScanEndpointTests"`
Expected: FAIL to compile — `SecurityScanEndpoint` / `AdminSecurityScanRequest` do not exist.

- [ ] **Step 3: Write the endpoint helper**

Create `ACP_Metabot.Api/Endpoints/SecurityScanEndpoint.cs`:

```csharp
// On-demand operator security scan — POST /admin/securityScan helper.
//
// Factored out of Program.cs so the validate -> scan-and-persist -> project
// behaviour can be unit-tested without standing up a WebApplicationFactory
// (none exists in this test project — see RiskAttestProEndpoint). Program.cs
// registers a thin `app.MapPost("/admin/securityScan", ...)` that resolves
// SecurityScanService + both repos and delegates straight to HandleAsync.
//
// GATING: /admin/* is NOT in the X-API-Key middleware bypass list, so this
// endpoint requires X-API-Key == INTERNAL_API_KEY (operator-only) — the same
// gate as /admin/pulse/tick-now. The handler is never reached unauthenticated.
//
// The scan-and-persist step is injected as a delegate so tests can write through
// real repos and return a synthetic ScanResult without a live SecurityBot.
//
// IResult shape: HandleAsync returns a CUSTOM `RawJsonResult : IResult` that
// serializes the body itself and writes the bytes straight to ctx.Response.Body
// — copied verbatim from RiskAttestProEndpoint. This is REQUIRED, not cosmetic:
// Results.Ok(...) / Results.BadRequest(...) call GetRequiredService on
// ctx.RequestServices, which throws ArgumentNullException (Parameter 'provider')
// when executed against a bare `new DefaultHttpContext()` (null RequestServices).
// The custom IResult never touches RequestServices, so the unit tests can
// round-trip it through ExecuteAsync(IResult) + DefaultHttpContext.

using System.Text.Json;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.AspNetCore.Http;

namespace ACP_Metabot.Api.Endpoints;

/// <summary>Request body for <c>POST /admin/securityScan</c>: the agent to scan.</summary>
public sealed record AdminSecurityScanRequest(string? AgentAddress);

/// <summary>
/// Static handler over <see cref="SecurityScanService"/>. The scan-and-persist
/// step is passed as a delegate (mirrors RiskAttestProEndpoint) so tests can
/// inject a counting/writing shim without the full cross-bot client.
/// </summary>
public static class SecurityScanEndpoint
{
    /// <summary>
    /// Validate the agent address (lower-case first, then <c>^0x[0-9a-fA-F]{40}$</c>;
    /// 400 otherwise), scan + persist through <paramref name="scanAndPersist"/>, and
    /// return the full operator projection: agentAddress, status, score, grade,
    /// observableCount, findingCount, severityCounts (object), verdict, scannedAt,
    /// findings[] (parsed from the persisted RawFindingsJson; null/blank -> []).
    /// <c>not_auditable</c>/<c>error</c> return 200 with that status (never a 500).
    /// <c>lastError</c> is NEVER surfaced (P30/P63).
    ///
    /// Returns a custom <see cref="RawJsonResult"/> (NOT Results.Ok/BadRequest) so
    /// the handler is unit-testable against a bare DefaultHttpContext — see the
    /// header note + RiskAttestProEndpoint.
    /// </summary>
    public static async Task<IResult> HandleAsync(
        AdminSecurityScanRequest req,
        SecurityVerdictRepository repo,
        SecurityScanHistoryRepository historyRepo,
        Func<string, SecurityVerdictRepository, SecurityScanHistoryRepository,
            CancellationToken, Task<ScanResult>> scanAndPersist,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.AgentAddress))
            return JsonError(400, "invalid_address");

        var addr = req.AgentAddress.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-fA-F]{40}$"))
            return JsonError(400, "invalid_address");

        // Always scans fresh (explicit operator override). Persists to the same
        // tables the worker writes via the shared SecurityScanService.
        var result = await scanAndPersist(addr, repo, historyRepo, ct);
        var v = result.Verdict;

        // severity_counts JSON -> object (null/blank -> empty object).
        JsonElement severityCounts = ParseObjectOrEmpty(v.SeverityCountsJson);
        // raw findings[] JSON -> array (null/blank/non-array -> empty array).
        JsonElement findings = ParseArrayOrEmpty(result.RawFindingsJson);

        // NOTE: v.LastError is deliberately OMITTED from the projection (P30/P63);
        // it is persisted server-side in security_scan_history only.
        //
        // The JsonElement values (severityCounts, findings) serialize INLINE as
        // real JSON via JsonSerializer — an object/array, not a quoted string.
        var json = JsonSerializer.Serialize(new
        {
            agentAddress    = v.AgentAddress,
            status          = v.Status,
            score           = v.Score,
            grade           = v.Grade,
            observableCount = v.ObservableCount,
            findingCount    = v.FindingCount,
            severityCounts,
            verdict         = result.RawVerdict,
            scannedAt       = v.ScannedAt,
            findings,
        }, JsonOpts);
        return new RawJsonResult(200, json);
    }

    /// <summary>
    /// Minimal <see cref="IResult"/> that writes a pre-serialized JSON body to the
    /// response without touching the DI container. Copied verbatim from
    /// RiskAttestProEndpoint: Results.Ok / Results.BadRequest / Results.Json all
    /// call GetRequiredService on ctx.RequestServices (null in a bare
    /// DefaultHttpContext), which makes the unit tests impossible. This shim writes
    /// the body byte-for-byte and lets the test just read ctx.Response.Body.
    /// </summary>
    private sealed class RawJsonResult : IResult
    {
        private readonly int _statusCode;
        private readonly string _json;
        public RawJsonResult(int statusCode, string json)
        {
            _statusCode = statusCode;
            _json = json;
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
    /// envelopes. Used for the 400 invalid_address reject. Body is camelCase
    /// <c>{"error":"..."}</c>.
    /// </summary>
    private static IResult JsonError(int status, string error)
        => new RawJsonResult(status,
            JsonSerializer.Serialize(new { error }, JsonOpts));

    private static JsonElement ParseObjectOrEmpty(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return doc.RootElement.Clone();
            }
            catch (JsonException) { /* fall through to empty */ }
        }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static JsonElement ParseArrayOrEmpty(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return doc.RootElement.Clone();
            }
            catch (JsonException) { /* fall through to empty */ }
        }
        using var empty = JsonDocument.Parse("[]");
        return empty.RootElement.Clone();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityScanEndpointTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Register the thin route wrapper in `Program.cs`**

In `ACP_Metabot.Api/Program.cs`, the operator route block currently reads (lines 1277-1282):

```csharp
app.MapPost("/admin/pulse/tick-now",
    async (MarketplacePulseWorker worker, CancellationToken ct) =>
{
    var delivered = await worker.TickOnceAsync(ct);
    return Results.Ok(new { ok = true, delivered });
});
```

Insert the new operator route immediately AFTER that block (so both operator routes sit together; `/admin/*` is X-API-Key-gated by the existing middleware — no `.RequireRateLimiting` per the `/admin/pulse/tick-now` precedent):

```csharp
app.MapPost("/admin/pulse/tick-now",
    async (MarketplacePulseWorker worker, CancellationToken ct) =>
{
    var delivered = await worker.TickOnceAsync(ct);
    return Results.Ok(new { ok = true, delivered });
});

// Operator-only on-demand SecurityBot scan of any marketplace bot. Gated by the
// inline X-API-Key middleware (/admin/* is NOT in the bypass list) — same gate as
// /admin/pulse/tick-now. Body: { agentAddress }. Scans fresh, persists to the same
// security_verdicts cache + security_scan_history log the worker writes (via the
// shared SecurityScanService), and returns the full verdict + per-finding detail.
// Free internal path (acp-shared, no escrow). lastError is never surfaced.
// operator-only behind X-API-Key; no rate-limit, matching /admin/pulse/tick-now.
app.MapPost("/admin/securityScan",
    async (ACP_Metabot.Api.Endpoints.AdminSecurityScanRequest req,
           SecurityScanService svc,
           SecurityVerdictRepository repo,
           SecurityScanHistoryRepository historyRepo,
           CancellationToken ct) =>
    {
        return await ACP_Metabot.Api.Endpoints.SecurityScanEndpoint.HandleAsync(
            req, repo, historyRepo, svc.ScanAndPersistAsync, ct);
    });
```

> `svc.ScanAndPersistAsync` matches the handler's `Func<string, SecurityVerdictRepository, SecurityScanHistoryRepository, CancellationToken, Task<ScanResult>>` delegate exactly (the service method has signature `ScanAndPersistAsync(string, SecurityVerdictRepository, SecurityScanHistoryRepository, CancellationToken)`). `SecurityScanService`, `SecurityVerdictRepository`, and `SecurityScanHistoryRepository` are all DI singletons (Tasks 3 + the prior security feature), so they bind by type.

- [ ] **Step 6: Build the whole API to confirm endpoint wiring compiles**

Run: `dotnet build ACP_Metabot.Api`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add ACP_Metabot.Api/Endpoints/SecurityScanEndpoint.cs ACP_Metabot.Api.Tests/SecurityScanEndpointTests.cs ACP_Metabot.Api/Program.cs
git commit -m "feat(metabot): POST /admin/securityScan operator endpoint (verdict + findings)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: `acp_security_scan` plugin tool

**Files:**
- Modify: `acp-find-plugin/mcp-server/server.js` (TOOLS entry + HANDLERS function)

This task runs from the `acp-find-plugin` repo root (NOT the Metabot repo). The plugin has no automated test runner (the `smoke-*.mjs` files are manual live-smoke scripts), so verification is a `node --check` syntax pass + a structural grep. Local commit only; an `npm version` + `npm publish` from a real PowerShell terminal is required to reach users (per the acp-find release rule) — deferred to Task 6.

Anchors confirmed in `server.js` (v0.13.0): `agentUrl` (line 185), `isHexAddress` (line 229), `normalizeAddress` (line 233), `callGateway` (line 505), `const TOOLS = [` (line 563), `const HANDLERS = {` (line 1490), representative POST tool `acp_risk_snapshot` (TOOLS def line 1122, HANDLERS impl line 2140). `ACP_API_KEY` → `SEND_API_KEY` → `callGateway` attaches `X-API-Key` when set (line 507).

- [ ] **Step 1: Add the TOOLS entry**

In `acp-find-plugin/mcp-server/server.js`, find the `acp_risk_snapshot` tool definition inside the `TOOLS` array (begins `name: "acp_risk_snapshot",` near line 1122). Insert the following NEW object immediately BEFORE the `acp_risk_snapshot` entry (i.e. right before its opening `{`), keeping the array comma-correct:

```javascript
  {
    name: "acp_security_scan",
    description:
      "OPERATOR-ONLY. Run TheSecurityBot's full passive security scan against any ACP marketplace bot ON DEMAND (jumps the background worker's queue for one agent). Returns the verdict + score/grade + per-finding detail {patternId, title, severity, verdict, evidence, fixRef} scored against the P1-P64 + B1-B9 catalogue, and persists the result to TheMetaBot's security history. REQUIRES the operator key: set ACP_API_KEY = TheMetaBot's INTERNAL_API_KEY (the gateway returns 401 without it). Free internal path ($0, no ACP escrow). Accepts ANY agent address whether or not it is indexed (SecurityBot resolves the target's public surface). Use to diagnose 'can SecurityBot score this bot?' or to get an actionable fix list for a bot you operate. not_auditable / error are returned honestly (status field), not as a failure.",
    inputSchema: {
      type: "object",
      properties: {
        agentAddress: {
          type: "string",
          description: "EVM agent wallet address (0x + 40 hex). Lower- or mixed-case OK."
        }
      },
      required: ["agentAddress"]
    }
  },
```

- [ ] **Step 2: Add the HANDLERS function**

In the `HANDLERS` object (begins `const HANDLERS = {` near line 1490), find the `acp_risk_snapshot` handler (begins `acp_risk_snapshot: async (args) => {` near line 2140). Insert the following NEW handler immediately BEFORE the `acp_risk_snapshot:` handler entry, keeping the object comma-correct:

```javascript
  acp_security_scan: async (args) => {
    if (!args?.agentAddress) throw new Error("agentAddress is required");
    if (!isHexAddress(args.agentAddress)) {
      throw new Error("agentAddress must be 0x followed by 40 hex chars");
    }
    // Operator-only: the gateway gates /admin/* behind X-API-Key. callGateway
    // attaches X-API-Key only when ACP_API_KEY is set (SEND_API_KEY). Give a clear
    // message up front rather than surfacing a bare 401 passthrough.
    if (!SEND_API_KEY) {
      throw new Error(
        "acp_security_scan is operator-only: set ACP_API_KEY to TheMetaBot's INTERNAL_API_KEY to authorise the scan."
      );
    }
    const agentAddress = normalizeAddress(args.agentAddress);
    const result = await callGateway("/admin/securityScan", { agentAddress }, "POST");
    // Decorate with the marketplace hire link, consistent with the other tools.
    if (result && typeof result === "object" && !result.marketplaceUrl) {
      result.marketplaceUrl = agentUrl(agentAddress);
    }
    return result;
  },
```

> Returned as-is (no `wrapUntrusted`): the response carries SecurityBot's structured findings + a verdict, not agent-authored marketplace text. This matches the risk/verification tools (`acp_risk_snapshot`, `acp_risk_deep_dive`) which call `callGateway` directly and return the result.

- [ ] **Step 3: Verify the file parses + the tool is wired**

Working dir for this step is the `acp-find-plugin` repo root (`C:\code_crypto\ACP\acp-find-plugin`); the path `mcp-server/server.js` below is relative to it. Run the syntax check + the structural grep:

```bash
node --check mcp-server/server.js
node -e "const s=require('fs').readFileSync('mcp-server/server.js','utf8'); const t=(s.match(/name: \"acp_security_scan\"/g)||[]).length; const h=(s.match(/acp_security_scan: async/g)||[]).length; console.log('tool-def='+t+' handler='+h);"
```

Expected: `node --check` prints nothing (exit 0 — file is syntactically valid); the second line prints `tool-def=1 handler=1` (both halves exist exactly once).

- [ ] **Step 4: Commit (LOCAL only — republish deferred)**

```bash
git add mcp-server/server.js
git commit -m "feat(acp-find): add acp_security_scan operator tool (POST /admin/securityScan)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

> This is LOCAL-only. The tool reaches users only after `npm version` + `npm publish` from a real Windows PowerShell terminal (the WebAuthn-only npm account blocks the Bash/`!` TOTP fallback) — see Task 6. A full release would also add a `## What's new` lead block to `mcp-server/README.md` + bump `plugin.json`/CHANGELOG in lockstep; that is a publish-time concern, not part of this code-only plan.

---

### Task 6: Full build + test verification + ops handoff

**Files:** none (verification only) — plus an OPTIONAL ops note.

- [ ] **Step 1: Build the API clean**

Run (working dir `C:\code_crypto\ACP\ACP_Metabot\ACP_Metabot`): `dotnet build ACP_Metabot.Api`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 2: Run the FULL test suite**

Run: `dotnet test ACP_Metabot.Api.Tests`
Expected: All tests pass — the pre-existing suite PLUS the new `SecurityScanServiceTests` (3), `SecurityScanEndpointTests` (6), and the unchanged `SecurityScanWorkerTests` (6, still green after the Task 2 refactor). No regressions in `SecurityVerdictRepositoryTests`, `SecurityScanHistoryRepositoryTests`, `TheSecurityBotClientTests`, `DigestServiceSecurityTests`.

- [ ] **Step 3: Boot smoke (worker default-OFF path unchanged)**

Run: `dotnet run --project ACP_Metabot.Api` (Ctrl-C after the boot banner).
Expected log line: `[security-scan] disabled — set SECURITY_SCAN_ENABLED=true to activate` (the worker still registers but stays dormant — the refactor did not change its default-OFF gating; confirms no crash-on-boot from the new service registration or route).

- [ ] **Step 4: Plugin syntax re-check**

Run (from `C:\code_crypto\ACP\acp-find-plugin`): `node --check mcp-server/server.js`
Expected: exit 0. The plugin has NO unit-test runner — the `smoke-*.mjs` scripts are manual live-smoke against the deployed gateway and are out of scope for this local-only change.

- [ ] **Step 5: Record the ops/deploy + republish handoff (OPTIONAL — only if the ACP workspace tracks a followups doc)**

These steps require Oliver's machine / the droplet and are NOT done by this plan. If `security-audit/_followups_2026-06-08.md` exists (the prior feature's handoff), append a new section; otherwise skip this step (no new file required):

```markdown
## H. On-demand acp_security_scan — deploy + plugin republish
Code committed local-only (no push). To ship:
1. Push the Metabot commits, then on the droplet `cd /root/ACP_Metabot && git pull --ff-only origin main`.
2. NO new Metabot env: /admin/securityScan reuses the existing INTERNAL_API_KEY (gating)
   and the already-wired THESECURITYBOT_API_KEY (the free cross-bot scan call).
3. `docker compose up -d --build acp-metabot-api`. The /admin/securityScan route is
   reached via the existing Caddy apex catch-all -> acp-metabot-api; no new Caddy block.
4. Operator smoke: `curl -s -X POST https://api.acp-metabot.dev/admin/securityScan \
   -H "X-API-Key: $INTERNAL_API_KEY" -H "Content-Type: application/json" \
   -d '{"agentAddress":"0x..."}'` -> 200 with {status, score, grade, findings:[...]}.
   Without the header -> 401 (middleware). Bad address -> 400.
5. Plugin: set ACP_API_KEY = Metabot's INTERNAL_API_KEY in the operator's MCP client
   config, then republish acp-find-mcp from a REAL PowerShell terminal
   (`cd ...\acp-find-plugin\mcp-server; npm version <bump>; npm publish`) with the
   README What's-new lead block + plugin.json/CHANGELOG bumped in lockstep.
```

- [ ] **Step 6: Commit the ops note (only if Step 5 wrote a file)**

```bash
git add security-audit/_followups_2026-06-08.md
git commit -m "docs: ops handoff for on-demand acp_security_scan deploy

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

(From the ACP workspace repo that tracks `security-audit/`. Check `git status` first to confirm the right repo root. Skip entirely if the file does not exist.)

---

## Self-Review

**Spec coverage** (against `2026-06-09-metabot-ondemand-security-scan-design.md`):
- **§Components 1 — `SecurityScanService` (refactor + reuse)** → Task 1 (service + tests) + Task 2 (worker delegates to it) + Task 3 (DI singleton). ✅ `ScanAndPersistAsync(string, SecurityVerdictRepository, SecurityScanHistoryRepository, CancellationToken) -> Task<ScanResult>`: calls `client.ScanAsync` → `repo.UpsertAsync(result.Verdict)` (cache first) → `historyRepo.AppendAsync(...)` with the EXACT 12-arg order the worker uses (AgentAddress, ScannedAt, Status, Score, Grade, ObservableCount, FindingCount, SeverityCountsJson, RawVerdict, CorpusVersion, RawFindingsJson, LastError) → returns the `ScanResult`. Depends on `ITheSecurityBotClient` only; repos passed in by the caller. ✅ Test: upsert + one history row + returns result; re-run → 2 history rows, one cache row.
- **§Components 1 — worker behaviour identical** → Task 2. ✅ The worker keeps its `ExecuteAsync` + all timing/batch/delay/stale-selection fields; the inlined three calls become one `ScanAndPersistAsync` call. The now-unused `ITheSecurityBotClient client` ctor param + `_client` field are REMOVED (the scope-resolved `SecurityScanService` is the sole client caller now) — this avoids an unused-field warning against Task 6's "0 warnings" gate. Program.cs needs no change: `AddHostedService<SecurityScanWorker>()` still resolves the remaining ctor params (`IServiceScopeFactory`, `IConfiguration`, `ILogger`) by type. All 6 existing `SecurityScanWorkerTests` stay green; the fixture gains a `SecurityScanService` registration and `MakeWorker` constructs the worker with no client arg (the FakeClient is registered in the test `ServiceCollection` so the scope-resolved service still gets it). xUnit builds a fresh test-class instance per `[Fact]` and each test calls `MakeWorker` once, so the single mutate-and-rebuild of the collection/provider is safe. Old→new diff shown verbatim for the worker ctor+fields, the worker loop, and the test fixture.
- **§Components 2 — `POST /admin/securityScan`** → Task 4. ✅ X-API-Key gated by the existing inline middleware (`/admin/*` not in the bypass list — verified against the middleware source; precedent `/admin/pulse/tick-now`). Body `{agentAddress}`; validate `^0x[0-9a-fA-F]{40}$` (lower-case first) → 400 via a camelCase `{"error":"invalid_address"}` body. Resolves `SecurityScanService` + both repos; calls `ScanAndPersistAsync`. The handler serializes the success projection itself with `JsonSerializer.Serialize(obj, JsonOpts)` (camelCase) and returns 200 with `{ agentAddress, status, score, grade, observableCount, findingCount, severityCounts (object), verdict, scannedAt, findings:[...] }`; `findings` is the parsed `JsonElement` from the persisted `RawFindingsJson` (null/blank/parse-fail → `[]`) and `severityCounts` the parsed `JsonElement` from `SeverityCountsJson` (null/blank/parse-fail → `{}`), each embedded INLINE as real JSON. `lastError` NEVER surfaced. `not_auditable`/`error` → 200 with that status (never 500). ✅ Tests assert 400 (bad + missing), 200 verdict+findings + both tables written + camelCase keys present + no `lastError` leak, null-findings → `[]`, `not_auditable` → 200, `error` → 200 + `lastError` only server-side.
- **§Components 2 — endpoint test harness** → Task 4. ✅ The project has NO WebApplicationFactory (confirmed: csproj refs + `RiskAttestProEndpointTests` header). Plan mirrors the established static-`HandleAsync`-helper pattern and tests via the `ExecuteAsync(IResult)` `DefaultHttpContext` round-trip. Critically, `HandleAsync` returns a CUSTOM `RawJsonResult : IResult` (copied verbatim from `RiskAttestProEndpoint`) — NOT `Results.Ok`/`Results.BadRequest`/`Results.Json` — because those call `GetRequiredService` on `ctx.RequestServices`, which throws `ArgumentNullException (Parameter 'provider')` against a bare `DefaultHttpContext` (empirically confirmed by all reviewers + a probe). The `RawJsonResult` serializes the body itself and writes bytes straight to `ctx.Response.Body`, so the round-trip test passes. 400s go through a `JsonError(int,string)` helper that wraps `RawJsonResult` with a camelCase `{"error":"..."}` body, and `JsonOpts` is the camelCase `JsonSerializerOptions` lifted from `RiskAttestProEndpoint`. 401 is a middleware concern documented (handler unreachable unauthenticated). This is the explicit "if the project lacks an endpoint-test harness, test the handler logic at the unit level" branch.
- **§Components 2 — rate-limit conscious skip** → Task 4. ✅ The spec mentions a "modest rate-limit"; the plan CONSCIOUSLY OMITS it, matching the `/admin/pulse/tick-now` precedent (also operator-only behind X-API-Key with no rate-limit). Documented as a one-line route comment ("operator-only behind X-API-Key; no rate-limit, matching /admin/pulse/tick-now") and here. No `.RequireRateLimiting` / rate-limit policy is added; the X-API-Key gate is the access control for `/admin/*`.
- **§Components 3 — plugin `acp_security_scan`** → Task 5. ✅ TOOLS entry + HANDLERS function; `callGateway("/admin/securityScan", {agentAddress}, "POST")` sends X-API-Key via existing `ACP_API_KEY`/`SEND_API_KEY`; clear "operator key required" error when `SEND_API_KEY` is false; `agentAddress` required + `isHexAddress` guard + `normalizeAddress`; adds `marketplaceUrl`. Local commit only; republish noted.
- **§Key decisions** → operator-only/free-internal/same-persist-path/full-findings-returned all honoured. Always scans fresh (no cache-first short-circuit) — the endpoint calls `ScanAndPersistAsync` unconditionally.
- **§Error handling** → `ScanAsync` never throws (existing client); `error`/`not_auditable` persist + return 200; raw upstream body / `lastError` never leaked (asserted).
- **§Out of scope** honoured: no public/paid access, no `baseUrl` override, no batch on-demand, no history-reader endpoint/tool, no cache-first behaviour.

**Placeholder scan:** No TBD / TODO / "similar to Task N" / "add error handling". Every code step is complete and compilable. Repeated code (the `ExecuteAsync(IResult)` helper, the `AppendAsync` 12-arg call) is written out in full at each use site rather than cross-referenced. Exact `dotnet test --filter` commands + expected pass counts (SecurityScanService 3, SecurityScanEndpoint 6, SecurityScanWorker 6) and exact `node --check` / grep verifications are given. All commits are local; every commit message ends with the required `Co-Authored-By` trailer.

**Type consistency:** `ScanResult(SecurityVerdict Verdict, string? RawFindingsJson, string? RawVerdict)` and `SecurityVerdict` (10 fields: AgentAddress, Status, Score, Grade, ObservableCount, FindingCount, SeverityCountsJson, ScannedAt, CorpusVersion, LastError) are used verbatim from the in-tree definitions in Tasks 1/2/4. `SecurityScanService.ScanAndPersistAsync(string, SecurityVerdictRepository, SecurityScanHistoryRepository, CancellationToken) -> Task<ScanResult>` is identical at: the service definition (Task 1), the worker call site (Task 2), the test shim shape (Tasks 1/4), and the `Program.cs` route's `svc.ScanAndPersistAsync` delegate binding (Task 4) — which exactly matches `SecurityScanEndpoint.HandleAsync`'s `Func<string, SecurityVerdictRepository, SecurityScanHistoryRepository, CancellationToken, Task<ScanResult>>` parameter. `SecurityScanHistoryRepository.AppendAsync` (12 params + ct, `rawVerdict` param name) and `ListByAgentAsync` / `SecurityVerdictRepository.UpsertAsync` / `GetByAgentAsync` are used with their exact in-tree signatures. `ITheSecurityBotClient.ScanAsync -> Task<ScanResult>` matches the fakes in Tasks 1/2 — note the worker NO LONGER takes `ITheSecurityBotClient` (the param + `_client` field are dropped in Task 2); only `SecurityScanService` depends on the client now, and the worker tests register the FakeClient in the `ServiceCollection` so the scope-resolved service still scans through it. The worker ctor is therefore `SecurityScanWorker(IServiceScopeFactory, IConfiguration, ILogger<SecurityScanWorker>)` at both the class definition (Task 2) and the test `MakeWorker` call site (Task 2). `AdminSecurityScanRequest(string? AgentAddress)` is constructed identically in the endpoint + tests. The endpoint's `RawJsonResult`/`JsonError(int,string)`/`JsonOpts` (camelCase) are lifted verbatim from `RiskAttestProEndpoint` so the success body + 400 envelope are camelCase and the handler is unit-testable against a bare `DefaultHttpContext`. `SecurityStatus.Scanned/NotAuditable/Error` constants used consistently; `lastError` appears in `security_scan_history` (server-side) but in NO response projection.