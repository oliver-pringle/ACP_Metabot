# TheMetaBot — Stack Purchase Router (intent→stack buy-on-behalf), v1

**Date:** 2026-06-03
**Status:** Approved design, pre-implementation
**Owner:** Oliver Pringle
**Builds on:** ACPPurchaser Path A (`purchase_quote` / `purchase_execute`, single fixed-price buy-on-behalf — live since 2026-06-01, commit e2b53e3). See `memory/project_acp_purchaser_pathA_shipped.md` and `memory/project_acp_purchaser_fund_drain_finding.md` (P61).

## 1. Goal & context

Extend ACPPurchaser from a **single** buy-on-behalf into an **intent→stack router**: a buyer agent describes a goal over a subject, and TheMetaBot uses its unique assets (the marketplace index + `composeStack` LLM curation + on-chain reputation + scam-risk scoring) to curate, screen, and **buy a whole stack of complementary offerings on the buyer's behalf**, returning a single combined deliverable.

This is the one capability a buyer agent **cannot replicate itself** — it requires the index Metabot already owns. It is the intended revenue/positioning lever ("move the needle"): Metabot becomes the trusted aggregation entry-point for multi-agent work.

**Decisions locked in brainstorming (2026-06-03):**
- **Buyer:** other ACP agents (B2B agent-to-agent).
- **Wedge:** intent→stack routing (curation is the value), not funds-relay or trust-escrow.
- **Control model:** `quote → approve → execute`. Quote returns a price-bound plan; execute validates and runs it. Metabot does the curation; the buyer authorizes the exact spend (the P61 money-safety lesson).
- **Fee model:** flat fee per executed stack (~$0.25) + downstream; quote carries a small curation fee (~$0.05).
- **v1 stack type:** **analysis fan-out** — independent steps over a shared subject, no output→input chaining.

## 2. Non-goals (explicitly v2+)

- **Execution pipelines** (step N's input = step N-1's output; e.g. quote→swap). Needs output→input mapping + non-fixed-price execution legs. Deferred.
- **Auto-derived per-step requirements from free intent** beyond the shared subject. v1 threads only the subject.
- **Non-fixed-price steps.** Dynamic/subscription offerings are dropped from the plan at quote time (mirrors the single flow's `not_fixed_price` BLOCK).
- **Cross-chain** (Solana / ETH mainnet targets). v1 is Base only.
- **Buyer-supplied arbitrary per-step requirements.** v1 only threads the single subject; richer per-step inputs are the "Buyer-supplied per-step inputs" option, deferred.

## 3. Architecture

Two **new** offerings (Approach A — dedicated, not an overload of `purchase_*`), reusing the existing money-core:

```
buyer agent
  │  (ACP hire: stack_quote)                 (ACP hire: stack_execute)
  ▼                                           ▼
acp-v2 sidecar  stackQuote.ts                 stackExecute.ts
  │  POST /v1/internal/stack/quote            │  POST /v1/internal/stack/precheck
  ▼                                           │  then N inner ACP hires (Require-Funds)
ACP_Metabot.Api                               │  then POST /v1/internal/stack/settle
  StackPurchaserService  ── uses ──►          ▼
    • StackComposerService (composeStack curation)
    • AgentRiskScorer       (per-step scam-risk tier)
    • OfferingRepository    (live price + PriceType → fixed-price gate)
    • PurchaserBudgetService (per-buyer daily cap, atomic reserve/refund)
    • StackQuoteStore (NEW)  (price-bound plan persistence + expiry)
    • acppurchaser_audit / acppurchaser_stack_audit (NEW) (forensic trail)
```

**Reused as-is:** `PurchaserBudgetService` (daily cap), `AgentRiskScorer`, `StackComposerService`, `OfferingRepository`, the Require-Funds buy-on-behalf mechanism the single `purchase_execute` already uses.

**New C# units:**
- `StackPurchaserService` — owns the stack money-safety logic (quote build, plan binding, precheck/cap-reserve, per-step settle/refund, deliverable combine). Mirrors `PurchaserService`'s seams (e.g. `IAgentRiskSource`) so it is unit-testable without a chain.
- `StackQuoteStore` (ADO.NET + SQLite) — persists each bound plan: `{ quote_id, buyer_key, subject, steps_json, total_downstream_usd, expires_at, created_at }`.
- `acppurchaser_stack_audit` table (or extend the existing audit) — one row per stack execute with per-step status JSON.

**New sidecar units:** `acp-v2/src/offerings/stackQuote.ts`, `acp-v2/src/offerings/stackExecute.ts`; registry + pricing entries. `stackExecute` orchestrates the N inner hires (Require-Funds) keyed to the single outer job, then combines deliverables.

## 4. Data flow

### 4.1 `stack_quote`
Input: `{ subject: string (0x address, Base), intent: string, maxFundsUsdc: number, maxSteps?: number ≤ 5 }`

1. `StackComposerService` curates candidate steps from `intent` (returns `{ agentAddress, offeringName, priceUsdc, role }[]`).
2. For each candidate (cap at `maxSteps`, default 5):
   - Resolve live offering via `OfferingRepository`: confirm it still exists, capture `PriceType`. **Drop** non-fixed-price (record `dropped: not_fixed_price`).
   - `AgentRiskScorer` tier. **Drop** `critical` (record `dropped: risk_critical`); flag `high` as `CAUTION`.
   - Confirm the offering's `requirementSchema` has a single address-type field the `subject` can fill. **Drop** if the offering needs inputs the subject can't satisfy (record `dropped: subject_unmappable`).
3. Compute `totalDownstreamUsdc = Σ kept step price`, `totalEscrowUsdc = totalDownstreamUsdc + executeFee`.
4. Reject the whole quote if `totalDownstreamUsdc > maxFundsUsdc` (verdict `BLOCK`, reason `over_max_funds`) or if **zero** steps survive (verdict `BLOCK`, reason `no_buyable_steps`).
5. Persist the bound plan in `StackQuoteStore` with `expiresAt = now + 15min`; return `{ quoteId, subject, steps:[{agentAddress, offeringName, role, priceUsdc, riskTier, verdict}], droppedCandidates:[…], totalDownstreamUsdc, executeFeeUsdc, totalEscrowUsdc, verdict, expiresAt }`.

No funds move. Charged: the `stack_quote` offering price (~$0.05).

### 4.2 `stack_execute`
Input: `{ quoteId: string, subject: string, maxFundsUsdc: number }`

1. Load the quote from `StackQuoteStore`. Validate: exists, **not expired**, `buyer_key` == this caller, `subject` == quoted subject. Else `BLOCK` (`quote_expired` / `quote_not_found` / `subject_mismatch` / `buyer_mismatch`).
2. **Re-resolve each step's live price.** If a step's current price **exceeds** its quoted price → **drop that step** (mark `skipped: price_inflated`) and proceed with the rest — the total can only decrease, so it stays ≤ the approved total. This is the **P61 guard generalized to N steps** (never spend more than approved per step). Lower-or-equal price is fine; charge the lower. (Dropping rather than aborting the whole execute is deliberate: one step re-pricing shouldn't kill an otherwise-valid approved stack.)
3. `PurchaserBudgetService.TryReserveAsync(buyerKey, totalDownstreamUsdc)` — atomic daily-cap reservation. Fail → `BLOCK` `daily_cap_exceeded` (no hires).
4. Fan-out the inner hires (Require-Funds), threading `subject` into each step's address field. Concurrency: bounded-concurrent preferred; **falls back to sequential** if the Privy signer proves unreliable under concurrency (correctness identical, latency only — see Risks). Each inner hire capped at its quoted price (`maxFunds` per step).
5. Per-step settle:
   - delivered → keep the deliverable.
   - rejected / expired / timeout / error → mark `failed`, **refund that step's reservation** via `RecordActualSpendAsync(buyerKey, -stepPrice)`.
6. Combine into one deliverable: `{ subject, summary, steps:[{agentAddress, offeringName, role, status, innerJobId?, deliverable?|error?}], deliveredCount, failedCount, downstreamChargedUsdc }`.
7. **Fee charging:** the flat execute fee is charged iff **≥1 step delivered**. If **zero** steps deliver → refund the full reservation and the fee (no value rendered); return `status: no_deliverables`.
8. Audit: one `acppurchaser_stack_audit` row with the per-step status JSON; reuse the `PRECHECK/DELIVERED/REJECTED` state vocabulary.

Charged: `executeFee` (~$0.25, iff ≥1 delivered) + Σ delivered-step downstream.

### 4.3 Deliverable size
N combined analysis deliverables may exceed the 50KB inline limit. If the combined payload > 50KB, persist to the C# `/deliverables` endpoint and return a reference (the boilerplate pattern), else inline.

## 5. Money-safety invariants (carry the P61 lesson)
1. **Plan is price-bound.** Execute never spends more than the quoted per-step price; any live price increase aborts that step (`price_inflated`). This is the generalized P61 inner-fund cap.
2. **Subject is the only injected input**, and only into an address-type field — no arbitrary requirement injection from the buyer.
3. **Total bounded twice:** ≤ `maxFundsUsdc` (caller-set) AND ≤ remaining daily cap (atomic reserve).
4. **Refund-on-failure is symmetric** per step (mirrors single-flow `SettleAsync`); zero-delivery refunds everything incl. fee.
5. **Quote expiry (15 min)** prevents stale-price execution.

## 6. Offerings & pricing
- `stack_quote` — ~**$0.05** (`pricing.ts`). Covers the LLM curation cost; deterministic-ish, cached where possible.
- `stack_execute` — ~**$0.25** flat fee + downstream (Require-Funds escrow = fee + Σ quoted step prices, capped by `maxFundsUsdc`).
- Both: `slaMinutes ≥ 5` (execute likely 10), full P32-compliant requirement + deliverable schemas with per-property descriptions incl. nested `items.properties`. Names ≤20 chars, descriptions ≤500.
- **Naming:** default `stack_quote` / `stack_execute` (generalizes to future pipeline stacks). Alternative `screen_quote` / `screen_execute` if we want to signal the v1 analysis focus — Oliver's call at registration.
- **Reserved-field guard:** schema field names avoid `to`/`data`/`value` (use `subject`, `maxFundsUsdc`, `quoteId`).

## 7. Scope limits (v1)
- Analysis fan-out only (independent steps, shared subject).
- Fixed-price steps only; Base only; ≤5 steps.
- Per-buyer daily cap (reuse `PurchaserBudgetService`; confirm the $50/day default is appropriate for stacks or set a separate stack cap).
- Subject is a single Base address (the common analysis input — wallet/agent/token contract).

## 8. Testing (TDD, mirrors `PurchaserServiceTests` / `PurchaserBudgetServiceTests`)
Unit (no chain — use the `IAgentRiskSource` seam + a fake composer + fake offering repo):
- Quote: drops non-fixed-price; drops `critical` risk; drops subject-unmappable; `BLOCK` on `over_max_funds`; `BLOCK` on `no_buyable_steps`; total math; persists a bound plan with expiry.
- Execute validation: `quote_expired`, `quote_not_found`, `subject_mismatch`, `buyer_mismatch`.
- **`price_inflated` reject** when a step's live price exceeds quoted (the P61 generalization) — and accept when live ≤ quoted, charging the lower.
- Daily-cap reserve fail → no hires.
- Partial failure: per-step refund; combined report status counts; **fee charged iff ≥1 delivered**; zero-delivery → full refund incl. fee.
- Audit rows written for PRECHECK / DELIVERED / REJECTED.

## 9. Deployment & rollout
- API + sidecar change (both containers). Deploy `acp-metabot-api` + `acp-metabot-acp` (detached build + poll; `git pull` first).
- `cd acp-v2 && npm run build` clean + `npm run print-offerings` passes the P32 gate before declaring done.
- New offerings are **manual** to register on app.virtuals.io (print blocks → Oliver pastes into Offerings → New offering). Diff marketplace-vs-source via `acp_browse_agent` after.
- Docs lockstep: Metabot README + user-guide; if exposed via the acp-find plugin later, that's a separate follow-up (out of v1).
- First-hire smoke via ACP_Tester: a real `stack_quote` then `stack_execute` over a known subject (e.g. a wallet) hitting 2-3 portfolio analysis bots; verify combined deliverable + per-step settle + correct charging. (Resources/quotes don't prove the execute path — exercise the real buy.)

## 10. Risks / open items
- **Privy signer concurrency:** N inner hires from one Metabot wallet may stress the Privy WaaS signer (cf. the cold-boot `UND_ERR_SOCKET` flake). Mitigation: bounded concurrency with sequential fallback; correctness identical.
- **One outer job → N inner hires** keyed correctly in the sidecar (the single flow does 1:1; verify the SDK cleanly supports 1:N within one outer job, incl. funding math).
- **Daily-cap semantics for stacks:** a single stack can reserve up to `maxFundsUsdc` of the buyer's daily cap at once — confirm the cap value/behaviour is right for multi-step.
- **`composeStack` quality on narrow intents:** if curation returns 0–1 usable steps often, the product is thin; the `droppedCandidates` telemetry in the quote will show this in practice.
- **Subject→requirement mapping** heuristic (which schema field is "the address") must be conservative — drop on ambiguity rather than guess (money safety).
