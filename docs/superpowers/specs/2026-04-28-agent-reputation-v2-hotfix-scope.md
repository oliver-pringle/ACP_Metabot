# agentReputation v2 — v1.0.1 Hotfix Scope

**Status:** Scope queued for next session. NOT a design spec — next session brainstorms a design from this scope, then plans, then executes.

**Context:** v2 (behavioural, on-chain) shipped to prod 2026-04-28. Smoke test the same day caught two blocking bugs and several smaller ones. None of v2 actually works for buyers right now: the daily warmer at 02:30 UTC will fail, and any paid hire returns `compute_failed`.

**Production state at time of writing:**
- Container `acp-metabot-api` started 2026-04-28T09:15:21Z on droplet 138.68.174.116
- `BASE_RPC_URL=https://mainnet.base.org`, `ACP_CONTRACT_ADDRESS_BASE=0x238E541BfefD82238730D00a2208E5497F1832E0`
- `INTERNAL_API_KEY` is 102 chars (longer than the 64-char hex of the BasicBot template)
- Public `GET /v1/agentReputation?agent=…` correctly returns `404 not_cached`
- Public `POST /v1/agentReputation` correctly returns `405`
- `LifetimeSnapshotService` ran on boot, wrote 18,593 rows for 2026-04-28
- `ReputationWarmerService` is sleeping until 2026-04-29T02:30:00Z — first run untested

---

## Blockers (v1.0.1 must-fix)

### B1. ChainEventScanner queries an unbounded block range

**Files:**
- `ACP_Metabot.Api/Services/ChainEventScanner.cs` (lines 82, 96, 112, 139, 152, 165, 180)
- `ACP_Metabot.Api/appsettings.json` (line 36 — `Reputation:ContractDeployBlock = 0`)

**Symptom:** `eth_getLogs` calls span ~28M blocks (genesis → head on Base mainnet) because `ContractDeployBlock = 0` and there's no chunking. Every public RPC rejects.

**Reproduced today:**
- `mainnet.base.org` → `{"error":"compute_failed","message":"Error occurred when trying to send rpc requests(s): eth_getLogs"}`
- `base.publicnode.com` → `{"error":"compute_failed","message":"exceed maximum block range: 50000: eth_getLogs"}`

**Fix needed (next session decides exact shape):**
1. Look up the actual ACP V2 contract deploy block for `0x238E541BfefD82238730D00a2208E5497F1832E0` on Base mainnet (BaseScan: contract creation tx → block number) and set `Reputation:ContractDeployBlock` accordingly.
2. Chunk every `GetAllChangesAsync` call into ≤10K-block windows. Helper method that takes `(handler, filterBuilder, fromBlock, toBlock, chunkSize)` and accumulates logs across chunks. Six call sites to refactor.
3. Make `chunkSize` configurable via `Reputation:ChunkSize`, default 10000 (works on every public RPC).
4. **Open question for design:** for a fresh agent with no cache, do we scan back to deploy block, or cap first-scan to last N days (e.g., 30) and treat older history as cold? Top-tier agents (Klark = 4088 jobs) will be slow on first scan even with chunking. Trade correctness for speed?

### B2. `agentReputation` offering source still has v1 contract

**Files:**
- `acp-v2/src/offerings/agentReputation.ts` — entire file
- `acp-v2/src/apiClient.ts` — `agentReputation()` method (verify)

**Symptom:** description, requirement schema, `validate()`, `execute()` all still reference `offeringName` from v1. Description literally says "Pass an optional offeringName to include a per-offering hire-count breakdown" — mixes v2 sub-scores narrative with v1 hire-count narrative.

**No `deliverableSchema` field** is defined in the offering source — so `npm run print-offerings` doesn't emit one, and the marketplace registration's deliverable shape can't be auto-regenerated.

**Fix needed:**
1. Strip `offeringName` from description, requirementSchema, validate, execute.
2. Add `deliverableSchema` matching the v2 C# response: `agentScore` + `subScores{}` (5 sub-dimensions with evidence + percentile) + `rawCounts` + `flags`.
3. Update `apiClient.ts` `agentReputation()` to take `agentAddress` only (drop `offeringName`).
4. Confirm the C# `POST /agentReputation` request DTO doesn't accept `offeringName` either (or silently ignores it — either is fine, but worth noting).

### B3. Marketplace registration on app.virtuals.io is stale

**Symptom:** browsed via `acp_browse_agent` against TheMetaBot wallet `0xecf9773b50f01f3a97b087a6ecdf12a71afc558c` — registered description, requirements, and deliverable for `agentReputation` are all v1. Buyer clients auto-parsing `agentTotalJobs` / `agentPercentile` will crash on the v2 deliverable shape.

**Fix needed (manual, after B2 lands):**
1. Run `cd acp-v2 && npm run print-offerings`.
2. On app.virtuals.io → TheMetaBot agent → Offerings → edit `agentReputation` → paste new description + requirements + deliverable.

---

## Minor (nice-to-have in v1.0.1)

### M1. `BASE_RPC_URL` persistence

`docker compose up -d acp-metabot-api` with an inline env var did NOT recreate the container today (`Container acp-metabot-api Running`). Inline env vars get discarded unless `--force-recreate` is passed. To persist:
- Add `BASE_RPC_URL=…` to `/root/ACP_Metabot/.env` on the droplet (compose auto-loads), OR
- Hardcode in `docker-compose.yml` (less flexible).

Decide canonical RPC: `base.publicnode.com` (free, throttled — fine for warmer running 4 concurrent over 60-min budget?), or Alchemy free tier (300M CU/mo, more headroom, requires signup + API key in env).

### M2. Plugin `acp-find` v0.2.0 not visible to Claude Code marketplace

- Local `acp-find-plugin/.claude-plugin/plugin.json` is at `0.2.0` with the GET fix.
- Claude Code's plugin cache is still on `0.1.7` (uses POST → returns 405 against prod's GET-only endpoint).
- Tagging convention `acp-find--vX.Y.Z` was last followed at v0.1.6; no tag for 0.1.7 or 0.2.0.

**Fix:**
1. `cd acp-find-plugin && git tag -a acp-find--v0.2.0 -m "v0.2.0 — agentReputation behavioural v2" && git push --tags`
2. `/plugin update acp-find` in Claude Code.
3. Verify `mcp__plugin_acp-find_acp-find__acp_health` returns `plugin.version: 0.2.0`.
4. Verify `acp_agent_reputation` against any wallet returns either `not_cached` (cache empty) or v2 JSON shape — no more 405.

(npm `acp-find-mcp@0.2.1` is already live and correct, so non-Claude-Code clients are unblocked.)

---

## Out of scope for v1.0.1 (record so we don't lose them)

- **TheMetaBot doesn't surface in marketplace semantic search.** Likely cause: the on-marketplace `agentReputation` description still talks about hire-counts, so queries about "behavioural / on-chain reputation" don't match. Fixing B3 should help but won't fully resolve — the agent name "TheMetaBot" doesn't match obvious queries like "Metabot" either. May need to add a more discoverable description or category metadata. Track for v1.1.
- **Container restarted at 2026-04-28T09:15:21Z** for unknown reason. Logs were already noisy; didn't dig in. Not blocking v1.0.1.
- **Performance for top-tier agents.** Even with chunking + smart deploy block, scanning Klark (4088 jobs, 8K+ events) on first scan will be slow + rate-limit-prone. v1.2 should consider: pull jobId list from off-chain marketplace API first, then do narrow per-jobId chain reads. For v1.0.1 just make it work; optimize later.
- **Stale legacy `agent_reputation_snapshots` table** from v1 (per old project memory). v2 introduced `agent_reputation_cache` + `agent_lifetime_snapshot`. Decide if v1 table is dropped or left dormant.

---

## Recommended approach next session

1. **Brainstorm** → small focused design, scope = blockers B1/B2/B3 only, optionally M1/M2.
2. **Plan** → step-by-step, TDD where viable for the chunking helper.
3. **Execute** via `superpowers:subagent-driven-development` on a hotfix branch (`hotfix/v1.0.1-chain-scanner`).
4. **Smoke-test** by replaying today's ssh sequence:
   - `KEY=$(docker exec acp-metabot-api printenv INTERNAL_API_KEY)`
   - `docker run --rm --network=container:acp-metabot-api curlimages/curl:latest --max-time 120 -sS -X POST -H "Content-Type: application/json" -H "X-API-Key: $KEY" -d '{"agentAddress":"0xb0aeee6a1be8991ee076f98c17ebac6256e2954f"}' http://localhost:5000/agentReputation`
   - Expect: v2 JSON with `subScores`, `rawCounts`, `flags`. RoysClaw at `0xb0aeee6a1be8991ee076f98c17ebac6256e2954f` (26 jobs) is the small-footprint test agent.
5. **Re-register** on app.virtuals.io (B3).
6. **Final E2E gate:** hire `agentReputation` via `mcp__acp-tester__acp_hire` for $0.05 USDC against any test agent. Validates marketplace path + chain scanner end-to-end.
7. **Tag and ship:** plugin v0.2.0 git tag (M2), `BASE_RPC_URL` persisted (M1), bump bot version to v1.0.1.
