# agentReputation v2 — Behavioural On-Chain Reputation

**Date:** 2026-04-28
**Status:** Approved (design), not yet implemented
**Owner:** Oliver Pringle
**Bot:** ACP_Metabot

## Summary

Replace the existing hire-count-based `agentReputation` offering with a behavioural
on-chain reputation. Same SKU, same price (0.05 USDC), same offering name, but the
score is now derived from real ACP V2 chain activity:

- `last_active_at` (recency)
- `jobs_completed_30d` (volume signal)
- `completion_rate` (quality signal)
- `avg_response_time` (speed signal)
- `dispute_rate` (negative signal)

Cached 24h per agent. New free public endpoint `GET /v1/agentReputation` serves
cached results only — never triggers compute. Paid SKU is the only path to force
a fresh computation on demand.

## Why this change

Hire count alone is a weak quality signal: it's lifetime-cumulative, slow to
respond to recent regressions, and easy for a popular-but-broken agent to game.
Buyers want to know "will this agent screw up my job?", which requires
behavioural data the upstream Strapi feed doesn't expose.

ACP V2 contracts emit indexed `JobCreated`/`JobSubmitted`/`JobCompleted`/
`JobRejected`/`JobExpired` events on Base. Filtering by `provider` topic gives
us per-agent state-machine timelines — enough to compute every metric the brief
asks for at low cost.

## Scope and non-goals

In scope (v1.0):
- Replace existing `ReputationService` with a behavioural-score implementation.
- Internal `POST /agentReputation` endpoint body shape changes (sidecar updates
  in lockstep).
- New public `GET /v1/agentReputation` endpoint (cache-only).
- Daily warmer for top-500 agents by lifetime hires.
- Lazy compute on cache miss for paid hires.
- Inline `/search` reputation summary stays as the cheap hire-count snapshot,
  renamed `reputationLite` and tagged with `lite: true` to disambiguate from
  the deep read.
- Plugin tool description updated; no MCP code change required.

Out of scope (v1.0, captured for v1.1+):
- `agentReputationDeep` recheck SKU at 0.05 USDC that bypasses the 24h cache.
- HMAC-signed score attestations.
- Cross-chain reputation (Base only in v1.0).
- Per-offering behavioural breakdown (only agent-level metrics; per-offering
  hire count is included as supplementary data).

## Architecture

```
                  upstream marketplace API           Base mainnet RPC
                   (acpx.virtuals.io)                  (Alchemy)
                          │                                │
                          ▼                                ▼
              MarketplaceIndexerService            ChainEventScanner
              (existing, 10-min ticks)             (new, on-demand + warmer)
                          │                                │
                          ▼                                │
                       SQLite ◄────────────────────────────┘
                  ┌───────┴────────┬──────────────────┐
                  ▼                ▼                  ▼
        (existing)            ReputationService     ReputationWarmerService
        SearchService         (rewritten;            (new BackgroundService;
        composeStack          consumes cache)        once daily, top-500 agents)
        WatchService                  │
                  │                   │
                  ▼                   ▼
             /search etc.   POST /agentReputation     GET /v1/agentReputation
             (unchanged)    (internal, X-API-Key)    (public, cache-only, rate-limited)
                                       │                       │
                                       ▼                       ▼
                            sidecar agentReputation        consumed by:
                            offering ($0.05 USDC)          - acp-find plugin
                                                           - third-party tools
```

**New components:**

- `ChainEventScanner` (C#, Nethereum) — filters indexed Base ACP V2 contract
  events by `provider` topic, reconstructs the agent's job state machine,
  computes the four chain-derived metrics.
- `AcpOffChainClient` (C#) — single-purpose client for
  `getAgentByWalletAddress` to read `lastActiveAt`. Same Origin/Referer
  auth pattern as the existing `AcpApiMarketplaceSource`.
- `ReputationWarmerService` (C# `BackgroundService`) — daily 02:30 UTC,
  iterates top-500 agents, populates `agent_reputation_cache`.
- `LifetimeSnapshotService` (C# `BackgroundService`) — daily 02:00 UTC,
  snapshots `offerings.agent_job_count` per agent into
  `agent_lifetime_snapshot` for 30-day-delta math.
- `agent_reputation_cache` SQLite table (24h TTL via `computed_at`).
- `agent_lifetime_snapshot` SQLite table (35-day rolling retention).
- Public `GET /v1/agentReputation` route + IP rate limiter.

**Rewritten:**

- `ReputationService` — drops the in-RAM sorted-array hire-count math. New
  job: orchestrate cache lookup → on-miss compute → store. Still owns
  `BuildSearchSummary` for `/search` inline data, which stays hire-count-only.

**Unchanged:** existing indexer, search, composeStack, watchOffering, and
everything in the sidecar except `agentReputation.execute` typings and the
plugin tool description.

## Data pipeline

### Chain event scanner

Library: **Nethereum** (`Nethereum.Web3` + `Nethereum.Contracts`). RPC:
Alchemy Base mainnet via `BASE_RPC_URL` env var (re-uses existing
`ALCHEMY_API_KEY` if available).

Events scanned, filtered by `provider` topic where indexed:

| Event | Indexed filter | Used for |
|---|---|---|
| `JobCreated(jobId, client, provider, evaluator, expiredAt, hook)` | `provider=agent` | enumerate the agent's jobs |
| `JobFunded(jobId, client, amount)` | by jobId set | response-time T0 |
| `JobSubmitted(jobId, provider, deliverable)` | `provider=agent` | response-time T1 |
| `JobCompleted(jobId, reason)` | by jobId set | success counter |
| `JobRejected(jobId, rejector, reason)` | by jobId set | dispute counter |
| `JobExpired(jobId)` | by jobId set | failure counter |

**Per-agent scan strategy:**

1. `eth_getLogs` for `JobCreated` filtered on `topics[3] = pad(agentAddress)`
   over `[CONTRACT_DEPLOY_BLOCK, head]` (or `[last_scanned_block+1, head]` on
   incremental rescans). Returns the agent's full jobId set + per-job
   creation block.
2. From that jobId set, `eth_getLogs` for the other five events filtered on
   `topics[1] ∈ jobIdSet` (chunked into batches of 1000 jobIds × 6 events).
   ~5 RPC calls per agent for an agent with <1000 jobs; ~10–15 for prolific
   agents.
3. Block timestamps fetched via `eth_getBlockByNumber` only for blocks
   involved in response-time math (Funded → Submitted), with an in-process
   LRU cache keyed by block number to avoid re-fetching across agents in the
   same warmer pass.

**Incremental rescan:** subsequent refreshes scan only
`[last_scanned_block+1, head]`. Stored in `agent_reputation_cache.last_scanned_block`.
Drops incremental cost to near-zero.

### Off-chain API client

Single endpoint:
`GET https://acpx.virtuals.io/api/agents/{walletAddress}` returning
`AcpAgentDetail`. We read only `lastActiveAt` from the response. 5-second
timeout, single retry on 5xx. Same Origin/Referer auth pattern as the
existing `AcpApiMarketplaceSource`.

### Strapi snapshot

`LifetimeSnapshotService` runs daily at 02:00 UTC (before the warmer).
Iterates the existing `offerings` table, writes one row per agent into
`agent_lifetime_snapshot(agent_address, snapshot_date, total_jobs)`. Retains
last 35 days; older rows pruned each cycle.

### Failure / partial-data handling

| Failure mode | Behaviour |
|---|---|
| Off-chain API down | `lastActiveAt = null`; recency sub-score uses chain-derived "most recent JobSubmitted block timestamp" as fallback |
| Chain scan partial failure | Cache write skipped; lazy retry on next call. Warmer logs warning, moves to next agent |
| Agent has zero jobs | `insufficientData: true`, all sub-scores neutral 50, score = 50, `flags.isColdStart: true` |
| Snapshot table empty for t-30d slice (boot, < 30 days old) | `volume30d` falls back to chain-derived count of JobCompleted in the 30-day window |

### Cost ceiling

| Source | Daily compute count | Daily RPC | Notes |
|---|---|---|---|
| Warmer (top-500, daily) | 500 | ~3000 calls (incremental) | one per agent on a steady-state day |
| Lazy (paid hires) | ~5–50 | proportional | upper-bounded by SKU revenue |
| Public reads | unbounded but free | 0 | cache-only |

Comfortably under Alchemy free tier (300M CU/month). Lever if exceeded:
shrink top-N from 500 → 200 before paying for a tier upgrade.

## Score formula

Five sub-scores, each in `[0, 100]`. Overall = weighted sum, rounded.

```
overall = round(
  0.30 × completion
+ 0.25 × dispute
+ 0.15 × recency
+ 0.20 × volume30d
+ 0.10 × responseTime
)
```

### Per sub-score

| Sub-score | Raw input | Formula | Insufficient-data trigger |
|---|---|---|---|
| `completion` | `successful / terminal` (terminal = COMPLETED ∪ REJECTED ∪ EXPIRED) | `round(rate × 100)` | < 5 terminal jobs → neutral 50 |
| `dispute` | `(rejected_by_buyer + expired) / terminal`, excluding JobRejected events where `rejector == provider` | `round((1 − rate) × 100)` | < 5 terminal jobs → neutral 50 |
| `recency` | hours since `lastActiveAt` (off-chain) or last JobSubmitted block (fallback) | `100` if ≤ 72h; linear decay to `0` at 90 days; `0` if older | Never insufficient |
| `volume30d` | `jobs_completed_30d` (Strapi delta or chain) | `round(100 × log(1 + n) / log(1 + corpusMax))` | Never insufficient |
| `responseTime` | mean `(JobSubmitted.ts − JobFunded.ts)` over completed jobs in last 30d | `100` if ≤ 5 min; linear decay to `0` at 24h | < 3 completed jobs in 30d → neutral 50 |

### Sub-score response shape

```json
"completion": {
  "value": 0.92,
  "score": 92,
  "percentile": 78,
  "evidence": "47/51 terminal jobs completed (90d window)",
  "insufficientData": false
}
```

### Percentile computation

Done inside `ReputationService` against an in-memory sorted array per metric,
rebuilt at the end of each warmer pass and on every lazy compute. Same pattern
the legacy `ReputationService` already uses for hire counts. Agents with
`insufficientData: true` for a metric are excluded from the corpus arrays for
that metric so they don't dilute the percentile signal.

### Cold-start agent (zero terminal jobs)

`completion`, `dispute`, `volume30d`, and `responseTime` collapse to neutral 50
with `insufficientData: true`. **`recency` is *not* overridden** — it computes
normally from `lastActiveAt` because a fresh agent that registered yesterday
genuinely is "active recently", and that signal still has value.

Worked example for a freshly-registered agent (`lastActiveAt` = 2h ago, zero
jobs):

```
recency      = 100  (active ≤ 72h)
completion   = 50   (insufficientData)
dispute      = 50   (insufficientData)
volume30d    = 50   (insufficientData)
responseTime = 50   (insufficientData)
overall      = round(0.30×50 + 0.25×50 + 0.15×100 + 0.20×50 + 0.10×50) = 58
```

Top-level flags: `{ isColdStart: true, insufficientData: true }`. The
deliverable tells the buyer "fresh agent, no behavioural track record yet,
hire at your own risk."

### Top-level response shape

```json
{
  "agentAddress": "0x...",
  "agentName": "...",
  "agentScore": 78,
  "computedAt": "2026-04-28T03:00:00Z",
  "windowDays": 90,
  "subScores": { "completion": {...}, "dispute": {...}, "recency": {...}, "volume30d": {...}, "responseTime": {...} },
  "rawCounts": {
    "totalJobs": 142,
    "completed": 128,
    "rejected": 8,
    "expired": 6,
    "completedLast30d": 47,
    "lastActiveAt": "2026-04-27T19:14:00Z"
  },
  "flags": { "isColdStart": false, "insufficientData": false, "warmCacheHit": true }
}
```

### `offeringName` filter behaviour

Behavioural metrics are agent-level (chain events don't carry offering names).
When `offeringName` is supplied, the response is identical to the unfiltered
case **plus** an `offering` block sourced from the existing `offerings` table
(Strapi-derived):

```json
"offering": {
  "name": "evaluate_trading",
  "hires": 1240,
  "percentile": 92,
  "evidence": "Per-offering hire count from marketplace metrics. Behavioural metrics above are agent-level — chain events do not carry offering names."
}
```

If the named offering is not owned by the agent, the response is the standard
404 `agent_not_indexed`-style envelope but with `error: "offering_not_found"`.

### Window

All chain-derived metrics use a rolling **90-day window** unless stated. Long
enough to be statistically meaningful, short enough that a reformed agent
isn't penalised forever.

## Storage schema

### Table: `agent_reputation_cache`

```sql
CREATE TABLE IF NOT EXISTS agent_reputation_cache (
  agent_address       TEXT PRIMARY KEY,         -- lowercased 0x... wallet
  agent_name          TEXT NOT NULL,
  agent_score         INTEGER NOT NULL,         -- 0..100
  sub_scores_json     TEXT NOT NULL,
  raw_counts_json     TEXT NOT NULL,
  flags_json          TEXT NOT NULL,
  computed_at         TEXT NOT NULL,            -- ISO-8601 UTC
  last_scanned_block  INTEGER NOT NULL,         -- highest Base block included
  source              TEXT NOT NULL             -- 'warmer' | 'lazy'
);

CREATE INDEX IF NOT EXISTS idx_reputation_cache_computed_at
  ON agent_reputation_cache(computed_at);
```

A row > 24h old is treated as a cache miss (not deleted, just shadowed). Warmer
overwrites; lazy compute does an `INSERT OR REPLACE`.

### Table: `agent_lifetime_snapshot`

```sql
CREATE TABLE IF NOT EXISTS agent_lifetime_snapshot (
  agent_address    TEXT NOT NULL,
  snapshot_date    TEXT NOT NULL,               -- 'YYYY-MM-DD' UTC
  total_jobs       INTEGER NOT NULL,
  PRIMARY KEY (agent_address, snapshot_date)
);

CREATE INDEX IF NOT EXISTS idx_snapshot_date
  ON agent_lifetime_snapshot(snapshot_date);
```

35-day rolling retention. ~5K agents × 35 days × ~50 bytes ≈ 8 MB worst case.

### Existing tables

No schema changes to `offerings`, `offering_embeddings`, `watches`, `watch_seen`.

### Db.cs

Append both new `CREATE TABLE` calls inside `EnsureCreatedAsync()`. SQLite
no-ops if tables exist; safe on every startup.

### New repositories

- `AgentReputationCacheRepository.cs`
  - `GetAsync(string agentAddress) → CachedReputation?` (null if missing or > 24h old)
  - `UpsertAsync(CachedReputation row)`
  - `ListWarmAgentsAsync(int topN) → IReadOnlyList<string>` (joins against `offerings.agent_job_count` desc)
  - `ListAllForPercentilesAsync() → IReadOnlyList<CachedReputation>` (feeds in-memory percentile arrays)

- `LifetimeSnapshotRepository.cs`
  - `UpsertAsync(string agentAddress, DateTime date, long totalJobs)`
  - `GetAsync(string agentAddress, DateTime date) → long?`
  - `PruneOlderThanAsync(DateTime cutoff)`

### Migration

The legacy `ReputationService` only reads from `offerings`; it has no
persistent state of its own. No data migration. Sidecar typings and plugin
tool description update in lockstep with the new server response shape.

## Refresh strategy

### Cache lookup — `ReputationService.GetOrComputeAsync(agentAddress, source)`

```
1. row = AgentReputationCacheRepository.GetAsync(addr)         // null if > 24h old
2. if row != null:
       return row + flags.warmCacheHit = (row.source == 'warmer')
3. else:
       acquired = TryAcquireComputeLock(addr)
       if !acquired:
           wait up to 5s for cache row to land, then re-read
           if row != null: return it
           else: throw 503 "concurrent compute timed out"
       try:
           result = ComputeAsync(addr)                          // chain scan + off-chain fetch
           UpsertAsync(result, source: 'lazy')
           return result
       finally:
           ReleaseComputeLock(addr)
```

### Compute lock

In-process `ConcurrentDictionary<string, SemaphoreSlim>` keyed by lowercased
agent address. Prevents two simultaneous paid hires for the same agent
triggering two chain scans. Single-process API container — no distributed
lock needed for v1.0.

### Lazy trigger

Only paid `agentReputation` hires trigger compute. Public
`/v1/agentReputation` is strictly cache-only; returns 404 + hint on miss.

### Warmer — `ReputationWarmerService` (BackgroundService)

```
schedule:    daily at 02:30 UTC
selection:   top 500 agents by offerings.agent_job_count desc, distinct
concurrency: 4 agents in flight (Parallel.ForEachAsync with degree=4)
budget cap:  hard stop after 60 minutes; remaining agents picked up next day
on each agent:
  - call ComputeAsync (incremental scan from last_scanned_block + 1)
  - UpsertAsync with source='warmer'
  - log progress every 50 agents
post-pass:
  - ReputationService.RebuildPercentilesFromCache()
  - LifetimeSnapshotRepository.PruneOlderThanAsync(today - 35d)
```

Schedule rationale:
- Strapi snapshot at 02:00 UTC is ready in time for the 02:30 warmer.
- Off Asia / off US peak — best RPC latency window.
- No collision with existing background services (indexer ticks every 10
  min, watch poller every 30 min — neither aligns precisely with 02:30).

### TTL semantics

Single source of truth: `computed_at`. Anything older than 24h is shadowed
regardless of `source`. Warmer pass at 02:30 covers paid + free traffic for
the next ~24h for top-500 agents; lazy compute on long-tail agents lasts 24h
from compute time.

### First-boot behaviour

- Indexer must complete one cycle before warmer runs (warmer needs
  `offerings.agent_job_count`). Warmer waits for `IndexerHealth.IsReady`;
  otherwise sleeps 60s and retries.
- Snapshot table empty for 30 days post-deploy → `volume30d` falls back to
  chain-derived count.
- Cache empty → public `/v1/agentReputation` returns 404 for any address
  until the first warmer pass completes (or a paid hire warms a row).

### Force-refresh path

Not in v1.0. v1.1 candidate: `agentReputationDeep` sibling SKU at 0.05 USDC
that bypasses the 24h cache, marketed at agent authors who shipped a fix.

## API surface

### Internal — `POST /agentReputation`

Auth: `X-API-Key`. Body:

```json
{ "agentAddress": "0x...", "offeringName": "optional string" }
```

Responses:

- **200** — top-level response shape from "Score formula" section.
- **404** `{ "error": "agent_not_indexed", "message": "agent has no offerings on the marketplace" }` — sidecar maps to job-message rejection (not a deliverable).
- **503** `{ "error": "compute_failed", "message": "..." }` — sidecar retries via 1s/4s backoff; if still failing, sends `unsupported: reputation service degraded, refunds will be issued` and the buyer is not charged.

### Public — `GET /v1/agentReputation?agent=<address>`

No auth. Cache-only.

```
addr = lower(query.agent), validate as hex 0x + 40
row = cache.GetAsync(addr)            // includes 24h freshness check
if row == null:
    return 404 { "error": "not_cached", "hint": "hire the agentReputation offering for live computation" }
return 200 row + ETag header
```

Headers:

```
Cache-Control: public, max-age=3600
ETag: "<sha1 of computed_at>"
```

CDN-friendly: `max-age=3600` means a downstream cache only re-asks us every
hour, even though `computed_at` can be up to 24h old.

### Rate limits (public endpoint only)

`Microsoft.AspNetCore.RateLimiting`:

| Limit | Bucket | Quota |
|---|---|---|
| Per-IP global | sliding window | 60 req/min |
| Per-IP unique-agent | fixed window | 200 distinct agents/hour |

The unique-agent limit discourages scraping; legitimate consumers use the
paid SKU for breadth.

### Sidecar — `agentReputation` offering

Requirement schema unchanged (`agentAddress` required, `offeringName`
optional). `apiClient.agentReputation()` typings update in lockstep with the
new response shape. Validators unchanged. Pricing unchanged at 0.05 USDC.
Description text updated:

> *"On-chain behavioural reputation for an ACP agent. Returns a 0–100 score
> derived from completion rate, dispute rate, recency, 30-day throughput,
> and average response time. Cached 24h per agent. Pass an optional
> offeringName to include a per-offering hire-count breakdown alongside the
> agent-level score."*

20-char marketplace name limit: `agentReputation` = 15 chars. Fits.

### Plugin — `acp_agent_reputation` MCP tool

The MCP tool just relays the public endpoint's JSON. New response fields
appear automatically without typing changes (the tool returns the response
body verbatim). The only change is a description-string update in
`acp-find-plugin/mcp-server/server.js` to mention behavioural metrics and
cache-only behaviour ("returns 404 if not yet evaluated; use the paid
agentReputation offering to force live computation"). Single-line text edit;
no logic change.

### Surface summary

| Surface | Method | Path | Auth | Compute | Cost |
|---|---|---|---|---|---|
| Internal | POST | `/agentReputation` | X-API-Key | lazy on miss | n/a |
| Public | GET | `/v1/agentReputation?agent=` | none | never | free, rate-limited |
| Paid SKU | ACP job | `agentReputation` offering | ACP escrow | lazy on miss | $0.05 USDC |
| Plugin tool | MCP | `acp_agent_reputation` | none | never (proxies public GET) | free |

### Stable error envelope

Same `{ error, message }` shape across surfaces, mapped to HTTP status:

- `400 invalid_address` — malformed wallet
- `404 not_cached` (public only) / `404 agent_not_indexed` (paid only) / `404 offering_not_found` (when `offeringName` supplied and not owned by agent)
- `429 rate_limited` (public only)
- `503 compute_failed` (paid only)

## Inline `/search` reputation

Existing search results carry a per-hit reputation block. Behavioural compute
is too expensive to fan out per search hit (100 RPC calls × 100 results), so
the inline block stays as the cheap hire-count snapshot. Renamed
`reputationLite` and tagged with `lite: true` in the JSON to disambiguate
from the deep read. `BuildSearchSummary` in `ReputationService` retains its
existing implementation.

Buyers who want behavioural data on a `/search` hit pay 0.05 USDC for the
`agentReputation` offering with the agent address — clean tier ladder, real
cost differential reflected in the price differential.

## Configuration / env vars

New:

- `BASE_RPC_URL` — Alchemy or other Base mainnet RPC endpoint.
- `ACP_CONTRACT_ADDRESS_BASE` — ACP V2 contract address on Base mainnet
  (already known constant; could also be hard-coded).
- `REPUTATION_WARMER_TOPN` — default 500.
- `REPUTATION_WARMER_BUDGET_MINUTES` — default 60.

Re-used:

- `INTERNAL_API_KEY` (existing).
- `ANTHROPIC_API_KEY`, `VOYAGE_API_KEY` (unrelated to this feature).

## Security posture

- Public endpoint is rate-limited and never triggers compute → no DoS vector.
- Compute lock prevents duplicate scans on the paid path.
- All address inputs lowercased + validated `^0x[0-9a-f]{40}$` before any
  query.
- Off-chain API call uses existing Origin/Referer pattern; no secrets shipped.
- Chain RPC key is read-only; no funds at risk if leaked.

## Observability

- Structured logs at INFO from `ReputationWarmerService` (per-agent timing,
  pass summary).
- WARN on partial compute failure with agent address + RPC error.
- Counter metrics: `reputation_compute_total{source}`, `reputation_cache_hit_total`,
  `reputation_cache_miss_total`, `reputation_public_404_total`. Surfaced via
  `/metrics` if/when Prometheus is added (not v1.0).
- `/health` includes `lastWarmerRunAt` and `cacheRowCount` in v1.0.

## Phasing

| Version | Scope |
|---|---|
| **v1.0** (this spec) | Behavioural score, daily warmer top-500, lazy paid compute, public cache-only endpoint, plugin tool description update |
| **v1.1** | `agentReputationDeep` recheck SKU at 0.05 USDC |
| **v1.2** | HMAC-signed score attestations |
| **v2.0** | Solana support; Hyperliquid if it ships ACP |

## Test surface (deferred to writing-plans)

The implementation plan should cover:

- Chain scanner integration test using a recorded fixture of `eth_getLogs`
  responses for a known agent.
- Warmer dry-run smoke test (top-3 agents, full pipeline).
- Public endpoint cache-hit / cache-miss / rate-limit / invalid-address paths.
- Sidecar offering smoke test against AgentEval-style fixture.
- Cold-start agent (zero jobs) path.
- Insufficient-data agent (< 5 terminal jobs) path.

## Open items

None at design-approval time. Implementation plan will resolve concrete
choices like Nethereum version, RPC retry policy specifics, and exact
Prometheus metric names.

## References

- Existing `ReputationService.cs` — to be rewritten.
- ACP V2 ABI events: `acp-v2/node_modules/@virtuals-protocol/acp-node-v2/dist/core/acpAbi.d.ts`.
- Off-chain API agent detail: `AcpAgentDetail` in `acp-v2/node_modules/@virtuals-protocol/acp-node-v2/dist/events/types.d.ts`.
- Sister-bot design pattern: `../../ACP_AgentEval/ACP_AgentEval/docs/design.md`.
