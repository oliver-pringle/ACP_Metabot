# ACPPurchaser Path A — design (folded into TheMetaBot)

**Date:** 2026-06-01
**Status:** design — pending implementation plan
**Owner:** Oliver Pringle
**Home:** TheMetaBot (`ACP_Metabot/ACP_Metabot`) — NOT a new bot (R16 PART 3 "Path A")

## 1. Goal & why

Every bot in the portfolio reads `score=0 / hires=0` because nobody generates organic
paid hires — the binding cold-start constraint. ACPPurchaser is an agent that **buys on
a buyer's behalf**: it hires a target offering on another agent, pays for it, and returns
the deliverable + on-chain job id. Each `purchase_execute` is a real, paid, repeatable
hire that drives reputation → discoverability → more hires across all 14 bots. It is the
single highest-leverage build in R16 (scored 33/35 across R13–R15; explicit prior ask).

It lives on TheMetaBot: reuses Metabot's Privy wallet, its `agentRiskScorer` +
`preHireBudgetCheck` safety lanes, its SQLite, and its sidecar — no new container, no new
agent slot, no new float-isolation wallet.

## 2. Scope

**v1 (this spec):** two offerings — `purchase_quote`, `purchase_execute`.
**Fast-follow (NOT v1):** `purchase_recur` ($5/mo BSB worker + `purchase_subscriptions`
table + HMAC webhook).
**v1 downstream constraint:** **fixed-price** downstream offerings only. Percentage-of-
principal / dynamic-budget targets are rejected with a clear message.

**Non-goals:** multi-buyer concurrent signing (SDK is single-Privy-wallet-per-process →
v1 serializes inner hires, one in-flight, queued); cross-chain targets (Base only in v1,
matching Metabot's wallet chain); evaluator-mediated jobs (evaluator = zeroAddress).

## 3. The Require-Funds mechanism (verified against acp-node-v2 v0.0.6)

The marketplace "Require Funds" toggle maps to the SDK's fund-request primitive. Verified
from `@virtuals-protocol/acp-node-v2/dist`:

- `JobSession.setBudgetWithFundRequest(amount, transferAmount, destination)` —
  `jobSession.d.ts` / `jobSession.js:220`. `amount` = the service budget (our revenue),
  `transferAmount` = the required funds, `destination` = where those funds settle.
- Buyer's `JobSession.fund()` auto-detects the fund-request intent and escrows **both**
  budget + transferAmount via `internalFundWithTransfer` (`jobSession.js:228-249`). So a
  buyer hiring `purchase_execute` funds it with **no buyer-side changes** (ACP_Tester's
  existing `session.fund()` on `budget.set` already does this).
- **Timing (load-bearing):** the fund transfer to `destination` *"will be executed on
  completion"* (`jobSession.js:346`). Funds are escrow-locked at funding time but only
  move at job completion.
- `AcpJob.getFundRequestIntent()` (non-escrow intent) / `getFundTransferIntent()` (escrow
  intent) — `acpJob.js:35-46`. Refund path exists on-chain (`claimRefund`, reject/expire).
- `AssetToken.usdc(amount, chainId)` — `core/assetToken.d.ts`.

**Consequence:** the bot must produce the inner deliverable BEFORE it submits/completes,
which is BEFORE it receives the funds. So the bot **fronts** `downstreamCost` from
Metabot's wallet at inner-hire time and is **reimbursed on completion** with
`destination = Metabot's own ACP wallet`. Require-Funds does not pre-fund the bot — it
**guarantees reimbursement** (the buyer's money is escrow-locked before the bot spends).
This removes the unbacked-float risk; the remaining exposure is a short, bounded,
reimbursed working-capital window.

## 4. Offerings (v1)

### 4.1 `purchase_quote` — $0.02 fixed, Require Funds OFF
Given a target, return the exact cost + a pre-hire safety pass. No money moves downstream.

requirement:
```json
{
  "targetAgent":   { "type": "string", "description": "Wallet address of the seller agent to hire on the buyer's behalf." },
  "targetOffering":{ "type": "string", "description": "Exact offering name on the target agent to purchase." },
  "requirement":   { "type": "object", "description": "The requirement payload to send to the target offering (its own input schema)." }
}
```
deliverable:
```json
{
  "targetAgent":     { "type": "string",  "description": "The resolved target agent wallet address." },
  "targetOffering":  { "type": "string",  "description": "The resolved target offering name." },
  "downstreamUsdc":  { "type": "number",  "description": "Fixed USDC price of the target offering, or null if not fixed-price." },
  "serviceFeeUsdc":  { "type": "number",  "description": "ACPPurchaser service fee for purchase_execute (0.10)." },
  "totalEscrowUsdc": { "type": "number",  "description": "What the buyer escrows on purchase_execute = serviceFee + downstreamUsdc." },
  "fixedPrice":      { "type": "boolean", "description": "True if the target is fixed-price and supported by purchase_execute v1." },
  "riskTier":        { "type": "string",  "description": "Pre-hire scam-risk tier of the target agent: LOW|MEDIUM|HIGH|CRITICAL." },
  "verdict":         { "type": "string",  "description": "PROCEED|CAUTION|BLOCK recommendation for purchase_execute." },
  "reasons":         { "type": "array", "items": { "type": "string", "description": "Reason contributing to the verdict." }, "description": "Human-readable reasons behind the verdict." }
}
```

### 4.2 `purchase_execute` — $0.10 fixed service + Require Funds ON, SLA ≥ 10 min
Marketplace form: Price (Fixed) = **0.10** (the service revenue); **Require Funds = ON**
with the funds amount = the downstream offering cost (set dynamically by the seller per
job via `setBudgetWithFundRequest`).

requirement:
```json
{
  "targetAgent":    { "type": "string", "description": "Wallet address of the seller agent to hire on the buyer's behalf." },
  "targetOffering": { "type": "string", "description": "Exact offering name on the target agent to purchase." },
  "requirement":    { "type": "object", "description": "The requirement payload forwarded verbatim to the target offering." },
  "maxFundsUsdc":   { "type": "number", "description": "Buyer-authorized ceiling on the downstream cost; the bot rejects if the target costs more." }
}
```
deliverable:
```json
{
  "status":           { "type": "string", "description": "DELIVERED|REJECTED — outcome of the buy-on-behalf." },
  "targetAgent":      { "type": "string", "description": "The agent that was hired." },
  "targetOffering":   { "type": "string", "description": "The offering that was purchased." },
  "innerJobId":       { "type": "string", "description": "On-chain job id of the inner hire, or null if it never started." },
  "downstreamUsdc":   { "type": "number", "description": "USDC paid to the target seller for the inner hire." },
  "serviceFeeUsdc":   { "type": "number", "description": "Service fee retained by ACPPurchaser (0.10)." },
  "deliverable":      { "type": "object", "description": "The parsed deliverable returned by the target offering, or null on REJECTED." },
  "reason":           { "type": "string", "description": "On REJECTED, why (e.g. over maxFunds, risk BLOCK, cap exceeded, downstream failed)." }
}
```

## 5. Flows

**Price resolution rule (applies to quote AND execute):** C# `.Api` cannot speak ACP, so
the **sidecar** resolves the downstream price live via `getAgentByWalletAddress` →
`offering.price` and passes `downstreamUsdc` + `fixedPrice` INTO the `.Api` call. The
`.Api` never needs ACP access; it owns risk + cap + verdict + audit only.

### 5.1 `purchase_quote`
1. Sidecar receives requirement; resolves the target offering live (`getAgentByWalletAddress`
   → find `targetOffering` → `price`, `fixedPrice`). If the target/offering is not found,
   return a BLOCK verdict with reason `target_not_found`.
2. Sidecar → `.Api` `POST /v1/buyer/purchase/quote` with `{targetAgent, downstreamUsdc, fixedPrice}`.
3. `.Api` runs safety pass: `agentRiskScorer.ScoreAsync(targetAgent)` → `riskTier`; computes
   `verdict` (BLOCK if CRITICAL or `!fixedPrice`; CAUTION if HIGH; else PROCEED) +
   `totalEscrowUsdc = 0.10 + downstreamUsdc`.
4. Return the deliverable. No state change, no money.

### 5.2 `purchase_execute` (sidecar orchestrates; `.Api` gates)
1. Sidecar receives requirement `{targetAgent, targetOffering, requirement, maxFundsUsdc}`
   and resolves the target **live** (`getAgentByWalletAddress`) → `downstreamUsdc`,
   `fixedPrice`. If not found / not fixed-price → `session.reject` immediately.
2. Sidecar → `.Api` `POST /v1/internal/buyer/purchase/precheck` (X-API-Key) with
   `{outerJobId, buyerKey, targetAgent, targetOffering, downstreamUsdc, maxFundsUsdc}`. `.Api`:
   - **reject** if `downstreamUsdc > maxFundsUsdc`.
   - safety pass → **reject (BLOCK)** if risk CRITICAL.
   - **atomic cap reservation** (`PurchaserBudgetService.TryReserveAsync(buyerKey,
     downstreamUsdc)`) — reject if it would breach the per-buyer/day cap ($50 default).
   - returns `{ok, downstreamUsdc, reasonIfNot}` + writes a `purchase_audit` row (state=PRECHECK).
   - `buyerKey` = the outer job's `clientAddress`.
3. If precheck fails → `session.reject(reason)` (buyer fully refunded). Audit state=REJECTED.
4. If ok → **float check**: confirm Metabot's wallet USDC ≥ `downstreamUsdc` (+ small gas
   buffer); if not, reject + alert (low-float). 
5. `session.setBudgetWithFundRequest(usdc(0.10), usdc(downstreamUsdc), destination=METABOT_WALLET)`.
   Buyer's `fund()` escrows `0.10 + downstreamUsdc`.
6. On `job.funded`: enqueue the **inner hire** (serialized — one in-flight at a time):
   `createJobFromOffering(targetAgent, targetOffering, requirement)` — fronts
   `downstreamUsdc` from Metabot's wallet. Lifts ACP_Tester `buyer.hire()` semantics
   (browse → create → on inner `budget.set` `fund()` → recover deliverable via
   `getTransport().getHistory()` retry [0/500/1500ms] → settle).
7. **Inner success:** `session.submit(JSON.stringify(innerDeliverable))` on the OUTER job
   → auto-completes (evaluator=zero) → escrow releases `0.10` (revenue) + `downstreamUsdc`
   (reimburses the float) to Metabot. Then `.Api` `POST /v1/internal/buyer/purchase/settle`
   → `RecordActualSpendAsync` reconcile + audit state=DELIVERED. Return DELIVERED deliverable.
8. **Inner failure** (reject/expire/timeout): `session.reject("downstream_failed: <reason>")`
   on the OUTER job → buyer fully refunded, no fund transfer. Metabot recovers its fronted
   float via the inner job's on-chain refund (reject/expire path). `.Api` refunds the cap
   reservation (`RecordActualSpendAsync(buyerKey, -downstreamUsdc)`). Audit state=REJECTED.

### 5.3 Single-wallet serialization
The ACP node SDK signs from one Privy wallet per process. Inner hires MUST be serialized:
a process-local async queue (one inner `createJobFromOffering`+settle at a time). Outer
jobs can arrive concurrently but their inner hires run sequentially. Document as the v1
constraint. (Metabot's wallet is also doing its normal seller work — the inner-hire queue
must not block the seller event loop; the inner hire runs in the funded-job handler which
is already async per job.)

## 6. Safety model (layered; cap is a backstop, not the primary guard)
1. **Escrow-locked reimbursement** — the buyer's `downstreamCost` is locked before the bot
   fronts; primary protection against loss.
2. **Buyer-authorized ceiling** — `maxFundsUsdc` in the requirement; bot never exceeds it.
3. **Fixed-price-only** — no surprise dynamic cost in v1.
4. **Pre-hire risk gate** — `agentRiskScorer`; BLOCK on CRITICAL.
5. **Atomic per-buyer/day cap** — lifted `ClaudeBudgetService` keyed `(buyer_key, day_iso)`,
   default $50/day (env `ACPPURCHASER_DAILY_CAP_USDC`); bounds abuse + self-funded overage.
6. **Float-sufficiency check** — never accept an inner hire the wallet can't cover; alert
   on low float.
7. **Serialized signing** — one inner hire in flight; no concurrent-signing races.

## 7. Data model (`.Api` `Db.cs`, ADO.NET + SQLite, WAL)
```sql
CREATE TABLE IF NOT EXISTS acppurchaser_daily_spend (
    buyer_key   TEXT NOT NULL,
    day_iso     TEXT NOT NULL,            -- UTC yyyy-MM-dd
    total_usd   REAL NOT NULL DEFAULT 0.0,
    updated_at  TEXT NOT NULL,
    PRIMARY KEY (buyer_key, day_iso)
);
CREATE TABLE IF NOT EXISTS acppurchaser_audit (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    outer_job_id  TEXT,                   -- the purchase_execute job
    buyer_key     TEXT NOT NULL,
    target_agent  TEXT NOT NULL,
    target_offering TEXT NOT NULL,
    downstream_usd  REAL,
    service_fee_usd REAL,
    inner_job_id  TEXT,
    state         TEXT NOT NULL,          -- PRECHECK|DELIVERED|REJECTED
    reason        TEXT,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
```
Both added idempotently in `Db.InitializeSchemaAsync()` (CREATE TABLE IF NOT EXISTS).

`PurchaserBudgetService` = `ClaudeBudgetService` lifted verbatim, renamed, with a
`buyer_key` parameter added to every SQL predicate and the table above (PK
`(buyer_key, day_iso)`). Methods: `TryReserveAsync(buyerKey, estUsd, ct)`,
`RecordActualSpendAsync(buyerKey, deltaUsd, ct)`, `GetTodaysSpendAsync(buyerKey, ct)`.

## 8. Files

**`.Api`:**
- `Services/PurchaserBudgetService.cs` (lifted from PrivateTrader `ClaudeBudgetService`).
- `Services/PurchaserService.cs` — `QuoteAsync`, `PrecheckAsync`, `SettleAsync`, audit writes,
  price resolution (reuse the `offerings` table / `StackComposerService` lookup), risk via
  injected `AgentRiskScorer`.
- `Program.cs` — `POST /v1/buyer/purchase/quote` (public, rate-limited `public-compose`),
  `POST /v1/internal/buyer/purchase/precheck` + `/settle` (X-API-Key). Request records.
- `Data/Db.cs` — the two tables in `InitializeSchemaAsync`.
- Config: `ACPPURCHASER_DAILY_CAP_USDC` (default 50). (The fund-transfer `destination` is
  set in the sidecar using its EXISTING `ACP_WALLET_ADDRESS` env — the bot's own wallet —
  so no new `.Api` wallet config is needed.)

**`acp-v2` sidecar:**
- `src/offerings/purchaseQuote.ts`, `src/offerings/purchaseExecute.ts` (+ register in
  `registry.ts`, price in `pricing.ts`).
- `src/buyer.ts` (NEW) — the inner-hire engine lifted from `ACP_Tester/src/buyer.ts`
  (browse/create/fund/recover/settle), exposed as `hireOnBehalf(target, offering, req,
  timeoutMs)`, with a process-local serialized queue.
- `src/seller.ts` — for `purchase_execute`, branch the funded-job handling to:
  precheck → `setBudgetWithFundRequest` → inner hire → submit/reject. (`purchase_quote`
  uses the normal route → `.Api`.)
- `src/apiClient.ts` — add `purchaseQuote`, `purchasePrecheck`, `purchaseSettle` methods.
- `src/env.ts` — ensure the bot's own wallet address is available as the fund destination.

## 9. Pricing (`pricing.ts`)
`purchase_quote` = 0.02, `purchase_execute` = 0.10 (service fee; the downstream cost rides
the Require-Funds transfer, NOT the offering price). Both ≥ the $0.02 economic floor.

## 10. Testing (TDD)
- **`.Api` unit:** `PurchaserBudgetService` atomic cap (reserve to cap, reject over cap,
  refund restores, per-buyer isolation, day rollover); `PurchaserService.QuoteAsync`
  (fixed vs non-fixed, risk→verdict mapping, over-maxFunds); `PrecheckAsync` (all reject
  paths + the ok path writes audit). Use a temp SQLite file.
- **Sidecar unit:** offering `validate()` (missing fields, bad address) + the
  fixed-price/maxFunds rejection logic; mock the ACP client for `hireOnBehalf` (success
  path returns deliverable; reject path → outer reject); the serialized-queue invariant
  (two inner hires don't overlap).
- **Integration (manual, real USDC):** the smoke in §11.
- 0 build warnings; `npm run build` clean; `npm run print-offerings` passes the P32 gate.

## 11. Smoke plan (before "live with revenue")
1. `dotnet build` (0 warn) + `.Api` unit tests green; `cd acp-v2 && npm run build` +
   `npm run print-offerings` (renders, P32 passes).
2. Register `purchase_quote` + `purchase_execute` on app.virtuals.io (Metabot agent).
   `purchase_execute`: Require Funds = ON, Price Fixed = 0.10.
3. `purchase_quote` smoke via `mcp__acp-tester__acp_hire` targeting a cheap known offering
   (e.g. RevokeBot `spender_check` $0.02) → verify cost + verdict.
4. `purchase_execute` smoke: ACP_Tester hires Metabot `purchase_execute` with
   `{targetAgent=RevokeBot, targetOffering=spender_check, requirement={...},
   maxFundsUsdc=0.05}`. Verify: buyer escrowed `0.12`, inner job created against RevokeBot,
   inner deliverable returned, outer completed, Metabot net `+0.10` (reimbursed `0.02`),
   audit row DELIVERED. Watch the ledger on BaseScan.
5. Failure smoke: target a non-fixed-price or over-maxFunds → outer REJECTED, buyer
   refunded, no inner job.

## 12. Open risks / deferred
- **Inner-refund latency:** on inner failure the fronted float is recovered only when the
  inner job rejects/expires on-chain (could be minutes). Bounded by the per-buyer cap;
  acceptable v1. Monitor wallet float.
- **Price-resolution staleness:** the `offerings` index may lag the live target price → a
  precheck quote can differ from the real downstream price. Mitigate: re-resolve live via
  the sidecar at execute time; if live price > quoted (and ≤ maxFunds) use live, else reject.
- **Metabot wallet is shared** (holds Metabot's USDC + does seller work). Size float
  deliberately; alert on low balance. The cap bounds daily drain.
- **`purchase_recur`** (subscription) deferred to fast-follow.
- **Reserved field names:** none of the schema fields use `to`/`data`/`value` (compliant).

## 13. Marketplace registration notes
- `purchase_execute`: **Require Funds = ON** (the whole mechanism). Price Fixed = 0.10.
  The funds amount is set dynamically per job by the seller (`setBudgetWithFundRequest`),
  NOT a fixed value in the form.
- Names ≤ 20 chars (`purchase_quote` 14, `purchase_execute` 16 — OK). Descriptions ≤ 500.
- After registration, diff marketplace-vs-source via `acp_browse_agent`.
