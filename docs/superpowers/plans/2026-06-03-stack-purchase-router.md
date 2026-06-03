# Stack Purchase Router (intent→stack buy-on-behalf) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two new TheMetaBot offerings — `stack_quote` (price-bound curated plan) and `stack_execute` (validated, all-or-nothing fan-out buy + combined report) — extending ACPPurchaser from a single buy-on-behalf to an intent→stack router for analysis fan-outs.

**Architecture:** Mirror the existing `purchase_quote`/`purchase_execute` split. `stack_quote` runs via the offering's `execute()` and calls a new C# `StackPurchaserService.QuoteAsync` (curate via `StackComposerService` + screen via `IAgentRiskSource` + fixed-price/subject-mappability filter via `OfferingRepository` + persist a price-bound plan in a new `StackQuoteStore`). `stack_execute` is special-cased in `seller.ts` (like `purchase_execute`): C# `PrecheckAsync` validates the quote + atomically reserves the daily cap and returns the resolved steps; the sidecar escrows `fee + totalQuotedDownstream` via `setBudgetWithFundRequest`, runs each step through the existing serialized `PurchaserBuyer.hireOnBehalf` (P61 per-step cap = quoted price), and **all-or-nothing**: all delivered → `submit(combined, transferAmount = totalQuotedDownstream)`; any failed → `reject` + full refund. C# `SettleAsync` writes audit + refunds the cap reservation on reject.

**Tech Stack:** C# .NET 10, ADO.NET + `Microsoft.Data.Sqlite` (no EF), xUnit. Node 22 + TypeScript sidecar (`@virtuals-protocol/acp-node-v2` ^0.0.6). No new dependencies.

**Spec:** `docs/superpowers/specs/2026-06-03-stack-purchase-router-design.md`.

**Deviation from spec (approved 2026-06-03):** v1 is **all-or-nothing**, not partial-delivery. Reason: partial delivery requires a partial fund-request settlement (transfer < fund request + remainder refund) that is unverified on-chain; the single flow's proven model settles the full fund request at `submit`. All-or-nothing keeps `transferAmount == fundRequest` always (no partial settle). Partial delivery is a v1.1 gated on a transfer-mechanics spike.

**Deviation #2 — live-reprice subsumed by all-or-nothing (recorded 2026-06-03 from the final review).** Spec §4.2-step-2 / §5-invariant-1 described re-resolving each step's *live* price at execute and "dropping the inflated step, proceeding with the rest" (+ charging the lower price if a seller re-priced down). Under the approved all-or-nothing model there is no "proceed with the rest", so v1 does NOT run a separate live-reprice pass. Instead the bound *quoted* price is the per-step `maxInnerUsdc` cap, and the buyer engine's existing P61 guard (`purchaserBuyer.ts` `budget.set` handler) refuses any inner job whose live on-chain budget exceeds that cap → that step fails → the whole stack rejects → full refund. This preserves invariant 1 ("never spend more than approved per step") and is money-safe; it differs from the spec only in that an inflated step rejects the whole stack (consistent with all-or-nothing) rather than being dropped. If a seller re-prices *below* the quote, v1 charges the quoted total and keeps the small surplus (same benign behaviour as the single `purchase_execute` flow). Consequence: the spec §8 `price_inflated` C# test is N/A in v1 (the path is exercised on-chain via the §14 smoke). Both are intentional v1 scope; partial delivery + live-reprice-and-charge-lower return together in v1.1.

**Operator note (not a defect):** all-or-nothing means if steps 1..k-1 deliver but step k fails, Metabot has already paid real USDC for the delivered inner hires yet fully refunds the buyer — Metabot eats that float. Bounded by serial-break (only steps before the first failure) + the per-buyer daily cap. Monitor via `acppurchaser_audit` rows with `reason=downstream_failed:<step>`.

---

## File structure

**C# (`ACP_Metabot.Api/`):**
- Create `Services/StackPurchaserService.cs` — quote build, precheck (validate + cap reserve), settle (refund/audit). The only money-safety owner. Mirrors `PurchaserService` (reuses `IAgentRiskSource`, `PurchaserBudgetService`).
- Create `Data/StackQuoteStore.cs` — persist/load/expire the bound plan (`acppurchaser_stack_quotes`).
- Modify `Data/Db.cs` — add the `acppurchaser_stack_quotes` table (and reuse `acppurchaser_audit` for the stack execute audit rows).
- Modify `Program.cs` — DI for `StackQuoteStore` + `StackPurchaserService`; 3 endpoints (`/v1/buyer/stack/quote`, `/v1/internal/buyer/stack/precheck`, `/v1/internal/buyer/stack/settle`).

**C# tests (`ACP_Metabot.Api.Tests/`):**
- Create `StackQuoteStoreTests.cs`, `StackPurchaserServiceTests.cs` (mirror `PurchaserServiceTests.cs` / `PurchaserBudgetServiceTests.cs`).

**Sidecar (`acp-v2/src/`):**
- Create `offerings/stackQuote.ts` (execute() → `client.stackQuote`), `offerings/stackExecute.ts` (validate() only).
- Modify `offerings/registry.ts` (register both), `pricing.ts` (prices), `apiClient.ts` (3 methods + types), `seller.ts` (stack_execute special-case).

---

## Shared types (used across tasks — defined in StackPurchaserService.cs)

```csharp
// A single buyable step in a bound plan.
public record StackPlanStep(
    string TargetAgent, string TargetOffering, string Role,
    decimal QuotedPriceUsdc, string RiskTier, Dictionary<string, object> InnerRequirement);

// Returned by QuoteAsync (the deliverable for stack_quote).
public record StackQuoteResult(
    string QuoteId, string Subject, IReadOnlyList<StackQuoteStep> Steps,
    IReadOnlyList<DroppedCandidate> DroppedCandidates,
    decimal TotalDownstreamUsdc, decimal ExecuteFeeUsdc, decimal TotalEscrowUsdc,
    string Verdict, string ExpiresAt);
public record StackQuoteStep(string TargetAgent, string TargetOffering, string Role, decimal PriceUsdc, string RiskTier, string Verdict);
public record DroppedCandidate(string TargetAgent, string TargetOffering, string Reason);

// Returned by PrecheckAsync (consumed by seller.ts).
public record StackPrecheckResult(bool Ok, string? Reason, IReadOnlyList<StackPlanStep> Steps, decimal TotalDownstreamUsdc);
```

`StackComposerService.ComposeAsync(useCase, budgetUsdc, maxOfferings, marketplace, chainFilter, ct)` returns `ComposedStack(string Rationale, IReadOnlyList<StackEntry> Entries, decimal TotalPriceUsdc)` where `StackEntry(string OfferingName, string AgentName, string AgentAddress, decimal PriceUsdc, string Role)`. `OfferingRepository.ListByAgentAsync(addr)` returns offering rows exposing `.OfferingName`, `.PriceType` (string, "fixed" when fixed-price), `.PriceUsdc`, `.RequirementSchemaJson` (string?).

---

## Task 1: `acppurchaser_stack_quotes` table

**Files:**
- Modify: `ACP_Metabot.Api/Data/Db.cs` (the `InitSchema`/`CREATE TABLE IF NOT EXISTS` block, immediately after the `acppurchaser_audit` table at ~line 454-468)
- Test: `ACP_Metabot.Api.Tests/StackQuoteStoreTests.cs` (created in Task 2 — table existence is exercised there)

- [ ] **Step 1: Add the table DDL**

In `Db.cs`, directly after the `acppurchaser_audit` CREATE TABLE + its index (~line 468), add:

```sql
            -- Stack Purchase Router (v1): a price-bound curated plan persisted by
            -- stack_quote and validated/consumed by stack_execute. steps_json is the
            -- serialized List<StackPlanStep> (agent, offering, role, quoted price,
            -- risk tier, inner requirement). expires_at bounds stale-price execution.
            CREATE TABLE IF NOT EXISTS acppurchaser_stack_quotes (
                quote_id              TEXT PRIMARY KEY,
                buyer_key             TEXT NOT NULL,
                subject               TEXT NOT NULL,
                steps_json            TEXT NOT NULL,
                total_downstream_usd  REAL NOT NULL,
                execute_fee_usd       REAL NOT NULL,
                expires_at            TEXT NOT NULL,
                created_at            TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_acppurchaser_stack_quotes_buyer
                ON acppurchaser_stack_quotes(buyer_key, created_at DESC);
```

- [ ] **Step 2: Build to confirm the DDL string compiles**

Run: `cd ACP_Metabot/ACP_Metabot && dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj`
Expected: build succeeds, 0 errors. (SQL is a C# string; a quote/`--`-comment escaping mistake surfaces as CS1xxx — see `feedback_csharp_verbatim_string_sql_comment_quote`. Note the DDL is inside the existing `@"..."` block; do not introduce unescaped `"`.)

- [ ] **Step 3: Commit**

```bash
git add ACP_Metabot.Api/Data/Db.cs
git commit -m "feat(stack): add acppurchaser_stack_quotes table"
```

---

## Task 2: `StackQuoteStore` (persist / load / expire)

**Files:**
- Create: `ACP_Metabot.Api/Data/StackQuoteStore.cs`
- Test: `ACP_Metabot.Api.Tests/StackQuoteStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Create `ACP_Metabot.Api.Tests/StackQuoteStoreTests.cs`:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ACP_Metabot.Api.Tests;

public class StackQuoteStoreTests
{
    private static Db NewDb()
    {
        // Db reads ConnectionStrings:Sqlite; an in-memory shared db gives an isolated schema per test class.
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sqlite"] = $"Data Source=file:stackquote-{System.Guid.NewGuid():N}?mode=memory&cache=shared"
        }).Build();
        var db = new Db(cfg);
        db.InitSchema();
        return db;
    }

    private static StackPlanStep Step(string agent, decimal price) =>
        new(agent, "risk_snapshot", "risk", price, "low", new Dictionary<string, object> { ["agentAddress"] = "0xabc" });

    [Fact]
    public async Task Save_then_Load_roundtrips_the_plan()
    {
        var store = new StackQuoteStore(NewDb());
        var steps = new List<StackPlanStep> { Step("0x1111111111111111111111111111111111111111", 0.05m) };
        await store.SaveAsync("q1", "0xbuyer", "0xsubject", steps, 0.05m, 0.25m,
            DateTime.UtcNow.AddMinutes(15), default);

        var loaded = await store.LoadAsync("q1", default);

        Assert.NotNull(loaded);
        Assert.Equal("0xbuyer", loaded!.BuyerKey);
        Assert.Equal("0xsubject", loaded.Subject);
        Assert.Single(loaded.Steps);
        Assert.Equal(0.05m, loaded.Steps[0].QuotedPriceUsdc);
        Assert.Equal("risk_snapshot", loaded.Steps[0].TargetOffering);
    }

    [Fact]
    public async Task LoadActive_returns_null_when_expired()
    {
        var store = new StackQuoteStore(NewDb());
        await store.SaveAsync("q2", "0xbuyer", "0xsubject",
            new List<StackPlanStep> { Step("0x2222222222222222222222222222222222222222", 0.05m) },
            0.05m, 0.25m, DateTime.UtcNow.AddMinutes(-1), default);

        var loaded = await store.LoadActiveAsync("q2", DateTime.UtcNow, default);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Load_returns_null_for_unknown_quote()
        => Assert.Null(await new StackQuoteStore(NewDb()).LoadAsync("nope", default));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj --filter "FullyQualifiedName~StackQuoteStoreTests"`
Expected: FAIL to compile — `StackQuoteStore` does not exist.
(If `Db` has no public `InitSchema()`/ctor taking `IConfiguration`, match the real `Db` ctor — check `Data/Db.cs` and adjust `NewDb()` to however other tests like `DbTests.cs` construct `Db`.)

- [ ] **Step 3: Write the implementation**

Create `ACP_Metabot.Api/Data/StackQuoteStore.cs`:

```csharp
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj --filter "FullyQualifiedName~StackQuoteStoreTests"`
Expected: PASS (3 tests). If `StackPlanStep` is undefined, temporarily declare it in `StackPurchaserService.cs` (Task 3) or a small shared file; it is finalized in Task 3.

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Data/StackQuoteStore.cs ACP_Metabot.Api.Tests/StackQuoteStoreTests.cs
git commit -m "feat(stack): StackQuoteStore persist/load/expire with tests"
```

---

## Task 3: `StackPurchaserService.QuoteAsync` (curate → screen → filter → bind)

**Files:**
- Create: `ACP_Metabot.Api/Services/StackPurchaserService.cs` (declares the shared types above + the service)
- Test: `ACP_Metabot.Api.Tests/StackPurchaserServiceTests.cs`

QuoteAsync algorithm:
1. `ComposeAsync(intent, maxFundsUsdc, maxSteps, marketplace:null, chainFilter:null, ct)` → entries.
2. For each entry (cap at `maxSteps`): look up the offering via `OfferingRepository.ListByAgentAsync(agentAddress)`, matching `OfferingName`.
   - Not fixed-price (`PriceType.ToLowerInvariant() != "fixed"`) → drop `not_fixed_price`.
   - Risk tier (`IAgentRiskSource.RiskTierAsync(agentAddress, 8453, ct)`); `critical` → drop `risk_critical`.
   - Subject-mappability: parse `RequirementSchemaJson`; find the single string property whose name (lowercased) contains one of `address|wallet|agent|spender|token|account|contract`. If exactly one → that is the inner-requirement field; build `InnerRequirement = { field: subject }`. If zero or more than one → drop `subject_unmappable`.
   - `high` risk → keep with per-step verdict `CAUTION`; else `PROCEED`.
3. `totalDownstream = Σ kept QuotedPrice`. If `totalDownstream > maxFundsUsdc` → return verdict `BLOCK`, reason `over_max_funds`, no persistence. If zero kept steps → verdict `BLOCK`, reason `no_buyable_steps`, no persistence.
4. Else persist via `StackQuoteStore.SaveAsync` (quoteId = `"stk_" + N` — generated by the caller via an injected `Func<string>` id factory so tests are deterministic; default uses `Guid`), `expiresAt = nowUtc + 15min`. Return the plan.

- [ ] **Step 1: Write the failing test**

Create `ACP_Metabot.Api.Tests/StackPurchaserServiceTests.cs`:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ACP_Metabot.Api.Tests;

public class StackPurchaserServiceTests
{
    // --- Fakes (mirror PurchaserServiceTests seams) ---
    private sealed class FakeRisk : IAgentRiskSource
    {
        public Dictionary<string, string> Tiers = new(StringComparer.OrdinalIgnoreCase);
        public Task<string> RiskTierAsync(string a, int c, CancellationToken ct)
            => Task.FromResult(Tiers.TryGetValue(a, out var t) ? t : "low");
    }
    private sealed class FakeComposer : IStackComposerSource
    {
        public List<StackEntry> Entries = new();
        public Task<IReadOnlyList<StackEntry>> CurateAsync(string intent, decimal budget, int max, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StackEntry>>(Entries);
    }
    private sealed class FakeOfferings : IStackOfferingSource
    {
        // key: (agentAddress,offeringName) -> (priceType, requirementSchemaJson)
        public Dictionary<(string, string), (string PriceType, string? Schema)> Map = new();
        public Task<(string PriceType, string? RequirementSchemaJson)?> ResolveAsync(string agent, string offering, CancellationToken ct)
            => Task.FromResult(Map.TryGetValue((agent.ToLowerInvariant(), offering), out var v) ? ((string, string?)?)v : null);
    }

    private static Db NewDb()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sqlite"] = $"Data Source=file:stacksvc-{System.Guid.NewGuid():N}?mode=memory&cache=shared"
        }).Build();
        var db = new Db(cfg); db.InitSchema(); return db;
    }

    private static (StackPurchaserService svc, FakeComposer comp, FakeRisk risk, FakeOfferings offs, Db db) Build()
    {
        var db = NewDb();
        var cfg = new ConfigurationBuilder().Build();
        var budget = new PurchaserBudgetService(db, cfg, NullLogger<PurchaserBudgetService>.Instance);
        var comp = new FakeComposer(); var risk = new FakeRisk(); var offs = new FakeOfferings();
        int n = 0;
        var svc = new StackPurchaserService(db, new StackQuoteStore(db), budget, risk, comp, offs,
            idFactory: () => $"stk_{++n}", NullLogger<StackPurchaserService>.Instance);
        return (svc, comp, risk, offs, db);
    }

    private const string A1 = "0x1111111111111111111111111111111111111111";
    private const string A2 = "0x2222222222222222222222222222222222222222";
    private const string SUBJECT = "0x9999999999999999999999999999999999999999";
    private const string ADDR_SCHEMA = "{\"type\":\"object\",\"properties\":{\"agentAddress\":{\"type\":\"string\"}}}";

    [Fact]
    public async Task Quote_keeps_fixed_price_low_risk_mappable_step()
    {
        var (svc, comp, _, offs, _) = Build();
        comp.Entries.Add(new StackEntry("risk_snapshot", "MetaBot", A1, 0.05m, "risk"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);

        var q = await svc.QuoteAsync(SUBJECT, "screen this wallet", 1.0m, 5, default);

        Assert.Equal("PROCEED", q.Verdict);
        Assert.Single(q.Steps);
        Assert.Equal(0.05m, q.TotalDownstreamUsdc);
        Assert.Equal(0.25m, q.ExecuteFeeUsdc);
        Assert.Equal(0.30m, q.TotalEscrowUsdc);
        Assert.StartsWith("stk_", q.QuoteId);
    }

    [Fact]
    public async Task Quote_drops_non_fixed_price_step()
    {
        var (svc, comp, _, offs, _) = Build();
        comp.Entries.Add(new StackEntry("oracle_watch", "OracleBot", A1, 0.50m, "watch"));
        offs.Map[(A1, "oracle_watch")] = ("subscription", ADDR_SCHEMA);

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        Assert.Equal("BLOCK", q.Verdict); // zero kept
        Assert.Contains(q.DroppedCandidates, d => d.Reason == "not_fixed_price");
    }

    [Fact]
    public async Task Quote_drops_critical_risk_step()
    {
        var (svc, comp, risk, offs, _) = Build();
        comp.Entries.Add(new StackEntry("scan", "Bad", A1, 0.05m, "x"));
        offs.Map[(A1, "scan")] = ("fixed", ADDR_SCHEMA);
        risk.Tiers[A1] = "critical";

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        Assert.Contains(q.DroppedCandidates, d => d.Reason == "risk_critical");
    }

    [Fact]
    public async Task Quote_drops_subject_unmappable_step()
    {
        var (svc, comp, _, offs, _) = Build();
        comp.Entries.Add(new StackEntry("digest", "X", A1, 0.05m, "x"));
        offs.Map[(A1, "digest")] = ("fixed", "{\"type\":\"object\",\"properties\":{\"days\":{\"type\":\"number\"}}}");

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        Assert.Contains(q.DroppedCandidates, d => d.Reason == "subject_unmappable");
    }

    [Fact]
    public async Task Quote_blocks_when_total_exceeds_maxFunds()
    {
        var (svc, comp, _, offs, _) = Build();
        comp.Entries.Add(new StackEntry("a", "X", A1, 0.50m, "r"));
        comp.Entries.Add(new StackEntry("b", "Y", A2, 0.60m, "r"));
        offs.Map[(A1, "a")] = ("fixed", ADDR_SCHEMA);
        offs.Map[(A2, "b")] = ("fixed", ADDR_SCHEMA);

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        Assert.Equal("BLOCK", q.Verdict);
        Assert.Equal("over_max_funds", q.Reasons_FirstOrDefault());
    }

    [Fact]
    public async Task Quote_persists_a_loadable_plan()
    {
        var (svc, comp, _, offs, db) = Build();
        comp.Entries.Add(new StackEntry("risk_snapshot", "MetaBot", A1, 0.05m, "risk"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);

        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);
        var stored = await new StackQuoteStore(db).LoadAsync(q.QuoteId, default);

        Assert.NotNull(stored);
        Assert.Single(stored!.Steps);
        Assert.Equal("agentAddress", System.Linq.Enumerable.First(stored.Steps[0].InnerRequirement.Keys));
        Assert.Equal(SUBJECT.ToLowerInvariant(), stored.Steps[0].InnerRequirement["agentAddress"]?.ToString());
    }
}
```

> Note: `Reasons_FirstOrDefault()` is a readability helper the test author may inline as `q.Verdict == "BLOCK"` + a `Reasons` list check. Define `StackQuoteResult.Reasons` (a `IReadOnlyList<string>`) and assert `Assert.Contains("over_max_funds", q.Reasons)` instead if a helper feels heavy. Keep one consistent shape with Task 3's implementation.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj --filter "FullyQualifiedName~StackPurchaserServiceTests"`
Expected: FAIL to compile — `StackPurchaserService`, `IStackComposerSource`, `IStackOfferingSource` do not exist.

- [ ] **Step 3: Write the implementation**

Create `ACP_Metabot.Api/Services/StackPurchaserService.cs`. It defines the seams (so the service is testable without the LLM or the repo), the shared records, and `QuoteAsync`. (`PrecheckAsync`/`SettleAsync` are added in Tasks 4-5.)

```csharp
using System.Text.Json;
using ACP_Metabot.Api.Data;

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
        => (await _c.ComposeAsync(intent, budgetUsdc, maxSteps, null, null, ct)).Entries;
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
        { "address", "wallet", "agent", "spender", "token", "account", "contract" };

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
            kept.Add(new StackPlanStep(c.AgentAddress.ToLowerInvariant(), c.OfferingName, c.Role,
                c.PriceUsdc, tier, new Dictionary<string, object> { [field] = subj }));
            keptView.Add(new StackQuoteStep(c.AgentAddress.ToLowerInvariant(), c.OfferingName, c.Role, c.PriceUsdc, tier, verdict));
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
        await _store.SaveAsync(quoteId, subj /*buyerKey set at precheck? see note*/, subj, kept, total, ExecuteFeeUsdc, expires, ct);
        reasons.Add("ok");
        return new StackQuoteResult(quoteId, subj, keptView, dropped, total, ExecuteFeeUsdc,
            decimal.Round(total + ExecuteFeeUsdc, 4), kept.Any(k => k.RiskTier == "high") ? "CAUTION" : "PROCEED",
            reasons, expires.ToString("O"));
    }

    // Finds the single string property whose name hints at an address. Conservative:
    // returns null on zero or multiple matches (money-safety — never guess).
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
                if (match is not null) return null; // ambiguous → drop
                match = p.Name;
            }
            return match;
        }
        catch { return null; }
    }
}
```

> **buyer_key note:** the quote is created before the on-chain hire, so the caller (`stack_quote`'s C# endpoint) does not yet know the buyer's address. Persist `buyer_key = subject` is wrong. Two clean options — pick **(b)**:
> (a) require the sidecar to pass the buyer's `clientAddress` into `/v1/buyer/stack/quote` and thread it to `SaveAsync`; OR
> (b) **store `buyer_key = ""` at quote time and bind it at precheck** (the first `stack_execute` that references the quote claims it; `PrecheckAsync` sets `buyer_key` if empty, else requires a match). This avoids a quote-time identity and matches how `purchase_quote` carries no buyer identity. Implement (b): add a `buyerKey` param to `QuoteAsync` defaulting to `""`, store it; `PrecheckAsync` (Task 4) enforces the bind. Update the test `Quote_persists_a_loadable_plan` to assert `stored.BuyerKey == ""`.

Apply the (b) correction: change the `SaveAsync` call to pass `buyerKey: ""` (add a `string buyerKey = ""` parameter to `QuoteAsync`).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj --filter "FullyQualifiedName~StackPurchaserServiceTests"`
Expected: PASS. Adjust the `over_max_funds` assertion to `Assert.Contains("over_max_funds", q.Reasons)` to match the `Reasons` list.

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Services/StackPurchaserService.cs ACP_Metabot.Api.Tests/StackPurchaserServiceTests.cs
git commit -m "feat(stack): StackPurchaserService.QuoteAsync (curate/screen/filter/bind) + tests"
```

---

## Task 4: `StackPurchaserService.PrecheckAsync` (validate quote + bind buyer + cap reserve)

**Files:**
- Modify: `ACP_Metabot.Api/Services/StackPurchaserService.cs`
- Modify: `ACP_Metabot.Api.Tests/StackPurchaserServiceTests.cs`

Algorithm: load the quote (`LoadActiveAsync`, now). Branch in order:
- null → `(false, "quote_expired_or_not_found", [], 0)`.
- subject mismatch → `(false, "subject_mismatch", …)`.
- `buyer_key` non-empty and != caller → `(false, "buyer_mismatch", …)`. (If empty, bind it: re-`SaveAsync` with the caller as buyer, same other fields/expiry.)
- `TryReserveAsync(buyerKey, total)` fails → `(false, "daily_cap_exceeded", …)`.
- else write a `PRECHECK` audit row (reuse `acppurchaser_audit` with `target_offering = "stack_execute"`, `downstream_usd = total`) and return `(true, null, steps, total)`.

- [ ] **Step 1: Write the failing tests** (append to `StackPurchaserServiceTests.cs`)

```csharp
    [Fact]
    public async Task Precheck_rejects_expired_quote()
    {
        var (svc, _, _, _, _) = Build();
        var r = await svc.PrecheckAsync("outer1", "0xbuyer", "missing", SUBJECT, default);
        Assert.False(r.Ok);
        Assert.Equal("quote_expired_or_not_found", r.Reason);
    }

    [Fact]
    public async Task Precheck_rejects_subject_mismatch()
    {
        var (svc, comp, _, offs, _) = Build();
        comp.Entries.Add(new StackEntry("risk_snapshot", "M", A1, 0.05m, "r"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);
        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        var r = await svc.PrecheckAsync("outer1", "0xbuyer", q.QuoteId, "0xdifferent", default);
        Assert.False(r.Ok);
        Assert.Equal("subject_mismatch", r.Reason);
    }

    [Fact]
    public async Task Precheck_binds_buyer_and_reserves_then_blocks_second_buyer()
    {
        var (svc, comp, _, offs, _) = Build();
        comp.Entries.Add(new StackEntry("risk_snapshot", "M", A1, 0.05m, "r"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);
        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);

        var first = await svc.PrecheckAsync("outer1", "0xbuyerA", q.QuoteId, SUBJECT, default);
        Assert.True(first.Ok);
        Assert.Single(first.Steps);

        var second = await svc.PrecheckAsync("outer2", "0xbuyerB", q.QuoteId, SUBJECT, default);
        Assert.False(second.Ok);
        Assert.Equal("buyer_mismatch", second.Reason);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test ... --filter "FullyQualifiedName~StackPurchaserServiceTests"`
Expected: FAIL to compile — `PrecheckAsync` not defined.

- [ ] **Step 3: Implement `PrecheckAsync`** (add to `StackPurchaserService`)

```csharp
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
```

Copy `WriteAuditAsync` / `UpdateAuditAsync` verbatim from `PurchaserService.cs:99-133` into `StackPurchaserService` (same `acppurchaser_audit` table + columns; `service_fee_usd` = `ExecuteFeeUsdc`).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test ... --filter "FullyQualifiedName~StackPurchaserServiceTests"`
Expected: PASS (all Task 3 + Task 4 tests).

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Services/StackPurchaserService.cs ACP_Metabot.Api.Tests/StackPurchaserServiceTests.cs
git commit -m "feat(stack): PrecheckAsync (validate quote, bind buyer, reserve cap) + tests"
```

---

## Task 5: `StackPurchaserService.SettleAsync` (refund-on-reject + audit close)

**Files:** Modify `StackPurchaserService.cs` + tests.

Semantics (all-or-nothing): on `REJECTED`, refund the full reservation (`RecordActualSpendAsync(buyer, -total)`); on `DELIVERED`, no refund. Always close the audit row.

- [ ] **Step 1: Write the failing test** (append)

```csharp
    [Fact]
    public async Task Settle_rejected_refunds_full_reservation()
    {
        var (svc, comp, _, offs, db) = Build();
        comp.Entries.Add(new StackEntry("risk_snapshot", "M", A1, 0.30m, "r"));
        offs.Map[(A1, "risk_snapshot")] = ("fixed", ADDR_SCHEMA);
        var q = await svc.QuoteAsync(SUBJECT, "x", 1.0m, 5, default);
        var pre = await svc.PrecheckAsync("outerX", "0xbuyer", q.QuoteId, SUBJECT, default);
        Assert.True(pre.Ok);

        var budget = new PurchaserBudgetService(db, new ConfigurationBuilder().Build(), NullLogger<PurchaserBudgetService>.Instance);
        Assert.Equal(0.30m, await budget.GetTodaysSpendAsync("0xbuyer", default));

        await svc.SettleAsync("outerX", "0xbuyer", "REJECTED", null, "downstream_failed", 0.30m, default);
        Assert.Equal(0m, await budget.GetTodaysSpendAsync("0xbuyer", default));
    }
```

- [ ] **Step 2: Run to verify failure** — `SettleAsync` undefined → compile FAIL.

- [ ] **Step 3: Implement** (copy `PurchaserService.SettleAsync:92-97` shape):

```csharp
    public async Task SettleAsync(string outerJobId, string buyerKey, string state,
        string? innerJobIds, string? reason, decimal totalDownstreamUsd, CancellationToken ct)
    {
        if (state == "REJECTED")
            await _budget.RecordActualSpendAsync(buyerKey.Trim().ToLowerInvariant(), -totalDownstreamUsd, ct);
        await UpdateAuditAsync(outerJobId, state, innerJobIds, reason, ct);
    }
```

- [ ] **Step 4: Run to verify pass** — PASS.
- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Services/StackPurchaserService.cs ACP_Metabot.Api.Tests/StackPurchaserServiceTests.cs
git commit -m "feat(stack): SettleAsync refund-on-reject + audit close + tests"
```

---

## Task 6: DI + 3 HTTP endpoints

**Files:** Modify `ACP_Metabot.Api/Program.cs`.

- [ ] **Step 1: Register DI** — after the `PurchaserBudgetService` registration (~line 188) add:

```csharp
builder.Services.AddSingleton<StackComposerService>(); // if not already registered — check; StackComposerService is used by composeStack/V17PaidOfferingsService and is likely already in DI. If already present, skip this line.
builder.Services.AddSingleton<StackQuoteStore>();
builder.Services.AddSingleton<IStackComposerSource, StackComposerAdapter>();
builder.Services.AddSingleton<IStackOfferingSource, StackOfferingAdapter>();
builder.Services.AddSingleton<StackPurchaserService>(sp => new StackPurchaserService(
    sp.GetRequiredService<Db>(), sp.GetRequiredService<StackQuoteStore>(),
    sp.GetRequiredService<PurchaserBudgetService>(), sp.GetRequiredService<IAgentRiskSource>(),
    sp.GetRequiredService<IStackComposerSource>(), sp.GetRequiredService<IStackOfferingSource>(),
    idFactory: () => $"stk_{Guid.NewGuid():N}",
    sp.GetRequiredService<ILogger<StackPurchaserService>>()));
```

(Verify whether `StackComposerService` is already in DI — grep `Program.cs` for `StackComposerService`. Only add the line if absent.)

- [ ] **Step 2: Add the public quote endpoint** — near the existing `/v1/buyer/purchase/quote` (~line 1168):

```csharp
app.MapPost("/v1/buyer/stack/quote", async (StackQuoteRequest req, StackPurchaserService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Subject) || !System.Text.RegularExpressions.Regex.IsMatch(req.Subject.Trim().ToLowerInvariant(), "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_subject", message = "subject must be a 0x EVM address" });
    if (string.IsNullOrWhiteSpace(req.Intent))
        return Results.BadRequest(new { error = "invalid_intent" });
    var result = await svc.QuoteAsync(req.Subject, req.Intent, req.MaxFundsUsdc, req.MaxSteps ?? 5, ct);
    return Results.Ok(result);
});
```

- [ ] **Step 3: Add the internal precheck + settle endpoints** — near `/v1/internal/buyer/purchase/precheck` (~line 1616):

```csharp
app.MapPost("/v1/internal/buyer/stack/precheck", async (StackPrecheckRequest req, StackPurchaserService svc, CancellationToken ct) =>
    Results.Ok(await svc.PrecheckAsync(req.OuterJobId, req.BuyerKey, req.QuoteId, req.Subject, ct)));

app.MapPost("/v1/internal/buyer/stack/settle", async (StackSettleRequest req, StackPurchaserService svc, CancellationToken ct) =>
{
    await svc.SettleAsync(req.OuterJobId, req.BuyerKey, req.State, req.InnerJobIds, req.Reason, req.TotalDownstreamUsd, ct);
    return Results.Ok(new { ok = true });
});
```

- [ ] **Step 4: Add the request DTOs** — alongside the other request records (bind as nullable-tolerant; mirror existing DTO style):

```csharp
public record StackQuoteRequest(string Subject, string Intent, decimal MaxFundsUsdc, int? MaxSteps);
public record StackPrecheckRequest(string OuterJobId, string BuyerKey, string QuoteId, string Subject);
public record StackSettleRequest(string OuterJobId, string BuyerKey, string State, string? InnerJobIds, string? Reason, decimal TotalDownstreamUsd);
```

(The `/v1/internal/*` prefix is already exempted from the per-IP limiter and requires `X-API-Key` via the existing middleware — no middleware change needed; confirm by reading the X-API-Key middleware comment at `Program.cs:598-617`.)

- [ ] **Step 5: Build + run the full C# suite**

Run: `dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj` then `dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj`
Expected: build clean; all tests pass (293 prior + the new StackQuoteStore/StackPurchaser tests).

- [ ] **Step 6: Commit**

```bash
git add ACP_Metabot.Api/Program.cs
git commit -m "feat(stack): DI + /v1/buyer/stack/quote + /v1/internal/buyer/stack/{precheck,settle}"
```

---

## Task 7: Sidecar pricing

**Files:** Modify `acp-v2/src/pricing.ts`.

- [ ] **Step 1:** After the `purchase_execute: 0.10,` entry (~line 56) add:

```typescript
  stack_quote: 0.05,
  stack_execute: 0.25,
```

- [ ] **Step 2: Commit**

```bash
git add acp-v2/src/pricing.ts
git commit -m "feat(stack): pricing stack_quote 0.05 / stack_execute 0.25"
```

---

## Task 8: `stack_quote` offering (sidecar)

**Files:** Create `acp-v2/src/offerings/stackQuote.ts`. Mirrors `purchaseQuote.ts` (runs via `execute()`).

- [ ] **Step 1: Write the offering**

```typescript
import type { Offering } from "./types.js";

export const stackQuote: Offering = {
  name: "stack_quote",
  description:
    "Given a subject (a 0x address) and an intent, TheMetaBot curates a stack of complementary fixed-price " +
    "analysis offerings from across the marketplace, screens each (scam-risk + fixed-price), and returns a " +
    "price-bound plan (quoteId, per-step agent/offering/price/risk, total escrow) for stack_execute. No money moves.",
  requirementSchema: {
    type: "object",
    properties: {
      subject: { type: "string", description: "The 0x EVM address the stack will analyse (wallet, agent, or token contract). Threaded into each step." },
      intent: { type: "string", description: "Natural-language description of the analysis you want (e.g. 'full safety + reputation screen of this wallet')." },
      maxFundsUsdc: { type: "number", description: "Ceiling on total downstream spend across the stack; steps over this are blocked." },
      maxSteps: { type: "number", description: "Optional cap on the number of steps in the stack (1-5, default 5)." },
    },
    required: ["subject", "intent", "maxFundsUsdc"],
  },
  requirementExample: {
    subject: "0x9999999999999999999999999999999999999999",
    intent: "full safety + reputation screen of this wallet",
    maxFundsUsdc: 1.0, maxSteps: 4,
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["quoteId", "subject", "steps", "totalDownstreamUsdc", "executeFeeUsdc", "totalEscrowUsdc", "verdict"],
    properties: {
      quoteId: { type: "string", description: "Opaque id to pass to stack_execute. Empty when verdict is BLOCK." },
      subject: { type: "string", description: "The resolved (lowercased) subject address." },
      steps: {
        type: "array",
        description: "The curated, screened, buyable steps in execution order.",
        items: {
          type: "object",
          required: ["targetAgent", "targetOffering", "role", "priceUsdc", "riskTier", "verdict"],
          properties: {
            targetAgent: { type: "string", description: "Seller agent wallet for this step." },
            targetOffering: { type: "string", description: "Offering name purchased for this step." },
            role: { type: "string", description: "Role this step plays in the stack." },
            priceUsdc: { type: "number", description: "Quoted fixed price for this step." },
            riskTier: { type: "string", description: "Scam-risk tier of the seller: low|medium|high." },
            verdict: { type: "string", description: "PROCEED or CAUTION (high-risk) for this step." },
          },
        },
      },
      droppedCandidates: {
        type: "array",
        description: "Candidates excluded from the plan and why.",
        items: {
          type: "object",
          required: ["targetAgent", "targetOffering", "reason"],
          properties: {
            targetAgent: { type: "string", description: "Excluded seller agent wallet." },
            targetOffering: { type: "string", description: "Excluded offering name." },
            reason: { type: "string", description: "Why excluded: not_fixed_price|risk_critical|subject_unmappable|not_found." },
          },
        },
      },
      totalDownstreamUsdc: { type: "number", description: "Sum of the kept steps' prices." },
      executeFeeUsdc: { type: "number", description: "Flat stack_execute fee (0.25)." },
      totalEscrowUsdc: { type: "number", description: "What stack_execute escrows = executeFee + totalDownstream." },
      verdict: { type: "string", description: "PROCEED|CAUTION|BLOCK for the whole stack." },
      reasons: { type: "array", items: { type: "string", description: "Reason contributing to the stack verdict." }, description: "Reasons behind the verdict." },
      expiresAt: { type: "string", description: "ISO-8601 expiry; the quote is unusable after this." },
    },
  },
  deliverableExample: {
    quoteId: "stk_ab12", subject: "0x9999999999999999999999999999999999999999",
    steps: [{ targetAgent: "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c", targetOffering: "risk_snapshot", role: "risk", priceUsdc: 0.30, riskTier: "low", verdict: "PROCEED" }],
    droppedCandidates: [], totalDownstreamUsdc: 0.30, executeFeeUsdc: 0.25, totalEscrowUsdc: 0.55,
    verdict: "PROCEED", reasons: ["ok"], expiresAt: "2026-06-03T13:00:00.000Z",
  },
  validate(req) {
    if (typeof req.subject !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.subject))
      return { valid: false, reason: "subject must be a 0x EVM address" };
    if (typeof req.intent !== "string" || req.intent.trim().length === 0)
      return { valid: false, reason: "intent required" };
    if (typeof req.maxFundsUsdc !== "number" || !(req.maxFundsUsdc > 0))
      return { valid: false, reason: "maxFundsUsdc must be a positive number" };
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.stackQuote({
      subject: req.subject as string,
      intent: req.intent as string,
      maxFundsUsdc: Number(req.maxFundsUsdc),
      maxSteps: req.maxSteps === undefined ? undefined : Number(req.maxSteps),
    });
  },
};
```

- [ ] **Step 2: Commit**

```bash
git add acp-v2/src/offerings/stackQuote.ts
git commit -m "feat(stack): stack_quote offering (sidecar)"
```

---

## Task 9: `stack_execute` offering (sidecar, validate-only)

**Files:** Create `acp-v2/src/offerings/stackExecute.ts`. Mirrors `purchaseExecute.ts` — `execute()` throws; handled in `seller.ts`.

- [ ] **Step 1: Write the offering**

```typescript
import type { Offering } from "./types.js";

// stack_execute's execute() is NEVER called via the router — seller.ts special-cases
// it (Require-Funds + N serialized inner hires, all-or-nothing). This object exists
// for registration, schema, and validate() only.
export const stackExecute: Offering = {
  name: "stack_execute",
  description:
    "Executes a stack_quote plan: hires each curated offering on your behalf over the subject and returns one " +
    "combined report. You escrow a $0.25 fee + the quoted total downstream (Require-Funds). ALL-OR-NOTHING: if " +
    "any step fails the whole job is rejected and you are fully refunded. Pass the quoteId from stack_quote.",
  requirementSchema: {
    type: "object",
    properties: {
      quoteId: { type: "string", description: "The quoteId returned by stack_quote." },
      subject: { type: "string", description: "The same 0x subject address used in stack_quote (must match the quote)." },
      maxFundsUsdc: { type: "number", description: "Your ceiling on total downstream spend; must be >= the quote's totalDownstreamUsdc." },
    },
    required: ["quoteId", "subject", "maxFundsUsdc"],
  },
  requirementExample: {
    quoteId: "stk_ab12", subject: "0x9999999999999999999999999999999999999999", maxFundsUsdc: 1.0,
  },
  slaMinutes: 30,
  deliverableSchema: {
    type: "object",
    required: ["status", "subject", "steps"],
    properties: {
      status: { type: "string", description: "DELIVERED (all steps delivered) | REJECTED (any step failed; full refund)." },
      subject: { type: "string", description: "The subject address the stack analysed." },
      steps: {
        type: "array",
        description: "Per-step result, in execution order.",
        items: {
          type: "object",
          required: ["targetAgent", "targetOffering", "role", "status"],
          properties: {
            targetAgent: { type: "string", description: "Seller agent hired for this step." },
            targetOffering: { type: "string", description: "Offering purchased for this step." },
            role: { type: "string", description: "Role this step played." },
            status: { type: "string", description: "delivered | failed." },
            innerJobId: { type: "string", description: "On-chain job id of this inner hire, or null." },
            deliverable: { type: "object", description: "Parsed deliverable from this step (present when delivered).", properties: {} },
            error: { type: "string", description: "Failure reason (present when failed)." },
          },
        },
      },
      downstreamChargedUsdc: { type: "number", description: "Total downstream charged (0 on REJECTED)." },
      executeFeeUsdc: { type: "number", description: "Stack execute fee (0.25; refunded on REJECTED)." },
      reason: { type: "string", description: "On REJECTED, why (e.g. quote_expired_or_not_found, daily_cap_exceeded, downstream_failed:<step>)." },
    },
  },
  deliverableExample: {
    status: "DELIVERED", subject: "0x9999999999999999999999999999999999999999",
    steps: [{ targetAgent: "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c", targetOffering: "risk_snapshot", role: "risk", status: "delivered", innerJobId: "7801", deliverable: { score: 79 }, error: "" }],
    downstreamChargedUsdc: 0.30, executeFeeUsdc: 0.25, reason: "",
  },
  validate(req) {
    if (typeof req.quoteId !== "string" || req.quoteId.length === 0)
      return { valid: false, reason: "quoteId required" };
    if (typeof req.subject !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.subject))
      return { valid: false, reason: "subject must be a 0x EVM address" };
    if (typeof req.maxFundsUsdc !== "number" || !(req.maxFundsUsdc > 0))
      return { valid: false, reason: "maxFundsUsdc must be a positive number" };
    return { valid: true };
  },
  async execute() {
    throw new Error("stack_execute is handled in seller.ts, not via the router");
  },
};
```

- [ ] **Step 2: Commit**

```bash
git add acp-v2/src/offerings/stackExecute.ts
git commit -m "feat(stack): stack_execute offering schema (sidecar)"
```

---

## Task 10: Register both offerings

**Files:** Modify `acp-v2/src/offerings/registry.ts`.

- [ ] **Step 1:** Import + add to the registry array (mirror how `purchaseQuote`/`purchaseExecute` are imported/registered ~lines 5/55):

```typescript
import { stackQuote } from "./stackQuote.js";
import { stackExecute } from "./stackExecute.js";
// ...in the registry list:
  stackQuote,
  stackExecute,
```

- [ ] **Step 2: Commit**

```bash
git add acp-v2/src/offerings/registry.ts
git commit -m "feat(stack): register stack_quote + stack_execute"
```

---

## Task 11: api-client methods

**Files:** Modify `acp-v2/src/apiClient.ts` — add 3 methods + their interface signatures (mirror `purchaseQuote`/`purchasePrecheck`/`purchaseSettle` at ~lines 444-450).

- [ ] **Step 1:** Add to the `ApiClient` interface:

```typescript
  stackQuote(req: { subject: string; intent: string; maxFundsUsdc: number; maxSteps?: number }): Promise<unknown>;
  stackPrecheck(req: { outerJobId: string; buyerKey: string; quoteId: string; subject: string }): Promise<{
    ok: boolean; reason?: string;
    steps: Array<{ targetAgent: string; targetOffering: string; role: string; quotedPriceUsdc: number; riskTier: string; innerRequirement: Record<string, unknown> }>;
    totalDownstreamUsdc: number;
  }>;
  stackSettle(req: { outerJobId: string; buyerKey: string; state: string; innerJobIds?: string | null; reason?: string | null; totalDownstreamUsd: number }): Promise<unknown>;
```

- [ ] **Step 2:** Add to the returned object (next to `purchaseSettle`):

```typescript
    stackQuote: (req) =>
      request<unknown>("/v1/buyer/stack/quote", { method: "POST", body: JSON.stringify(req) }),
    stackPrecheck: (req) =>
      request("/v1/internal/buyer/stack/precheck", { method: "POST", body: JSON.stringify(req) }),
    stackSettle: (req) =>
      request<unknown>("/v1/internal/buyer/stack/settle", { method: "POST", body: JSON.stringify(req) }),
```

> The C# `StackPrecheckResult.Steps` serializes `StackPlanStep` as `{ targetAgent, targetOffering, role, quotedPriceUsdc, riskTier, innerRequirement }` (System.Text.Json camelCases by default in this project — verify against the existing `purchasePrecheck` JSON to match casing). Align the TS interface field names exactly to avoid the zero-fill DTO-drift trap (`feedback_cross_bot_dto_drift`).

- [ ] **Step 3: Commit**

```bash
git add acp-v2/src/apiClient.ts
git commit -m "feat(stack): apiClient stackQuote/stackPrecheck/stackSettle"
```

---

## Task 12: `seller.ts` — stack_execute orchestration (all-or-nothing)

**Files:** Modify `acp-v2/src/seller.ts`.

- [ ] **Step 1: Extend the `PendingJob` union** (after the `execute` variant, ~line 26):

```typescript
  | {
      kind: "stack";
      offeringName: "stack_execute";
      steps: Array<{ targetAgent: string; targetOffering: string; role: string; quotedPriceUsdc: number; innerRequirement: Record<string, unknown> }>;
      totalDownstreamUsdc: number;
      buyerKey: string;
      subject: string;
    };
```

- [ ] **Step 2: Handle the requirement** — in `handleRequirement`, after the `purchase_execute` block (~line 172) add:

```typescript
    if (offeringName === "stack_execute") {
      const quoteId = String(requirement.quoteId);
      const subject = String(requirement.subject);
      const maxFundsUsdc = Number(requirement.maxFundsUsdc);
      const buyerKey = job.clientAddress;

      const pre = await client.stackPrecheck({ outerJobId: session.jobId, buyerKey, quoteId, subject });
      if (!pre.ok) { await session.sendMessage(`stack precheck rejected: ${pre.reason}`); return; }
      if (pre.totalDownstreamUsdc > maxFundsUsdc) { await session.sendMessage(`stack total ${pre.totalDownstreamUsdc} exceeds maxFundsUsdc ${maxFundsUsdc}`); return; }

      // Escrow: our fee (0.25) as budget + the quoted total downstream as a fund
      // request to our own wallet (Require-Funds), exactly like purchase_execute.
      await session.setBudgetWithFundRequest(
        AssetToken.usdc(0.25, session.chainId),
        AssetToken.usdc(pre.totalDownstreamUsdc, session.chainId),
        env.walletAddress as `0x${string}`,
      );
      pending.set(session.jobId, {
        kind: "stack", offeringName: "stack_execute",
        steps: pre.steps.map((s) => ({ targetAgent: s.targetAgent, targetOffering: s.targetOffering, role: s.role, quotedPriceUsdc: s.quotedPriceUsdc, innerRequirement: s.innerRequirement })),
        totalDownstreamUsdc: pre.totalDownstreamUsdc, buyerKey, subject,
      });
      return;
    }
```

- [ ] **Step 3: Handle funding** — in `handleJobFunded`, after the `stash.kind === "execute"` block (~line 222) add:

```typescript
    if (stash.kind === "stack") {
      const results: Array<{ targetAgent: string; targetOffering: string; role: string; status: string; innerJobId: string | null; deliverable?: unknown; error?: string }> = [];
      let allOk = true;
      let failReason = "";
      // Per-step inner timeout: bounded so N serial hires fit the 30-min outer SLA.
      const innerTimeoutMs = Math.min(180_000, Math.floor((28 * 60_000) / Math.max(stash.steps.length, 1)));
      for (const step of stash.steps) {
        const hire = await purchaser.hireOnBehalf(step.targetAgent, step.targetOffering, step.innerRequirement, step.quotedPriceUsdc, innerTimeoutMs);
        if (hire.status === "completed" && hire.deliverableParsed !== undefined) {
          results.push({ targetAgent: step.targetAgent, targetOffering: step.targetOffering, role: step.role, status: "delivered", innerJobId: hire.jobId, deliverable: hire.deliverableParsed });
        } else {
          allOk = false;
          failReason = `downstream_failed:${step.targetOffering}:${hire.status}${hire.error ? `:${hire.error}` : ""}`;
          results.push({ targetAgent: step.targetAgent, targetOffering: step.targetOffering, role: step.role, status: "failed", innerJobId: hire.jobId || null, error: hire.error });
          break; // all-or-nothing: stop on first failure
        }
      }

      const innerIds = results.map((r) => r.innerJobId).filter(Boolean).join(",");
      if (allOk) {
        const deliverable = { status: "DELIVERED", subject: stash.subject, steps: results, downstreamChargedUsdc: stash.totalDownstreamUsdc, executeFeeUsdc: 0.25, reason: "" };
        // Require-Funds: submit MUST carry the full fund-request transferAmount.
        await session.submit(await toDeliverable(session.jobId, deliverable), AssetToken.usdc(stash.totalDownstreamUsdc, session.chainId));
        await client.stackSettle({ outerJobId: session.jobId, buyerKey: stash.buyerKey, state: "DELIVERED", innerJobIds: innerIds, reason: null, totalDownstreamUsd: stash.totalDownstreamUsdc });
        console.log(`[seller] stack_execute DELIVERED jobId=${session.jobId} steps=${results.length}`);
      } else {
        await session.reject(failReason);
        await client.stackSettle({ outerJobId: session.jobId, buyerKey: stash.buyerKey, state: "REJECTED", innerJobIds: innerIds || null, reason: failReason, totalDownstreamUsd: stash.totalDownstreamUsdc });
        console.log(`[seller] stack_execute REJECTED jobId=${session.jobId} reason=${failReason}`);
      }
      return;
    }
```

> All-or-nothing money correctness: on REJECTED we call `session.reject` and never `submit`, so no `transferAmount` moves — the buyer's full escrow (fee + fund request) is refunded by ACP. On DELIVERED we transfer exactly the fund request (`totalDownstreamUsdc`), identical to the single flow — no partial settlement. If a seller priced a step *below* the quote, the P61 cap funds the lower inner budget and Metabot keeps the small surplus, exactly as the single flow already does.

- [ ] **Step 4: Build the sidecar**

Run: `cd acp-v2 && npm run build`
Expected: clean `tsc` (0 errors). Fix any type mismatches (esp. the `pre.steps` shape vs the api-client interface).

- [ ] **Step 5: Commit**

```bash
git add acp-v2/src/seller.ts
git commit -m "feat(stack): seller.ts all-or-nothing stack_execute orchestration"
```

---

## Task 13: Build gates (P32 + full suite)

- [ ] **Step 1: C# full suite**

Run: `cd ACP_Metabot/ACP_Metabot && dotnet test ACP_Metabot.Api.Tests/ACP_Metabot.Api.Tests.csproj`
Expected: all pass.

- [ ] **Step 2: Sidecar build + offerings/P32 gate**

Run: `cd acp-v2 && npm run build && npm run print-offerings`
Expected: clean tsc; `print-offerings` renders `stack_quote` + `stack_execute` and passes the property-description (P32) + name(≤20)/description(≤500) gates. Fix any flagged missing description (incl. nested `items.properties`).

- [ ] **Step 3: Commit any fixes**

```bash
git add -A acp-v2/src
git commit -m "fix(stack): P32 description + tsc gate fixes"
```

---

## Task 14: Deploy + first-stack smoke

- [ ] **Step 1: Push + deploy both containers**

Per `acp-bot-deploy`: push `main`; on the droplet `cd /root/ACP_Metabot && git pull --ff-only origin main`; rebuild **both** `acp-metabot-api` and `acp-metabot-acp` (sidecar changed) via the detached-build + poll pattern; verify `healthy`.

- [ ] **Step 2: Smoke via ACP_Tester**

`stack_quote` over a known wallet subject + intent ("safety + reputation screen") → confirm a `quoteId` + ≥2 steps from portfolio analysis bots (e.g. `risk_snapshot`, `agentRiskCheck`). Then `stack_execute` with that `quoteId` → confirm a combined `DELIVERED` report with per-step deliverables + on-chain inner job ids, and that escrow math (fee + downstream) settled. (Resources/quotes don't prove the buy — exercise the real execute. See `reference_acp_first_hire_smoke`.)

- [ ] **Step 3:** Record the smoke result (jobIds) in a memory note.

---

## Task 15: Marketplace registration + docs lockstep

- [ ] **Step 1:** `cd acp-v2 && npm run print-offerings` → give Oliver the `stack_quote` + `stack_execute` blocks to paste into app.virtuals.io (Offerings → New offering, under TheMetaBot). Manual step (no programmatic registration). After, diff marketplace-vs-source via `acp_browse_agent`.

- [ ] **Step 2:** Docs lockstep (`feedback_acp_docs_in_lockstep`): update Metabot `README.md` + `docs/user-guide.md` with the two offerings. If/when exposed via the acp-find plugin, that is a separate follow-up (out of v1).

- [ ] **Step 3: Commit docs**

```bash
git add ACP_Metabot.Api/../README.md docs/user-guide.md
git commit -m "docs(stack): document stack_quote + stack_execute"
```

---

## Self-review notes (addressed inline)
- **Spec coverage:** quote (T3), price-bound plan + expiry (T2/T3), buyer-bind + cap reserve (T4), all-or-nothing settle/refund (T5/T12), risk + fixed-price + subject-map filters (T3), combined deliverable (T12), offerings + pricing (T7-T10), P32 (T13), deploy + smoke + marketplace (T14-T15). The spec's "partial delivery" is intentionally replaced by all-or-nothing (approved deviation, noted at top).
- **Type consistency:** `StackPlanStep` (C#) ↔ `pre.steps[]` (TS) field names must match System.Text.Json camelCasing — verified against the existing `purchasePrecheck` shape in T11. `ExecuteFeeUsdc = 0.25` is consistent across C# (`StackPurchaserService`), pricing.ts, and the seller.ts escrow.
- **Open verification during impl:** (1) `Db` test-construction (match `DbTests.cs`); (2) whether `StackComposerService` is already in DI; (3) System.Text.Json casing of `StackPrecheckResult` over the wire. Each is called out in its task.
