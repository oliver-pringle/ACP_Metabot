# TheMetaBot `riskAttestPro` ($10 premium tier) — Design

**Author:** Oliver Pringle (with Claude Opus 4.7)
**Date:** 2026-05-30
**Status:** Design — approved (brainstorming locked)
**Strategic context:** R15 finding (`IdeasForACP2.0Bots15.txt`): V2 has 239 new agents / 30 days but ZERO V2 hires. Premium pricing wins (WhaleIntel $10 × 36k V1 hires = $360k precedent). Distribution > new bot adds.

## Goal

Ship a $10 USDC premium tier on TheMetaBot that synthesises 7 signal sources at **depth** (not breadth) into one deliverable serving three buyer types (ACP agents, human-facing orchestrators, compliance teams) via three surfaces of the same envelope (rich JSON / LLM-narrated summary / markdown report + on-chain attestation UID).

Differentiates from the existing `riskAttestation` ($0.50) by returning each signal's DEEP data product (full trajectories, per-row breakdowns, calldata bundles, history) rather than the composite score only — so a $10 hire returns ~20x the actionable artifacts of the $0.50 hire.

## Scope (locked)

| Concern | In scope |
|---|---|
| New offering `riskAttestPro` on TheMetaBot @ $10 USDC | YES |
| New endpoint `POST /v1/risk/attest-pro` | YES |
| New sidecar offering `acp-v2/src/offerings/riskAttestPro.ts` + registry + pricing | YES |
| New service `RiskAttestProService.cs` orchestrating 7 cross-bot lanes | YES |
| New `risk_snapshot_history` SQLite table for 30-day trajectory | YES |
| LLM-narrated executive summary via Haiku (budget-capped, deterministic-cached) | YES |
| On-chain EAS attestation via existing EASIssuer `/v1/eas-publish` cross-bot lane | YES |
| Markdown report (base64-encoded in deliverable) | YES |
| Ad-hoc revoke calldata bundles for top 10 high-risk approvals | YES (lifted from existing `riskDeepDive`) |
| New EAS schema registration for the richer `riskAttestPro` shape | YES (one-shot via existing `attest_schema` $1.00) |
| ~25-30 new tests | YES |

Out of scope:

- Daily-warmer worker that proactively snapshots wallets into `risk_snapshot_history` (default-OFF; the pro tier just does live re-fetches on demand at 7-day stride). Worker is a v1.0.1 follow-up if hire volume justifies.
- Compliance-grade PDF rendering (markdown is sufficient for v1; PDF rendering is heavy + adds dependencies).
- Cross-validated multi-orchestrator verdict (no value — same orchestrator returns same answer).
- Risk-trajectory beyond 30 days (longer windows = much larger storage + slower compute, defer until a real buyer asks).
- Real-time webhook subscription for ongoing risk monitoring on the wallet (would be a separate subscription offering at v1.1; `riskAttestPro` is a one-shot at v1.0).
- New cross-bot lanes that don't already exist today.
- Marketplace re-registration of any OTHER offering (additive new offering only).

## Decisions locked

- **Offering name:** `riskAttestPro` — 12 chars, well under the 20-char marketplace cap, mirrors `agentRiskCheck` casing.
- **Price:** $10 USDC one-shot. No subscription tier in v1.0.
- **SLA:** 60 seconds. The slowest cross-bot fetch + LLM call + on-chain publish fits comfortably; 5-min headroom for tail-latency.
- **Three surfaces, one envelope.** All buyers receive the same response object; different fields serve different buyers. No per-buyer enum or mode flag — buyers ignore surfaces they don't care about.
- **Depth over breadth.** Same 7 sources as today's riskAttestation family plus Witness manifest + 30-day trajectory; each returns its deep data product (full per-position view, per-spender classification with calldata, per-incident MEV detail, etc.) rather than the composite score.
- **Reuse, not rewrite.** `RiskAttestProService.cs` is an orchestrator over existing `RiskPeerClients.cs` + `RiskSnapshotService.cs` + `RiskDeepDiveService.cs`. No new cross-bot clients; lift `WitnessBot` client pattern from existing `RiskPeerClients`.
- **Trajectory: on-demand stride re-fetch.** No new worker. The endpoint re-fetches `riskSnapshot` 4 times at +day 0 / -day 7 / -day 14 / -day 21 stride (4 calls, ~2-3s each cached). Stored into new `risk_snapshot_history` table for future hires of the same wallet to dedupe via cache lookup (24h TTL per stride row).
- **LLM:** Anthropic Haiku via the existing Phase 3 budget-cap pattern (`risk_attest_pro_spend` table, $0.50/day rolling cap, $0.01/call cap, env `RISK_ATTEST_PRO_LLM_*`). Deterministic cache: key on SHA256 of the canonical sub-component JSON (so two hires of the same wallet within 1h get the same narration without burning LLM budget).
- **Markdown report:** generated deterministically from sub-component fields (no LLM call for the report itself — only the executive summary uses Haiku). Base64-encoded in the deliverable's `markdownReport` field. Buyers decode + render. ~5-15kB typical.
- **On-chain attestation:** new EAS schema registered via existing `attest_schema` $1.00 on first deploy. Schema includes: `address wallet, uint8 scorePro, string verdict, uint64 generatedAt, bytes32 componentsHash, string summaryHash`. Idempotent one-shot registration (race-safe via `INSERT OR IGNORE` like SchemaBootstrapWorker on EASIssuer v0.3).
- **Buyer signature:** NOT required in v1.0. Optional `buyerSignature` field accepted on request; if present, sidecar surfaces it through to EASIssuer; if absent, the bot signs as issuer. Strict mode deferred to v1.1.
- **Cross-bot fail-soft:** any cross-bot lane that errors or times out is surfaced in `sourcesUnavailable[]` and excluded from the composite score; verdict degrades gracefully. Floor: at least 4 of 7 sources must succeed or the endpoint 502s.
- **Cache:** 1-hour cache per wallet (key = walletAddress + chainId). Cached responses don't burn cross-bot quota AND don't burn LLM budget AND don't republish on-chain — the cached attestation UID is returned. Disable via `?fresh=true` query.

## Offering wire shape

### Request schema

```typescript
{
  walletAddress: string,    // 0x...40-hex; lower-cased before processing
  chain: "base" | "ethereum",  // optional, default "base"
  buyerSignature?: string,  // optional EIP-712 sig over the request envelope; v1.1 makes this required in strict mode
  fresh?: boolean,          // optional, default false; bypass 1h cache
}
```

### Deliverable schema (one envelope, three surfaces)

```typescript
{
  // --- Verdict layer (agent + orchestrator + compliance) ---
  verdict: "STRONG_BUY" | "OK" | "CAUTION" | "AVOID" | "INSUFFICIENT_DATA",
  scorePro: number,                          // 0-100 weighted composite
  grade: "A" | "B" | "C" | "D" | "F",

  // --- Agent surface: rich JSON per signal ---
  components: {
    healthFactor: {
      score: number,
      perProtocolPositions: Array<{ protocol, chain, hf, collateralUsd, debtUsd, ltv, liquidationThreshold, eMode? }>,
      sevenDayTrend: { direction: "improving" | "stable" | "declining", deltaPp: number },
      source: "LiquidGuard",
      status: "fresh" | "stale" | "unavailable",
      details: string,
    },
    approvals: {
      score: number,
      perSpender: Array<{ spender, token, allowanceTokens, priority, label, revokeCalldata?: { to, data, value } }>,
      highRiskCount: number,
      source: "RevokeBot",
      status, details,
    },
    mev: {
      score: number,
      sandwichIncidents: Array<{ blockNumber, txHash, lossUsd, sandwicher }>,
      thirtyDayRate: number,
      source: "MEVProtect",
      status, details,
    },
    reputation: {
      score: number,
      composite: number | null,                  // null when not an ACP agent
      thirtyDayTrajectory: Array<{ date, score }>,
      perOffering: Array<{ name, hires, totalEarningsUsdc }>,
      source: "TheMetaBot",
      status, details,
    },
    arena: {
      score: number,
      isParticipant: boolean,
      councilPicksHistory: Array<{ date, picks }>,
      sevenDayCorrelation?: number,
      source: "TheArenaBot",
      status, details,
    },
    witness: {
      score: number,
      isAcpAgent: boolean,
      manifestSigned: boolean,
      catalogueHash?: string,
      signerEoa?: string,
      signedAt?: string,
      verifyVerdict?: "current" | "envelope_tampered" | "signature_invalid" | "legacy_unbound",
      source: "TheWitnessBot",
      status, details,
    },
    trajectory: {
      sevenDayPriorScore: number | null,
      fourteenDayPriorScore: number | null,
      twentyOneDayPriorScore: number | null,
      direction: "improving" | "stable" | "declining" | "insufficient_data",
      source: "TheMetaBot internal (risk_snapshot_history)",
      status, details,
    },
  },

  // --- Orchestrator surface: LLM-narrated paragraph ---
  executiveSummary: string,                    // ~3-5 sentences, deterministic-cached by componentsHash

  // --- Action surface ---
  recommendations: Array<{
    priority: "critical" | "high" | "medium" | "low",
    action: "revoke" | "raise_hf" | "verify_witness" | "manual_review",
    calldata?: { to, data, value, description },
    rationale: string,
  }>,

  // --- Compliance surface ---
  markdownReport: string,                      // base64-encoded full audit-trail markdown (~5-15kB)
  attestation: {
    uid: string,                               // EAS attestation UID
    txHash: string,
    blockNumber: number,
    schemaUid: string,                         // riskAttestPro schema UID
    basescanUrl: string,
  },

  // --- Provenance ---
  walletAddress: string,                       // lowercased
  chain: "base" | "ethereum",
  generatedAt: string,                         // ISO-8601 UTC
  expiresAt: string,                           // ISO-8601, 24h after generatedAt
  sourcesQueried: string[],                    // ["LiquidGuard", "RevokeBot", ...]
  sourcesUnavailable: string[],                // subset that failed/timed-out
  componentsHash: string,                      // SHA256 of sorted JSON of `components` (LLM cache key)
  cacheHit: boolean,                           // true if served from 1h wallet cache
}
```

## Architecture + reuse

### New files (Metabot.Api side)

- `ACP_Metabot/ACP_Metabot/Metabot.Api/Services/RiskAttestProService.cs` — orchestrator over the 7 lanes. Reuses `RiskPeerClients`, `RiskSnapshotService`, `RiskDeepDiveService`.
- `ACP_Metabot/ACP_Metabot/Metabot.Api/Services/RiskTrajectoryStore.cs` — wraps the new `risk_snapshot_history` table. Read-through cache pattern.
- `ACP_Metabot/ACP_Metabot/Metabot.Api/Services/RiskAttestProMarkdown.cs` — deterministic markdown generator (no LLM).
- `ACP_Metabot/ACP_Metabot/Metabot.Api/Services/RiskAttestProLlm.cs` — Haiku-narrated executive summary with budget cap + cache by componentsHash. Lifted from Phase 3 `LlmQueryRewriter` shape.

### Modified files

- `Program.cs` — register `RiskAttestProService` + the new endpoint `POST /v1/risk/attest-pro` (X-API-Key gated).
- `Data/Db.cs` — add `risk_snapshot_history` table + `risk_attest_pro_spend` table + `risk_attest_pro_cache` table.
- `acp-v2/src/offerings/riskAttestPro.ts` (NEW) — sidecar offering.
- `acp-v2/src/offerings/registry.ts` — register new offering.
- `acp-v2/src/pricing.ts` — add price entry.
- `acp-v2/src/apiClient.ts` — add `riskAttestPro(req)` method calling `/v1/risk/attest-pro`.

### New SQLite tables

```sql
CREATE TABLE IF NOT EXISTS risk_snapshot_history (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  wallet        TEXT NOT NULL,          -- lowercased
  chain         TEXT NOT NULL,          -- "base" or "ethereum"
  captured_at   TEXT NOT NULL,          -- ISO-8601 UTC
  score         INTEGER NOT NULL,       -- composite 0-100
  components_json TEXT NOT NULL         -- full sub-component shape for re-narration
);
CREATE INDEX idx_risk_snapshot_history_wallet ON risk_snapshot_history(wallet, chain, captured_at DESC);

CREATE TABLE IF NOT EXISTS risk_attest_pro_spend (
  day_utc       TEXT PRIMARY KEY,       -- YYYY-MM-DD
  llm_calls     INTEGER NOT NULL DEFAULT 0,
  llm_cost_usd  REAL NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS risk_attest_pro_cache (
  wallet_chain  TEXT PRIMARY KEY,       -- lower(wallet) + ":" + chain
  response_json TEXT NOT NULL,
  attestation_uid TEXT NOT NULL,
  generated_at  TEXT NOT NULL,
  expires_at    TEXT NOT NULL
);
```

## Cross-bot lane reuse

All 7 cross-bot calls go through existing infrastructure:

| Lane | Client | Existing method | Pro change |
|---|---|---|---|
| LiquidGuard | `RiskPeerClients.LiquidGuard` | `HfCheckProAsync(wallet, chain)` | Add per-position + 7-day trend params |
| RevokeBot | `RiskPeerClients.Revoke` | `RevokeCalldataAsync(wallet, chain, top10)` | Already returns calldata bundles |
| MEVProtect | `RiskPeerClients.MevProtect` | `MevForensicsAsync(wallet, 30d)` | Already returns per-incident detail |
| Reputation | Internal | `AgentReputationService.GetWithTrajectory(addr)` | Already returns 30-day trajectory |
| Arena | `RiskPeerClients.Arena` | `ArenaPickHistoryAsync(wallet)` | Already returns history (was added 2026-05-18) |
| Witness | NEW (`RiskPeerClients.Witness`) | `ManifestByAgentAsync(addr) + ManifestVerifyAsync(uid)` | New 2-call sub-orchestrator; both methods exist on WitnessBot today |
| Trajectory | Internal (`RiskTrajectoryStore`) | Re-snap riskSnapshot at -7/-14/-21 stride OR read from history table | New |

## LLM narration

- **Model:** `claude-haiku-4-5` (per portfolio convention).
- **Prompt template:** static system prompt + structured JSON of `components` + verdict/score. Output: 3-5 sentence executive summary.
- **Cost cap:** `$0.50/day` rolling cap via atomic SQL UPDATE on `risk_attest_pro_spend`. When cap reached, `executiveSummary` falls back to a deterministic template ("Verdict: {grade}. Health factor {hf_status}, approvals {appr_status}, ..." — boring but coherent).
- **Cache:** `executiveSummary` cached by `componentsHash` for 1 hour. Two hires of the same wallet within 1h return the same narration without burning budget.
- **Prompt cache:** static system prompt cached via Anthropic prompt-caching (5-min TTL, ~$0.0001/cache-hit).

## On-chain attestation

- **Schema:** new EAS schema registered on first deploy via existing `attest_schema` $1.00 internal call. Schema string: `address wallet, uint8 scorePro, string verdict, uint64 generatedAt, bytes32 componentsHash, string summaryHash`.
- **Registration:** idempotent one-shot at boot via `SchemaBootstrapWorker`-style pattern (race-safe `INSERT OR IGNORE` into a new `risk_attest_pro_bootstrap_state` table). Schema UID stored in env + log for downstream consumers.
- **Publish:** every $10 hire publishes one attestation on Base mainnet via EASIssuer's existing `/v1/eas-publish` cross-bot lane. Gas paid by `EAS_OPERATOR_PRIVATE_KEY` (`0x693a…4633` per CLAUDE.md). Cost per attestation: ~$0.02 in gas.
- **Cached hires** (1h cache hit) return the previously-published attestation UID, no republish.

## Test plan

~30 new tests across:

- `RiskAttestProServiceTests.cs` (~10) — orchestrator with mocked cross-bot clients. Cases: all-fresh, 1 lane unavailable, 4-of-7 floor (proceeds), 3-of-7 floor (502s), partial-trajectory (insufficient_data direction).
- `RiskTrajectoryStoreTests.cs` (~5) — write/read via in-memory SQLite. Cases: empty history, stride re-fetch matches expected timestamps, dedup within stride.
- `RiskAttestProMarkdownTests.cs` (~5) — deterministic markdown shape, snapshot test against canonical input.
- `RiskAttestProLlmTests.cs` (~6) — budget-cap arithmetic (atomic SQL update), cache key by componentsHash, fallback template when cap hit, prompt-cache hit count.
- Endpoint-level (~4) — `/v1/risk/attest-pro` happy path / cache-hit / 4-of-7 success / fresh=true bypass.

All cross-bot clients mocked via existing `IRiskPeerClients` interface (same pattern as `riskSnapshot` tests).

## What's deferred

- Daily warmer worker that proactively fills `risk_snapshot_history` for active wallets (v1.0.1 follow-up; only if hire volume justifies the extra cross-bot quota burn).
- Real subscription tier (`riskAttestPro-watch`) for ongoing risk monitoring with webhook deliveries — v1.1.
- PDF report rendering (markdown is sufficient for v1; buyer can render to PDF locally).
- Strict mode (require `buyerSignature` and 422 on missing) — v1.1; mirrors EASIssuer's v0.5 pattern.
- Additional signals (Homelander 13F-style insider trades, OracleBot per-token drift profile for the wallet's holdings) — v1.1+ when buyer feedback ranks them.
- Multi-chain coverage beyond Base + Ethereum mainnet — v1.2+.

## Backwards compat

Purely additive. New offering + new endpoint + new tables. No changes to existing offerings, existing endpoints, existing schemas, existing tables. Existing `riskAttestation` ($0.50) stays unchanged — `riskAttestPro` is the premium parallel tier, not a replacement.

## Acceptance criteria

- `cd ACP_Metabot/ACP_Metabot/acp-v2 && npm run build` clean.
- `npm run print-offerings` shows 20 offerings (was 19) and passes the P32 scanner.
- `dotnet build` clean; `dotnet test` 234 + ~30 new = ~264 tests green.
- Endpoint `POST /v1/risk/attest-pro` returns a valid envelope for a test wallet end-to-end (mocked cross-bot lanes in unit tests; live one-hire smoke after deploy).
- On-chain schema registered at first boot (`risk_attest_pro_bootstrap_state` table has the row).
- Marketplace registration: the new offering shows on app.virtuals.io after Oliver pastes the `print-offerings` block.

## Estimated effort

~8-10 hours implementer time:

- ~2h: `RiskAttestProService` orchestrator + cross-bot fan-out + 4-of-7 floor logic
- ~1h: `RiskTrajectoryStore` + new SQLite tables + read-through cache
- ~1.5h: `RiskAttestProLlm` (LLM narration + budget cap + cache)
- ~1h: `RiskAttestProMarkdown` (deterministic generator)
- ~0.5h: Schema bootstrap worker + EAS-publish wiring
- ~2h: ~30 new tests across all components
- ~1h: Sidecar offering + endpoint + apiClient + Program.cs registration
- ~0.5h: print-offerings verify + manual end-to-end live smoke

Plus adversarial-verify pass per the v0.4/v0.5 pattern (catches load-bearing issues pre-merge).

## Open questions

None — all locked above. If new questions surface during writing-plans, they get folded into v1.0.1.

## Related artifacts

- R15 strategy: `IdeasForACP2.0Bots15.txt` (workspace root, 2294 lines)
- Existing risk-family offerings: `ACP_Metabot/acp-v2/src/offerings/risk*.ts`
- Cross-bot client pattern: `ACP_Metabot/Metabot.Api/Services/RiskPeerClients.cs`
- LLM cost-cap pattern reference: Metabot v1.10 Phase 3 `LlmQueryRewriter` (commit history)
- EASIssuer cross-bot lane: `easissuer-api:5000/v1/internal/attest` + `/v1/eas-publish` (see memory `project_acp_easissuer*`)
- Schema bootstrap pattern: EASIssuer v0.3 `SchemaBootstrapWorker` (see memory `project_acp_easissuer_v0_4_shipped`)
