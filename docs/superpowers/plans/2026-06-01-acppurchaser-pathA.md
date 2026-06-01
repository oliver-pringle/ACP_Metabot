# ACPPurchaser Path A Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `purchase_quote` + `purchase_execute` on TheMetaBot so it can buy a fixed-price offering on another agent on a buyer's behalf, using the marketplace Require-Funds primitive, bounded by layered safety guards.

**Architecture:** Two-tier (existing). C# `.Api` owns the money-safety logic (atomic per-buyer/day cap, risk gate, audit) over ADO.NET+SQLite and exposes 3 endpoints. The Node `acp-v2` sidecar owns ACP: `purchase_quote` is a normal offering; `purchase_execute` is special-cased in `seller.ts` — it calls `setBudgetWithFundRequest`, then does the inner hire (a `PurchaserBuyer` adapted from ACP_Tester's `buyer.ts`, reusing the single seller `AcpAgent`), then submits the inner deliverable or rejects. The fund transfer reimburses Metabot's wallet on completion, so the bot fronts the inner cost against escrow-locked funds.

**Tech Stack:** .NET 10 minimal APIs, `Microsoft.Data.Sqlite` (ADO.NET), xUnit 2.9; Node 22 + TypeScript 5.7, `@virtuals-protocol/acp-node-v2` 0.0.6.

**Spec:** `docs/superpowers/specs/2026-06-01-acppurchaser-pathA-design.md`

---

## File Structure

**`.Api` (C#):**
- Create `ACP_Metabot.Api/Services/PurchaserBudgetService.cs` — atomic per-buyer/day USDC cap (lifted from ACP_PrivateTrader `ClaudeBudgetService`, + `buyerKey`).
- Create `ACP_Metabot.Api/Services/PurchaserService.cs` — `QuoteAsync` / `PrecheckAsync` / `SettleAsync` + private audit SQL.
- Modify `ACP_Metabot.Api/Data/Db.cs` — 2 tables in `InitializeSchemaAsync`.
- Modify `ACP_Metabot.Api/Program.cs` — DI registrations + 3 endpoints + request records.

**`.Api` tests (xUnit):**
- Create `ACP_Metabot.Api.Tests/PurchaserBudgetServiceTests.cs`
- Create `ACP_Metabot.Api.Tests/PurchaserServiceTests.cs`
- Modify `ACP_Metabot.Api.Tests/DbTests.cs` — assert the 2 new tables (new `[Fact]`).

**`acp-v2` sidecar (TS):**
- Modify `acp-v2/src/pricing.ts` — 2 prices.
- Modify `acp-v2/src/apiClient.ts` — 3 client methods + interface decls.
- Modify `acp-v2/src/offerings/types.ts` — add `agent` to `OfferingContext`.
- Create `acp-v2/src/offerings/purchaseQuote.ts` — normal offering.
- Create `acp-v2/src/offerings/purchaseExecute.ts` — offering metadata (schemas/validate) for registration; execution is special-cased in seller.ts.
- Modify `acp-v2/src/offerings/registry.ts` — register both.
- Create `acp-v2/src/purchaserBuyer.ts` — inner-hire engine (adapted AcpBuyer).
- Modify `acp-v2/src/seller.ts` — pass `agent` into ctx; inner-event dispatch; `purchase_execute` branch.

**Note on sidecar tests:** the sidecar has no test runner (portfolio convention: no vitest in bots). Sidecar correctness is verified by `npm run build` (tsc) + `npm run print-offerings` (P32 gate) + the live real-hire smoke (Task 14). All money-critical logic (cap, risk verdict, reject paths) lives in the C# tier and IS unit-tested.

---

## PHASE A — `.Api` business tier (TDD, xUnit)

Run all C# tests with:
`dotnet test C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj`

### Task 1: PurchaserBudgetService (atomic per-buyer/day cap)

**Files:**
- Create: `ACP_Metabot.Api/Data/Db.cs` change (table) — see Step 3
- Create: `ACP_Metabot.Api/Services/PurchaserBudgetService.cs`
- Test: `ACP_Metabot.Api.Tests/PurchaserBudgetServiceTests.cs`

- [ ] **Step 1: Add the `acppurchaser_daily_spend` table to Db.cs**

In `ACP_Metabot.Api/Data/Db.cs`, inside `InitializeSchemaAsync()`'s big `cmd.CommandText` SQL batch, immediately AFTER the `agent_risk_cache` table block (around line 441, after its closing `);`), insert:

```sql
            CREATE TABLE IF NOT EXISTS acppurchaser_daily_spend (
                buyer_key   TEXT NOT NULL,
                day_iso     TEXT NOT NULL,
                total_usd   REAL NOT NULL DEFAULT 0.0,
                updated_at  TEXT NOT NULL,
                PRIMARY KEY (buyer_key, day_iso)
            );

            CREATE TABLE IF NOT EXISTS acppurchaser_audit (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                outer_job_id    TEXT,
                buyer_key       TEXT NOT NULL,
                target_agent    TEXT NOT NULL,
                target_offering TEXT NOT NULL,
                downstream_usd  REAL,
                service_fee_usd REAL,
                inner_job_id    TEXT,
                state           TEXT NOT NULL,
                reason          TEXT,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_acppurchaser_audit_buyer ON acppurchaser_audit(buyer_key, created_at DESC);
```

(Both tables are added together here so Task 2 needs no further Db change.)

- [ ] **Step 2: Write the failing test**

Create `ACP_Metabot.Api.Tests/PurchaserBudgetServiceTests.cs`:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class PurchaserBudgetServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly PurchaserBudgetService _svc;

    public PurchaserBudgetServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_purch_budget_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
                ["ACPPURCHASER_DAILY_CAP_USDC"] = "10.0",
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _svc = new PurchaserBudgetService(_db, config, NullLogger<PurchaserBudgetService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Reserve_under_cap_succeeds_and_accumulates()
    {
        var r1 = await _svc.TryReserveAsync("0xbuyer", 4m, CancellationToken.None);
        Assert.True(r1.Reserved);
        var r2 = await _svc.TryReserveAsync("0xbuyer", 4m, CancellationToken.None);
        Assert.True(r2.Reserved);
        Assert.Equal(8m, r2.DayTotalUsd);
    }

    [Fact]
    public async Task Reserve_over_cap_is_rejected_and_not_charged()
    {
        Assert.True((await _svc.TryReserveAsync("0xbuyer", 9m, CancellationToken.None)).Reserved);
        var over = await _svc.TryReserveAsync("0xbuyer", 5m, CancellationToken.None);
        Assert.False(over.Reserved);
        Assert.Equal(9m, await _svc.GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
    }

    [Fact]
    public async Task Refund_restores_headroom()
    {
        await _svc.TryReserveAsync("0xbuyer", 9m, CancellationToken.None);
        await _svc.RecordActualSpendAsync("0xbuyer", -9m, CancellationToken.None);
        Assert.Equal(0m, await _svc.GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
        Assert.True((await _svc.TryReserveAsync("0xbuyer", 9m, CancellationToken.None)).Reserved);
    }

    [Fact]
    public async Task Caps_are_isolated_per_buyer()
    {
        Assert.True((await _svc.TryReserveAsync("0xA", 9m, CancellationToken.None)).Reserved);
        var b = await _svc.TryReserveAsync("0xB", 9m, CancellationToken.None);
        Assert.True(b.Reserved); // 0xB has its own bucket
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj --filter PurchaserBudgetServiceTests`
Expected: FAIL — `PurchaserBudgetService` does not exist (compile error).

- [ ] **Step 4: Write PurchaserBudgetService.cs**

Create `ACP_Metabot.Api/Services/PurchaserBudgetService.cs`:

```csharp
using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj --filter PurchaserBudgetServiceTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add ACP_Metabot.Api/Services/PurchaserBudgetService.cs ACP_Metabot.Api/Data/Db.cs ACP_Metabot.Api.Tests/PurchaserBudgetServiceTests.cs
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "feat(acppurchaser): atomic per-buyer/day budget cap + tables"
```

### Task 2: Db schema test for the two new tables

**Files:**
- Test: `ACP_Metabot.Api.Tests/DbTests.cs` (add a `[Fact]`)

- [ ] **Step 1: Write the failing test** — add this method inside the existing `DbTests` class:

```csharp
[Fact]
public async Task InitAsync_creates_acppurchaser_tables()
{
    await _db.InitializeSchemaAsync();
    await using var conn = _db.OpenConnection();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT name FROM sqlite_master WHERE type='table'
                        AND name IN ('acppurchaser_daily_spend','acppurchaser_audit');";
    var found = new List<string>();
    await using var rdr = await cmd.ExecuteReaderAsync();
    while (await rdr.ReadAsync()) found.Add(rdr.GetString(0));
    Assert.Contains("acppurchaser_daily_spend", found);
    Assert.Contains("acppurchaser_audit", found);
}
```

- [ ] **Step 2: Run** `dotnet test ... --filter DbTests` → Expected: PASS (the tables already exist from Task 1 Step 1).
- [ ] **Step 3: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add ACP_Metabot.Api.Tests/DbTests.cs
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "test(acppurchaser): assert new tables created"
```

### Task 3: PurchaserService (quote / precheck / settle)

**Files:**
- Create: `ACP_Metabot.Api/Services/PurchaserService.cs`
- Test: `ACP_Metabot.Api.Tests/PurchaserServiceTests.cs`

Risk verdict mapping (used by both quote and precheck): `!fixedPrice` → `BLOCK` reason `not_fixed_price`; `RiskTier=="critical"` → `BLOCK` reason `risk_critical`; `RiskTier=="high"` → `CAUTION`; else `PROCEED`. `AgentRiskScorer.ScoreAsync` is a singleton already in DI; `RiskTier` is a lowercase string (`low`/`medium`/`high`/`critical`).

- [ ] **Step 1: Write the failing test**

Create `ACP_Metabot.Api.Tests/PurchaserServiceTests.cs`. Because `AgentRiskScorer` is a concrete sealed class with heavy deps, `PurchaserService` takes a small `IAgentRiskSource` seam (defined in the service file) so tests inject a stub:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class PurchaserServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly IConfiguration _config;

    public PurchaserServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_purch_svc_{Guid.NewGuid():N}.db");
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
            ["ACPPURCHASER_DAILY_CAP_USDC"] = "10.0",
        }).Build();
        _db = new Db(_config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private PurchaserService Make(string tier) =>
        new(_db, new PurchaserBudgetService(_db, _config, NullLogger<PurchaserBudgetService>.Instance),
            new StubRisk(tier), NullLogger<PurchaserService>.Instance);

    private sealed class StubRisk : IAgentRiskSource
    {
        private readonly string _tier;
        public StubRisk(string tier) => _tier = tier;
        public Task<string> RiskTierAsync(string agent, int chainId, CancellationToken ct) => Task.FromResult(_tier);
    }

    [Fact]
    public async Task Quote_fixed_low_risk_is_PROCEED()
    {
        var q = await Make("low").QuoteAsync("0xtarget", 0.05m, true, CancellationToken.None);
        Assert.Equal("PROCEED", q.Verdict);
        Assert.Equal(0.15m, q.TotalEscrowUsdc); // 0.10 + 0.05
    }

    [Fact]
    public async Task Quote_non_fixed_is_BLOCK()
    {
        var q = await Make("low").QuoteAsync("0xtarget", 0m, false, CancellationToken.None);
        Assert.Equal("BLOCK", q.Verdict);
        Assert.Contains("not_fixed_price", q.Reasons);
    }

    [Fact]
    public async Task Quote_critical_is_BLOCK()
    {
        var q = await Make("critical").QuoteAsync("0xtarget", 0.05m, true, CancellationToken.None);
        Assert.Equal("BLOCK", q.Verdict);
    }

    [Fact]
    public async Task Precheck_over_maxfunds_rejects_without_reserving()
    {
        var svc = Make("low");
        var r = await svc.PrecheckAsync("job1", "0xbuyer", "0xtarget", "spender_check", 0.20m, 0.05m, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal("over_max_funds", r.Reason);
        Assert.Equal(0m, await new PurchaserBudgetService(_db, _config, NullLogger<PurchaserBudgetService>.Instance).GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
    }

    [Fact]
    public async Task Precheck_ok_reserves_and_audits()
    {
        var svc = Make("low");
        var r = await svc.PrecheckAsync("job1", "0xbuyer", "0xtarget", "spender_check", 0.05m, 0.50m, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(0.05m, await new PurchaserBudgetService(_db, _config, NullLogger<PurchaserBudgetService>.Instance).GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
    }

    [Fact]
    public async Task Settle_rejected_refunds_reservation()
    {
        var svc = Make("low");
        await svc.PrecheckAsync("job1", "0xbuyer", "0xtarget", "spender_check", 0.05m, 0.50m, CancellationToken.None);
        await svc.SettleAsync("job1", "0xbuyer", "REJECTED", null, "downstream_failed", 0.05m, CancellationToken.None);
        Assert.Equal(0m, await new PurchaserBudgetService(_db, _config, NullLogger<PurchaserBudgetService>.Instance).GetTodaysSpendAsync("0xbuyer", CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run** `dotnet test ... --filter PurchaserServiceTests` → Expected: FAIL (compile — `PurchaserService`, `IAgentRiskSource` missing).

- [ ] **Step 3: Write PurchaserService.cs**

Create `ACP_Metabot.Api/Services/PurchaserService.cs`:

```csharp
using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;

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
```

- [ ] **Step 4: Run** `dotnet test ... --filter PurchaserServiceTests` → Expected: PASS (6 tests).
- [ ] **Step 5: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add ACP_Metabot.Api/Services/PurchaserService.cs ACP_Metabot.Api.Tests/PurchaserServiceTests.cs
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "feat(acppurchaser): PurchaserService quote/precheck/settle + tests"
```

### Task 4: DI + 3 endpoints + request records

**Files:**
- Modify: `ACP_Metabot.Api/Program.cs`

- [ ] **Step 1: Register services** — after `builder.Services.AddSingleton<AgentRiskScorer>();` (line ~184) add:

```csharp
builder.Services.AddSingleton<IAgentRiskSource, AgentRiskScorerSource>();
builder.Services.AddSingleton<PurchaserBudgetService>();
builder.Services.AddSingleton<PurchaserService>();
```

- [ ] **Step 2: Add the public quote endpoint** — near the other `/v1/buyer/*` endpoints (after the `/v1/buyer/budget-check` block ~line 1158) add:

```csharp
app.MapPost("/v1/buyer/purchase/quote",
    async (PurchaseQuoteRequest req, PurchaserService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.TargetAgent))
        return Results.BadRequest(new { error = "targetAgent required" });
    var q = await svc.QuoteAsync(req.TargetAgent, req.DownstreamUsdc, req.FixedPrice, ct);
    return Results.Ok(q);
}).RequireRateLimiting("public-compose");
```

- [ ] **Step 3: Add the internal precheck + settle endpoints** — near `/v1/internal/risk/watch` (~line 1595) add:

```csharp
app.MapPost("/v1/internal/buyer/purchase/precheck",
    async (PurchasePrecheckRequest req, PurchaserService svc, CancellationToken ct) =>
{
    var r = await svc.PrecheckAsync(req.OuterJobId, req.BuyerKey, req.TargetAgent, req.TargetOffering,
        req.DownstreamUsdc, req.MaxFundsUsdc, ct);
    return Results.Ok(r);
});

app.MapPost("/v1/internal/buyer/purchase/settle",
    async (PurchaseSettleRequest req, PurchaserService svc, CancellationToken ct) =>
{
    await svc.SettleAsync(req.OuterJobId, req.BuyerKey, req.State, req.InnerJobId, req.Reason, req.DownstreamUsdc, ct);
    return Results.Ok(new { ok = true });
});
```

(Both are under `/v1/internal/*` → auto-gated by the existing X-API-Key middleware.)

- [ ] **Step 4: Add request records** — near `public record BudgetCheckRequest(...)` (~line 2959) add:

```csharp
public record PurchaseQuoteRequest(string TargetAgent, decimal DownstreamUsdc, bool FixedPrice);
public record PurchasePrecheckRequest(string OuterJobId, string BuyerKey, string TargetAgent, string TargetOffering, decimal DownstreamUsdc, decimal MaxFundsUsdc);
public record PurchaseSettleRequest(string OuterJobId, string BuyerKey, string State, string? InnerJobId, string? Reason, decimal DownstreamUsdc);
```

- [ ] **Step 5: Build + full test run**

Run: `dotnet build C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/ACP_Metabot.Api.csproj`
Expected: Build succeeded, 0 warnings.
Run: `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj`
Expected: all tests PASS (existing + new).

- [ ] **Step 6: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add ACP_Metabot.Api/Program.cs
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "feat(acppurchaser): wire DI + quote/precheck/settle endpoints"
```

---

## PHASE B — `acp-v2` sidecar (TypeScript; build + print-offerings verified)

Run sidecar build with: `cd C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot/acp-v2 && npm run build`

### Task 5: Pricing

**Files:** Modify `acp-v2/src/pricing.ts`

- [ ] **Step 1:** In `PRICE_USDC`, after `riskAttestPro: 10.00,` add:

```typescript
  // ACPPurchaser Path A (R16 #1 cold-start fix). purchase_execute's $0.10 is
  // the SERVICE fee only; the downstream cost rides the Require-Funds transfer.
  purchase_quote: 0.02,
  purchase_execute: 0.10,
```

- [ ] **Step 2:** Build: `npm run build` → Expected: clean tsc (no new errors).
- [ ] **Step 3: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add acp-v2/src/pricing.ts
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "feat(acppurchaser): sidecar prices for purchase_quote/execute"
```

### Task 6: apiClient methods

**Files:** Modify `acp-v2/src/apiClient.ts`

- [ ] **Step 1:** In the `ApiClient` interface (near `budgetCheck(...)` ~line 273) add:

```typescript
  purchaseQuote(req: { targetAgent: string; downstreamUsdc: number; fixedPrice: boolean }): Promise<unknown>;
  purchasePrecheck(req: { outerJobId: string; buyerKey: string; targetAgent: string; targetOffering: string; downstreamUsdc: number; maxFundsUsdc: number }): Promise<{ ok: boolean; reason?: string; downstreamUsdc: number }>;
  purchaseSettle(req: { outerJobId: string; buyerKey: string; state: string; innerJobId?: string | null; reason?: string | null; downstreamUsdc: number }): Promise<unknown>;
```

- [ ] **Step 2:** In the returned object (near `budgetCheck: (req) => ...` ~line 433) add:

```typescript
    purchaseQuote: (req) =>
      request<unknown>("/v1/buyer/purchase/quote", { method: "POST", body: JSON.stringify(req) }),
    purchasePrecheck: (req) =>
      request<{ ok: boolean; reason?: string; downstreamUsdc: number }>(
        "/v1/internal/buyer/purchase/precheck", { method: "POST", body: JSON.stringify(req) }),
    purchaseSettle: (req) =>
      request<unknown>("/v1/internal/buyer/purchase/settle", { method: "POST", body: JSON.stringify(req) }),
```

- [ ] **Step 3:** Build: `npm run build` → Expected: clean.
- [ ] **Step 4: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add acp-v2/src/apiClient.ts
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "feat(acppurchaser): apiClient purchase quote/precheck/settle"
```

### Task 7: OfferingContext.agent + purchase_quote offering

**Files:**
- Modify: `acp-v2/src/offerings/types.ts`
- Create: `acp-v2/src/offerings/purchaseQuote.ts`
- Modify: `acp-v2/src/offerings/registry.ts`
- Modify: `acp-v2/src/seller.ts` (pass agent into ctx)

- [ ] **Step 1:** In `acp-v2/src/offerings/types.ts`, change the imports + `OfferingContext`:

```typescript
import type { ValidationResult } from "../validators.js";
import type { ApiClient } from "../apiClient.js";
import type { JobSession, AcpAgent } from "@virtuals-protocol/acp-node-v2";

export interface OfferingContext {
  client: ApiClient;
  session: JobSession;
  agent: AcpAgent;
}
```

- [ ] **Step 2:** In `acp-v2/src/seller.ts`, `handleJobFunded` passes the agent. Change the `route(...)` call:

```typescript
    const outcome = await route(stash.offeringName, stash.requirement, { client, session, agent });
```

(`agent` is in scope — it's the `const agent = await AcpAgent.create(...)` in `main()`.)

- [ ] **Step 3:** Create `acp-v2/src/offerings/purchaseQuote.ts`:

```typescript
import type { Offering } from "./types.js";

const FIXED_PRICE_TYPES = new Set(["fixed"]); // verify literal via acp_browse_agent in smoke

export const purchaseQuote: Offering = {
  name: "purchase_quote",
  description:
    "Given a target agent + offering, returns the exact downstream USDC cost, the total escrow " +
    "for purchase_execute (service fee + downstream), and a pre-hire safety verdict " +
    "(PROCEED/CAUTION/BLOCK) from the target's scam-risk tier. No money moves. The pre-flight for purchase_execute.",
  requirementSchema: {
    type: "object",
    properties: {
      targetAgent: { type: "string", description: "Wallet address of the seller agent to hire on the buyer's behalf." },
      targetOffering: { type: "string", description: "Exact offering name on the target agent to purchase." },
      requirement: { type: "object", description: "The requirement payload that purchase_execute would forward to the target offering.", properties: {} },
    },
    required: ["targetAgent", "targetOffering"],
  },
  requirementExample: {
    targetAgent: "0xbd95e7235d6f0b1a2b3c4d5e6f7a8b9c0d1e2f30",
    targetOffering: "spender_check",
    requirement: { chainId: 1, spender: "0x6b7a87899490EcE95443e979cA9485CBE7E71522" },
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["targetAgent", "downstreamUsdc", "serviceFeeUsdc", "totalEscrowUsdc", "fixedPrice", "riskTier", "verdict"],
    properties: {
      targetAgent: { type: "string", description: "Resolved target agent wallet address." },
      downstreamUsdc: { type: "number", description: "Fixed USDC price of the target offering (0 if not fixed-price)." },
      serviceFeeUsdc: { type: "number", description: "ACPPurchaser service fee charged by purchase_execute (0.10)." },
      totalEscrowUsdc: { type: "number", description: "What the buyer escrows on purchase_execute = serviceFee + downstreamUsdc." },
      fixedPrice: { type: "boolean", description: "True if the target is fixed-price and supported by purchase_execute v1." },
      riskTier: { type: "string", description: "Pre-hire scam-risk tier of the target agent: low|medium|high|critical." },
      verdict: { type: "string", description: "PROCEED|CAUTION|BLOCK recommendation for purchase_execute." },
      reasons: { type: "array", items: { type: "string", description: "Reason contributing to the verdict." }, description: "Reasons behind the verdict." },
    },
  },
  deliverableExample: {
    targetAgent: "0xbd95e7235d6f0b1a2b3c4d5e6f7a8b9c0d1e2f30",
    downstreamUsdc: 0.02, serviceFeeUsdc: 0.10, totalEscrowUsdc: 0.12,
    fixedPrice: true, riskTier: "low", verdict: "PROCEED", reasons: ["ok"],
  },
  validate(req) {
    if (typeof req.targetAgent !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.targetAgent))
      return { valid: false, reason: "targetAgent must be a 0x EVM address" };
    if (typeof req.targetOffering !== "string" || req.targetOffering.length === 0)
      return { valid: false, reason: "targetOffering required" };
    return { valid: true };
  },
  async execute(req, { client, agent }) {
    const targetAgent = req.targetAgent as string;
    const targetOffering = req.targetOffering as string;
    const detail = await agent.getAgentByWalletAddress(targetAgent);
    const off = detail?.offerings.find((o) => o.name === targetOffering);
    if (!off) {
      return { targetAgent, downstreamUsdc: 0, serviceFeeUsdc: 0.10, totalEscrowUsdc: 0,
        fixedPrice: false, riskTier: "unknown", verdict: "BLOCK", reasons: ["target_not_found"] };
    }
    const fixedPrice = FIXED_PRICE_TYPES.has((off.priceType || "").toLowerCase());
    const downstreamUsdc = fixedPrice ? Number(off.priceValue) : 0;
    return await client.purchaseQuote({ targetAgent, downstreamUsdc, fixedPrice });
  },
};
```

- [ ] **Step 4:** In `acp-v2/src/offerings/registry.ts`, add the import (with the others) and register it in `OFFERINGS`:

```typescript
import { purchaseQuote } from "./purchaseQuote.js";
```
and inside the `OFFERINGS` object (after `riskAttestPro,`):
```typescript
  // ACPPurchaser Path A (R16 #1). purchase_quote is a normal offering;
  // purchase_execute is special-cased in seller.ts (Require-Funds + inner hire).
  purchase_quote: purchaseQuote,
```

- [ ] **Step 5:** Build + print-offerings:

Run: `cd acp-v2 && npm run build` → Expected: clean.
Run: `npm run print-offerings` → Expected: renders `purchase_quote`, passes the P32 description gate, name ≤ 20, desc ≤ 500.

- [ ] **Step 6: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add acp-v2/src/offerings/types.ts acp-v2/src/offerings/purchaseQuote.ts acp-v2/src/offerings/registry.ts acp-v2/src/seller.ts
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "feat(acppurchaser): purchase_quote offering + OfferingContext.agent"
```

### Task 8: PurchaserBuyer (inner-hire engine)

**Files:** Create `acp-v2/src/purchaserBuyer.ts`

Adapted from `ACP_Tester/src/buyer.ts`: reuses the EXISTING seller `AcpAgent` (no second agent), discriminates inner jobs via the `pending` map, and serializes inner hires with a mutex (one in-flight; single-Privy-wallet constraint).

- [ ] **Step 1:** Create `acp-v2/src/purchaserBuyer.ts`:

```typescript
import type { AcpAgent, JobSession, JobRoomEntry } from "@virtuals-protocol/acp-node-v2";

export type HireStatus = "completed" | "rejected" | "expired" | "timeout" | "error";

export interface HireResult {
  jobId: string;
  status: HireStatus;
  deliverableParsed?: unknown;
  error?: string;
  durationMs: number;
}

interface PendingHire {
  jobId: string;
  funded: boolean;
  deliverable?: string;
  resolve: (r: HireResult) => void;
  startedAt: number;
  timer?: NodeJS.Timeout;
}

/**
 * Inner-hire engine for ACPPurchaser. Reuses the seller's single AcpAgent.
 * seller.ts routes any entry whose jobId isInnerJob() here. Inner hires are
 * serialized (one in-flight) — the SDK signs from one Privy wallet per process,
 * and serialization bounds the fronted-float exposure.
 */
export class PurchaserBuyer {
  private readonly pending = new Map<string, PendingHire>();
  private chain: Promise<void> = Promise.resolve();

  constructor(
    private readonly agent: AcpAgent,
    private readonly chainId: number
  ) {}

  isInnerJob(jobId: string): boolean {
    return this.pending.has(jobId);
  }

  /** Serialized: queue behind any in-flight inner hire. */
  async hireOnBehalf(
    targetAgent: string,
    targetOffering: string,
    requirement: Record<string, unknown>,
    timeoutMs: number
  ): Promise<HireResult> {
    const run = this.chain.then(() => this.doHire(targetAgent, targetOffering, requirement, timeoutMs));
    // keep the chain alive regardless of this hire's outcome
    this.chain = run.then(() => undefined, () => undefined);
    return run;
  }

  private async doHire(
    targetAgent: string,
    targetOffering: string,
    requirement: Record<string, unknown>,
    timeoutMs: number
  ): Promise<HireResult> {
    const detail = await this.agent.getAgentByWalletAddress(targetAgent);
    if (!detail) return this.fail("error", `target ${targetAgent} not found`);
    const offering = detail.offerings.find((o) => o.name === targetOffering);
    if (!offering) return this.fail("error", `offering ${targetOffering} not found on ${detail.name}`);

    const jobIdBig = await this.agent.createJobFromOffering(this.chainId, offering, targetAgent, requirement);
    const jobId = jobIdBig.toString();
    console.log(`[purchaser] inner job ${jobId} created -> ${detail.name}/${targetOffering}`);

    return await new Promise<HireResult>((resolve) => {
      const startedAt = Date.now();
      const p: PendingHire = { jobId, funded: false, resolve, startedAt };
      this.pending.set(jobId, p);
      p.timer = setTimeout(() => {
        if (this.pending.has(jobId)) {
          this.pending.delete(jobId);
          resolve({ jobId, status: "timeout", error: `no completion within ${timeoutMs}ms`, durationMs: Date.now() - startedAt });
        }
      }, timeoutMs);
    });
  }

  private fail(status: HireStatus, error: string): HireResult {
    return { jobId: "", status, error, durationMs: 0 };
  }

  /** Called by seller.ts for any entry whose session.jobId is an inner job. */
  async handleInnerEntry(session: JobSession, entry: JobRoomEntry): Promise<void> {
    const jobId = session.jobId;
    const p = this.pending.get(jobId);
    if (!p) return;
    if (entry.kind !== "system") return;
    const ev = entry.event;
    try {
      switch (ev.type) {
        case "budget.set":
          if (!p.funded) {
            p.funded = true;
            await session.fund(); // funds the inner downstream cost from our wallet
            console.log(`[purchaser] funded inner job ${jobId}`);
          }
          return;
        case "job.completed":
          if (!p.deliverable) await this.recoverDeliverable(jobId, p);
          this.settle(jobId, "completed");
          return;
        case "job.rejected":
          this.settle(jobId, "rejected", (ev as { reason?: string }).reason);
          return;
        case "job.expired":
          this.settle(jobId, "expired");
          return;
      }
    } catch (err) {
      this.settle(jobId, "error", err instanceof Error ? err.message : String(err));
    }
  }

  private async recoverDeliverable(jobId: string, p: PendingHire): Promise<void> {
    const delays = [0, 500, 1500];
    for (const delay of delays) {
      if (delay) await new Promise((r) => setTimeout(r, delay));
      try {
        const history = await this.agent.getTransport().getHistory(this.chainId, jobId);
        for (let i = history.length - 1; i >= 0; i--) {
          const e = history[i];
          if (e.kind === "system" && e.event.type === "job.submitted") {
            p.deliverable = (e.event as { deliverable?: string }).deliverable;
            return;
          }
        }
      } catch { /* indexing lag — retry */ }
    }
  }

  private settle(jobId: string, status: HireStatus, reasonOrError?: string): void {
    const p = this.pending.get(jobId);
    if (!p) return;
    this.pending.delete(jobId);
    if (p.timer) clearTimeout(p.timer);
    let parsed: unknown;
    if (p.deliverable) { try { parsed = JSON.parse(p.deliverable); } catch { /* non-JSON */ } }
    p.resolve({
      jobId, status, deliverableParsed: parsed,
      error: status === "completed" ? undefined : reasonOrError ?? `inner job ${status}`,
      durationMs: Date.now() - p.startedAt,
    });
  }
}
```

- [ ] **Step 2:** Build: `cd acp-v2 && npm run build` → Expected: clean (the file is imported in Task 9; build it together if tsc prunes unused — otherwise this is type-checked standalone).
- [ ] **Step 3: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add acp-v2/src/purchaserBuyer.ts
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "feat(acppurchaser): inner-hire engine (reuses seller agent, serialized)"
```

### Task 9: purchase_execute offering metadata + seller.ts orchestration

**Files:**
- Create: `acp-v2/src/offerings/purchaseExecute.ts` (metadata only — for registration/validate/schemas)
- Modify: `acp-v2/src/offerings/registry.ts` (register)
- Modify: `acp-v2/src/seller.ts` (instantiate PurchaserBuyer; inner-event dispatch; `purchase_execute` branch in handleRequirement + handleJobFunded)

- [ ] **Step 1:** Create `acp-v2/src/offerings/purchaseExecute.ts`:

```typescript
import type { Offering } from "./types.js";

// purchase_execute's execute() is NEVER called via the normal router — seller.ts
// special-cases it (Require-Funds + inner hire). This object exists for
// registration, schema, and validate() only.
export const purchaseExecute: Offering = {
  name: "purchase_execute",
  description:
    "Hires a fixed-price offering on another agent on your behalf and returns its deliverable + on-chain job id. " +
    "You escrow a $0.10 service fee plus the downstream cost (Require-Funds); the bot pays the seller and is " +
    "reimbursed on completion. Set maxFundsUsdc to cap the downstream spend.",
  requirementSchema: {
    type: "object",
    properties: {
      targetAgent: { type: "string", description: "Wallet address of the seller agent to hire on your behalf." },
      targetOffering: { type: "string", description: "Exact offering name on the target agent to purchase." },
      requirement: { type: "object", description: "The requirement payload forwarded verbatim to the target offering.", properties: {} },
      maxFundsUsdc: { type: "number", description: "Your ceiling on the downstream cost; the hire is rejected if the target costs more." },
    },
    required: ["targetAgent", "targetOffering", "maxFundsUsdc"],
  },
  requirementExample: {
    targetAgent: "0xbd95e7235d6f0b1a2b3c4d5e6f7a8b9c0d1e2f30",
    targetOffering: "spender_check",
    requirement: { chainId: 1, spender: "0x6b7a87899490EcE95443e979cA9485CBE7E71522" },
    maxFundsUsdc: 0.05,
  },
  slaMinutes: 10,
  deliverableSchema: {
    type: "object",
    required: ["status", "targetAgent", "targetOffering"],
    properties: {
      status: { type: "string", description: "DELIVERED|REJECTED outcome of the buy-on-behalf." },
      targetAgent: { type: "string", description: "The agent that was hired." },
      targetOffering: { type: "string", description: "The offering that was purchased." },
      innerJobId: { type: "string", description: "On-chain job id of the inner hire, or null if it never started." },
      downstreamUsdc: { type: "number", description: "USDC paid to the target seller for the inner hire." },
      serviceFeeUsdc: { type: "number", description: "Service fee retained by ACPPurchaser (0.10)." },
      deliverable: { type: "object", description: "The parsed deliverable from the target offering, or null on REJECTED.", properties: {} },
      reason: { type: "string", description: "On REJECTED, why (over_max_funds, risk_critical, daily_cap_exceeded, downstream_failed, ...)." },
    },
  },
  deliverableExample: {
    status: "DELIVERED",
    targetAgent: "0xbd95e7235d6f0b1a2b3c4d5e6f7a8b9c0d1e2f30",
    targetOffering: "spender_check", innerJobId: "7704", downstreamUsdc: 0.02, serviceFeeUsdc: 0.10,
    deliverable: { verdict: "high_risk" }, reason: "",
  },
  validate(req) {
    if (typeof req.targetAgent !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.targetAgent))
      return { valid: false, reason: "targetAgent must be a 0x EVM address" };
    if (typeof req.targetOffering !== "string" || req.targetOffering.length === 0)
      return { valid: false, reason: "targetOffering required" };
    if (typeof req.maxFundsUsdc !== "number" || !(req.maxFundsUsdc > 0))
      return { valid: false, reason: "maxFundsUsdc must be a positive number" };
    return { valid: true };
  },
  // Never invoked — seller.ts handles purchase_execute. Throw to make a routing bug loud.
  async execute() {
    throw new Error("purchase_execute is handled in seller.ts, not via the router");
  },
};
```

- [ ] **Step 2:** Register in `acp-v2/src/offerings/registry.ts` — add import:

```typescript
import { purchaseExecute } from "./purchaseExecute.js";
```
and in `OFFERINGS` (right after `purchase_quote: purchaseQuote,`):
```typescript
  purchase_execute: purchaseExecute,
```

- [ ] **Step 3:** In `acp-v2/src/seller.ts`, add imports at the top (with the other imports):

```typescript
import { getChain } from "./chain.js";
import { PurchaserBuyer } from "./purchaserBuyer.js";
import { AssetToken } from "@virtuals-protocol/acp-node-v2";
```

- [ ] **Step 4:** In `seller.ts` `main()`, after `const agent = await AcpAgent.create({ provider });` add:

```typescript
  const chainId = getChain(env.chain).id;
  const purchaser = new PurchaserBuyer(agent, chainId);
```

- [ ] **Step 5:** In `seller.ts`, change the `PendingJob` type to carry execute state:

```typescript
type PendingJob =
  | { kind: "normal"; offeringName: string; requirement: Record<string, unknown> }
  | { kind: "execute"; offeringName: "purchase_execute"; requirement: Record<string, unknown>;
      targetAgent: string; targetOffering: string; innerRequirement: Record<string, unknown>;
      downstreamUsdc: number; buyerKey: string };
```

- [ ] **Step 6:** In `seller.ts` `agent.on("entry", ...)`, at the VERY TOP of the handler (before the `if (entry.kind === "system")` block) add the inner-job dispatch:

```typescript
      // Inner hires (ACPPurchaser purchase_execute) ride this same agent. Route
      // their events to the buyer engine and stop — they are NOT seller jobs.
      if (purchaser.isInnerJob(session.jobId)) {
        await purchaser.handleInnerEntry(session, entry);
        return;
      }
```

- [ ] **Step 7:** In `seller.ts` `handleRequirement`, replace the tail (from `const offering = getOffering(offeringName);` to the end of the function) with a branch that special-cases `purchase_execute`:

```typescript
    const offering = getOffering(offeringName);
    if (!offering) {
      await session.sendMessage(`unknown offering: ${offeringName}`);
      return;
    }
    const v = offering.validate(requirement);
    if (!v.valid) {
      await session.sendMessage(v.reason ?? "validation failed");
      return;
    }

    if (offeringName === "purchase_execute") {
      const targetAgent = String(requirement.targetAgent);
      const targetOffering = String(requirement.targetOffering);
      const maxFundsUsdc = Number(requirement.maxFundsUsdc);
      const innerRequirement = (requirement.requirement ?? {}) as Record<string, unknown>;

      // Resolve the downstream price live; reject non-fixed / not-found here.
      const detail = await agent.getAgentByWalletAddress(targetAgent);
      const off = detail?.offerings.find((o) => o.name === targetOffering);
      if (!off) { await session.sendMessage(`target offering not found: ${targetAgent}/${targetOffering}`); return; }
      const fixedPrice = (off.priceType || "").toLowerCase() === "fixed";
      if (!fixedPrice) { await session.sendMessage("purchase_execute v1 supports fixed-price targets only"); return; }
      const downstreamUsdc = Number(off.priceValue);

      const buyerKey = (session.job ?? (await session.fetchJob())).clientAddress;
      const pre = await client.purchasePrecheck({
        outerJobId: session.jobId, buyerKey, targetAgent, targetOffering, downstreamUsdc, maxFundsUsdc,
      });
      if (!pre.ok) { await session.sendMessage(`precheck rejected: ${pre.reason}`); return; }

      // Service fee = our budget; downstream cost = the fund request to our own wallet.
      await session.setBudgetWithFundRequest(
        AssetToken.usdc(0.10, session.chainId),
        AssetToken.usdc(downstreamUsdc, session.chainId),
        env.walletAddress as `0x${string}`,
      );
      pending.set(session.jobId, {
        kind: "execute", offeringName: "purchase_execute", requirement,
        targetAgent, targetOffering, innerRequirement, downstreamUsdc, buyerKey,
      });
      return;
    }

    const price = await priceForAssetToken(offeringName, requirement, session.chainId);
    await session.setBudget(price);
    pending.set(session.jobId, { kind: "normal", offeringName, requirement });
```

- [ ] **Step 8:** In `seller.ts` `handleJobFunded`, branch on the stash kind:

```typescript
  async function handleJobFunded(session: JobSession) {
    const stash = pending.get(session.jobId);
    if (!stash) {
      console.warn(`[seller] job.funded without stashed requirement, jobId=${session.jobId}`);
      return;
    }

    if (stash.kind === "execute") {
      const hire = await purchaser.hireOnBehalf(stash.targetAgent, stash.targetOffering, stash.innerRequirement, 240_000);
      if (hire.status === "completed" && hire.deliverableParsed !== undefined) {
        const deliverable = {
          status: "DELIVERED", targetAgent: stash.targetAgent, targetOffering: stash.targetOffering,
          innerJobId: hire.jobId, downstreamUsdc: stash.downstreamUsdc, serviceFeeUsdc: 0.10,
          deliverable: hire.deliverableParsed, reason: "",
        };
        await session.submit(await toDeliverable(session.jobId, deliverable));
        await client.purchaseSettle({ outerJobId: session.jobId, buyerKey: stash.buyerKey, state: "DELIVERED",
          innerJobId: hire.jobId, reason: null, downstreamUsdc: stash.downstreamUsdc });
        console.log(`[seller] purchase_execute DELIVERED jobId=${session.jobId} inner=${hire.jobId}`);
      } else {
        const reason = `downstream_failed:${hire.status}${hire.error ? `:${hire.error}` : ""}`;
        await session.reject(reason);
        await client.purchaseSettle({ outerJobId: session.jobId, buyerKey: stash.buyerKey, state: "REJECTED",
          innerJobId: hire.jobId || null, reason, downstreamUsdc: stash.downstreamUsdc });
        console.log(`[seller] purchase_execute REJECTED jobId=${session.jobId} reason=${reason}`);
      }
      return;
    }

    const outcome = await route(stash.offeringName, stash.requirement, { client, session, agent });
    if (!outcome.ok) {
      await session.sendMessage(`execution failed: ${outcome.reason}`);
      return;
    }
    const payload = await toDeliverable(session.jobId, outcome.result);
    await session.submit(payload);
    console.log(`[seller] submitted jobId=${session.jobId} offering=${stash.offeringName}`);
  }
```

- [ ] **Step 9:** Build + print-offerings:

Run: `cd acp-v2 && npm run build` → Expected: clean tsc.
Run: `npm run print-offerings` → Expected: renders `purchase_quote` + `purchase_execute`, P32 gate passes, names ≤ 20.

- [ ] **Step 10: Commit**

```bash
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot add acp-v2/src/offerings/purchaseExecute.ts acp-v2/src/offerings/registry.ts acp-v2/src/seller.ts
git -C C:/code_crypto/ACP/ACP_Metabot/ACP_Metabot commit -m "feat(acppurchaser): purchase_execute orchestration in seller (Require-Funds + inner hire)"
```

---

## PHASE C — verification gates (no new code)

### Task 10: Full build + test gate

- [ ] **Step 1:** `.Api` build: `dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj` → 0 warnings.
- [ ] **Step 2:** `.Api` tests: `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj` → all PASS.
- [ ] **Step 3:** sidecar: `cd acp-v2 && npm run build` → clean tsc.
- [ ] **Step 4:** offerings: `npm run print-offerings` → P32 gate passes; capture the rendered `purchase_quote` + `purchase_execute` blocks for marketplace registration.
- [ ] **Step 5:** If anything fails, STOP and fix before the smoke (do NOT register/deploy a failing build).

### Task 11 (GATED — Oliver runs / requires deploy + real USDC): register + smoke

These steps need the droplet deploy + app.virtuals.io + real USDC; do them when Oliver is available. NOT part of the autonomous code build.

- [ ] **Step 1:** Deploy Metabot api + acp sidecar to the droplet (acp-bot-deploy skill: `docker compose stop metabot-api metabot-acp` → `up -d --build`, sequential).
- [ ] **Step 2:** Register `purchase_quote` (Price 0.02, Require Funds OFF) and `purchase_execute` (Price Fixed 0.10, **Require Funds ON**) on app.virtuals.io → TheMetaBot agent. Use the rendered blocks. Verify names ≤ 20, descriptions ≤ 500.
- [ ] **Step 3:** Quote smoke: `mcp__acp-tester__acp_hire(TheMetaBot, "purchase_quote", { targetAgent: "<RevokeBot 0xbd95…7df8>", targetOffering: "spender_check", requirement: { chainId: 1, spender: "0x6b7a87899490EcE95443e979cA9485CBE7E71522" } })` → expect `verdict` + `downstreamUsdc` ≈ 0.02 + `totalEscrowUsdc` ≈ 0.12.
- [ ] **Step 4:** Execute smoke: `mcp__acp-tester__acp_hire(TheMetaBot, "purchase_execute", { targetAgent: "<RevokeBot>", targetOffering: "spender_check", requirement: {...}, maxFundsUsdc: 0.05 })` → expect `status: "DELIVERED"`, `innerJobId` present, `deliverable.verdict` = "high_risk", and on BaseScan: buyer escrowed ≈ 0.12, an inner job created against RevokeBot, Metabot net ≈ +0.10. Confirm an `acppurchaser_audit` row state=DELIVERED.
- [ ] **Step 5:** Failure smoke: hire `purchase_execute` with `maxFundsUsdc: 0.001` (below the 0.02 target) → expect `status: "REJECTED"`, reason `over_max_funds`, buyer refunded, NO inner job, audit row REJECTED.
- [ ] **Step 6:** Diff marketplace-vs-source via `acp_browse_agent(TheMetaBot)`; update CLAUDE.md / memory with the first-hire jobIds.

---

## Self-Review (completed)

- **Spec coverage:** §4 offerings → Tasks 7/9; §5 flows → Tasks 7/9 (sidecar) + 3 (.Api); §6 safety → Tasks 1/3/9; §7 data model → Task 1; §8 files → all tasks; §10 testing → Tasks 1/2/3; §11 smoke → Task 11; §13 registration → Task 11. `purchase_recur` is explicitly out of v1 (spec §2) — no task, intentional.
- **Placeholder scan:** none — every code step has complete code. The one literal to confirm at runtime (`priceType === "fixed"`) is coded with a concrete default + a Task 11 verify note, not left blank.
- **Type consistency:** `PurchaserService` method names (`QuoteAsync`/`PrecheckAsync`/`SettleAsync`), `PrecheckResult.Ok/Reason`, `BudgetCheckResult.Reserved/DayTotalUsd`, apiClient `purchaseQuote/purchasePrecheck/purchaseSettle`, sidecar `PurchaserBuyer.isInnerJob/handleInnerEntry/hireOnBehalf`, and the `PendingJob` `kind` discriminator are used consistently across .Api ↔ sidecar ↔ tests. Endpoint paths match between Program.cs and apiClient.ts (`/v1/buyer/purchase/quote`, `/v1/internal/buyer/purchase/precheck`, `/settle`).
