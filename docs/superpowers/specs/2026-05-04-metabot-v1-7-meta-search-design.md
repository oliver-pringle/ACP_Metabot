# Meta-Search Enhancement — TheMetaBot v1.7 design

**Date:** 2026-05-04
**Status:** Approved (awaiting implementation plan)
**Owner:** Oliver Pringle
**Bot:** ACP_Metabot

## Summary

TheMetaBot's offering search works well; the gap is at the *meta* layer.
Buyer LLMs asking "find me an agent who does wallet intel" hit the existing
`/v1/searchAgents` endpoint (shipped in v1.6) but it ranks by BM25 only —
no embeddings, no rerank — so semantic intent collapses to keyword overlap.
There is no per-result signal for whether an offering is in a crowded niche
or how its price compares to peers. `/v1/agent/{address}` returns V1 + V2
offerings as one merged list with no cross-marketplace footprint summary.
And `/v1/digest`, while it returns new offerings and gainers, is missing
agent inflow, churn, cohort retention, and per-category saturation.

v1.7 fills the meta-search gap. Five additions, all backward-compatible:

1. **Hybrid agent search.** Upgrade existing `/v1/searchAgents` (today:
   BM25 group-by) to the same hybrid pipeline `/v1/search` already uses —
   FTS5 BM25 + dense via a new `agent_profiles` table + RRF + Voyage
   rerank. Same endpoint, same MCP tool (`acp_search_agents`), upgraded
   ranker. Existing `AgentSearchHit` shape is extended additively
   (`marketplaces`, `dominantMarketplace`, `agentScore`, richer
   `topOfferings`).
2. **V1 ↔ V2 cross-presence on `/v1/agent/{address}`.** New
   `crossPresence` block: per-marketplace footprint with `inBoth` and
   `dominant` fields.
3. **Saturation flag on `/v1/search` offering hits.** Per-offering
   `nearDuplicateCount` + `categorySize`, computed from already-loaded
   embeddings.
4. **Pricing percentile on `/v1/search` offering hits and
   `/v1/agent/{addr}`.** Value within `(category × marketplace)` with
   `peerN` and `lowN` flag.
5. **`/v1/digest` becomes the marketplace pulse tool.** Existing fields
   preserved (`newOfferings`, `gainers`, etc.). Extended with: `newAgents`,
   `churnRate`, `cohortSurvival`, `saturationMap`, `windowStart`,
   `partial`. The existing `days` parameter cap extends from 30 → 90 so
   cohort survival can show 12 weeks back.

No new REST endpoints in v1.7. The work threads through three existing
endpoints (`/v1/searchAgents`, `/v1/agent/{address}`, `/v1/digest`) plus
additive fields on `/v1/search`. MCP tool shapes get additive extensions;
slash commands get prose updates and (for `/acp-find:today`) one new
optional argument.

## Why

- `/v1/searchAgents` BM25-only is a stop-gap. It catches keyword matches
  but loses on synonyms, paraphrase, and intent ("an agent who watches
  whale wallets" misses an agent whose offerings describe "tracking large
  on-chain holders"). Adding embeddings + rerank brings agent search to
  parity with offering search quality.
- Offering hits today don't expose competitive context. `saturation` and
  `pricePercentile` turn `/v1/search` results into something a buyer LLM
  can reason about commercially without an extra round-trip.
- `/v1/agent/{address}` returns merged V1+V2 offerings with no summary —
  buyers can't tell whether an agent is V1-native, V2-native, or
  multi-marketplace. `crossPresence` makes that explicit.
- `/v1/digest` already surfaces what's new and what's growing. Buyers
  also want pulse signals: agent inflow, churn, retention, saturation
  pressure. The data is in the indexer; the digest is the natural
  aggregator.
- The vNext UX bet: a buyer LLM asking marketplace-level questions
  ("what's the pulse of wallet intel this month?", "is this agent
  multi-marketplace?", "is this offering in a crowded niche?") should
  get the answer in one tool call, not three.

## Already shipped in v1.6 (built on)

This design layers on top of the v1.6 release (commit `5b71d08`,
"feat(v1.6): 4 new public endpoints + V1/V2 split + filter parity")
and the post-v1.6 metrics commits (`9523e4a`, `78f868c`, `e71a9ee`,
`61c9822`).

Concretely, v1.7 builds on the following v1.6 surface:

- **`POST /v1/searchAgents`** — agent-level BM25 group-by. Returns
  `AgentSearchHit { agentAddress, agentName, score, totalOfferings,
  topOfferings (string[]), totalJobs }`. v1.7 swaps the ranker, extends
  the hit shape additively. Endpoint URL and MCP tool name unchanged.
- **`GET /v1/recentHires`** — top offerings by absolute hire-count Δ.
  Unchanged in v1.7. The `gainers` array on `/v1/digest` is the same
  data viewed through the digest lens; both surfaces stay in sync via
  `OfferingRepository.ListGainersAsync`.
- **`GET /v1/digest`** — currently returns `newOfferings` + `gainers`,
  with `days` (1–30), `marketplace`, `chain[]`, `priceMaxUsdc` filters.
  v1.7 keeps all existing fields; cap on `days` extends to 90; six new
  fields added.
- **`GET /v1/categories`** — already returns `offeringCount` per
  category (live from corpus). v1.7's `saturationMap` is the natural
  per-category extension.
- **`GET /v1/agent/{address}`** — returns offerings + reputation aggregate.
  v1.7 adds `crossPresence` block and per-offering `pricePercentile`.
- **`POST /v1/search`** — existing hybrid offering search with `chain[]`,
  `minReputation`, `freshness`, `priceMaxUsdc`, `category`, `offset`
  filters. v1.7 adds `saturation` and `pricePercentile` to each
  offering hit (no new request params).

## Scope and non-goals

In scope (this spec):

- **Schema:** new `agent_profiles` table, new `agent_profiles_fts` FTS5
  external-content table with sync triggers, new `agent_profiles_dirty`
  partial index.
- **Embedding service:** new `AgentProfileEmbedderService` (background
  hosted service) on the existing indexer cycle. Cold-start full
  re-embed; steady-state dirty-flag re-embed.
- **Agent search ranker upgrade:** new `AgentSearchService` (or
  extension on `SearchService`) implementing the BM25 + dense + RRF +
  rerank pipeline against `agent_profiles_fts` + `agent_profiles.embedding`.
  `OfferingRepository.SearchAgentsAsync` (the v1.6 BM25-only path) is
  retained as the BM25 leg of the new hybrid; the agent-grouping logic
  it owns is reused for surfacing top offerings per agent.
- **Per-result enrichments:** new `SaturationCalculator` and
  `PricePercentileCalculator` operating on `SearchService._corpus`.
- **Pulse digest extensions:** `DigestService.BuildAsync` extended with
  six new fields (`newAgents`, `churnRate`, `cohortSurvival`,
  `saturationMap`, `windowStart`, `partial`). `days` cap extended from
  30 → 90. Hourly in-memory cache keyed on `(days, marketplace,
  chain-set, priceMaxUsdc, hourBucket)`.
- **`/v1/agent/{address}` cross-presence:** new
  `BuildCrossPresenceAsync(address)` in the `browseAgent` service path,
  reusing the existing offerings-by-agent query and grouping by
  `marketplace_version`.
- **Wire surfaces:** `/v1/searchAgents`, `/v1/search`, `/v1/agent/{address}`,
  `/v1/digest` extended additively. No new endpoints.
- **Sidecar:** `apiClient.ts` types extended; `find`, `acp_search_agents`,
  `acp_browse_agent`, `acp_today` deliverable schemas extended.
- **Plugin:** `/acp-find:search`, `/acp-find:search-agents`,
  `/acp-find:agent`, `/acp-find:today` skill prose / args updated.
- **MCP server (`acp-find-mcp`):** tool schemas extended; bumped to 0.7.0
  and republished to npm.
- **Documentation:** lockstep update across ACP_Metabot README +
  user-guide + technical-specifications + design.md, acp-find-plugin
  README + skills, all relevant slash commands, mcp-server README.
- **Tests:** unit tests for new services, integration tests for new
  wire shapes, smoke against droplet after deploy.

Out of scope (deferred to v1.8 or later):

- **Per-marketplace job-count split on cross-presence.** V1 jobs come
  from `acpx.virtuals.io/api/metrics/skills`; V2 jobs come from on-chain
  `JobCreated` events on Base via `ChainEventScanner`. Reconciling them
  into a per-marketplace count requires its own design pass (chain
  selection, dedup, identity mapping for V1 sellers who later upgraded
  to V2). v1.7 ships `crossPresence` with `offeringCount` per
  marketplace only; the existing reputation block stays cross-version.
- **V1 chain enumeration.** V1 has no equivalent to V2's
  `v2_known_sellers` table. v1.7 accepts that V1 first-seen is
  offering-derived (`MIN(offerings.first_seen_at)` per agent). V1 agents
  with zero offerings remain invisible — they probably don't exist in
  practice.
- **Saturation cluster details on `/v1/digest`.** Top-K duplicate
  clusters per category would be useful but adds payload weight for
  limited extra signal vs. a percentage. `saturationMap` ships as
  `(category, total, saturatedCount, saturationPct)` only.
- **Reputation-weighted leaderboards per category.** Useful but separate
  from meta-search.
- **First-mover badge** (agents who entered a niche before it crowded).
  Same reasoning.
- **Cohort survival > 12 weeks back.** Hard cap at 12 weeks regardless
  of `days` value, to keep payload bounded.
- **Custom embedding model per agent.** v1.7 reuses the existing model
  configured for `offering_embeddings` (Voyage; same dimension, same
  provider). No `model` parameter on the agent search.
- **Unified `/v1/search` with a `kind` param.** Kept as two endpoints —
  `/v1/search` for offerings, `/v1/searchAgents` for agents — matching
  the v1.6 surface. A unified entry point can be evaluated separately
  if buyer LLMs struggle to pick.

## Architecture deltas

```
                    upstream V1 (acpx.virtuals.io)
                    upstream V2 (api.acp.virtuals.io + Base chain JobCreated)
                                 │
                                 ▼
                  MarketplaceIndexerService (existing, ~30-min cycle)
                                 │
                                 ▼
   ┌─────────────────────────────┴──────────────────────────────┐
   │                            SQLite                          │
   │  offerings ◄─ triggers ─► offerings_fts                    │
   │  offering_embeddings                                       │
   │  v2_known_sellers                                          │
   │  agent_reputation_cache / _history / _snapshots            │
   │  agent_lifetime_snapshot                                   │
   │  agent_profiles (NEW) ◄─ triggers ─► agent_profiles_fts    │
   │                                       (NEW)                │
   └────────────────────────────────────────────────────────────┘
                                 │
   ┌──────────────────────┬──────┴───────┬───────────────────────────┐
   ▼                      ▼              ▼                           ▼
 SearchService    AgentProfileEmbedder  browseAgent path     DigestService
 (offering hybrid; (NEW, hosted; dirty- (cross-presence,     (extended: pulse
  saturation +     queue draining;       per-offering         fields, days
  percentile       Voyage batch embed)   pricePercentile)     cap 30→90)
  enrichments)    AgentSearchService
                  (NEW; hybrid BM25 +
                   dense + rerank
                   against agent
                   corpus)
   │                  │              │                           │
   ▼                  ▼              ▼                           ▼
 POST /v1/search   POST /v1/        GET /v1/agent/{addr}    GET /v1/digest
 (existing +       searchAgents    (existing + crossPresence  (existing +
  saturation +     (existing URL,   + per-offering              new pulse
  pricePercentile  upgraded ranker)  pricePercentile)           fields)
  per hit)
```

## Schema (additions in `Db.cs`)

```sql
-- agent profile corpus and embedding.
-- Surrogate INTEGER PK (matching the offerings pattern): TEXT PK tables get an
-- implicit rowid that is not VACUUM-stable, which would silently desynchronise
-- the FTS5 external-content mirror. agent_address is UNIQUE for the
-- repository-level lookup primitive. agent_address values are stored
-- lowercased.
CREATE TABLE IF NOT EXISTS agent_profiles (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    agent_address       TEXT    NOT NULL UNIQUE,         -- lowercased
    agent_name          TEXT    NOT NULL,
    profile_text        TEXT    NOT NULL,                -- concatenation, retained for re-embed + debug
    embedding           BLOB,                            -- matches offering_embeddings dimension/model
    embedding_model     TEXT,                            -- mirrors the value used by offering_embeddings.model
    embedded_at         TEXT,                            -- ISO 8601 UTC; null until first embed
    last_change_at      TEXT    NOT NULL                 -- ISO 8601 UTC; bumped when offering set changes
);
CREATE INDEX IF NOT EXISTS ix_agent_profiles_dirty
    ON agent_profiles(last_change_at)
    WHERE embedded_at IS NULL OR last_change_at > embedded_at;

-- FTS5 mirror over agent_name + profile_text. content_rowid='id' so the FTS
-- index references the stable surrogate, not the implicit rowid.
CREATE VIRTUAL TABLE IF NOT EXISTS agent_profiles_fts USING fts5(
    agent_name, profile_text,
    content='agent_profiles', content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);
CREATE TRIGGER IF NOT EXISTS agent_profiles_ai AFTER INSERT ON agent_profiles BEGIN
    INSERT INTO agent_profiles_fts(rowid, agent_name, profile_text)
    VALUES (new.id, new.agent_name, new.profile_text);
END;
CREATE TRIGGER IF NOT EXISTS agent_profiles_ad AFTER DELETE ON agent_profiles BEGIN
    INSERT INTO agent_profiles_fts(agent_profiles_fts, rowid, agent_name, profile_text)
    VALUES ('delete', old.id, old.agent_name, old.profile_text);
END;
-- AFTER UPDATE OF column-scoped (same pattern as offerings_au, v1.2 lesson):
-- the indexer-side last_change_at / embedded_at writes must NOT fire the FTS
-- rebuild.
CREATE TRIGGER IF NOT EXISTS agent_profiles_au
AFTER UPDATE OF agent_name, profile_text ON agent_profiles BEGIN
    INSERT INTO agent_profiles_fts(agent_profiles_fts, rowid, agent_name, profile_text)
    VALUES ('delete', old.id, old.agent_name, old.profile_text);
    INSERT INTO agent_profiles_fts(rowid, agent_name, profile_text)
    VALUES (new.id, new.agent_name, new.profile_text);
END;
```

`last_change_at` is bumped by the existing offering ingest path when an
offering is inserted, updated, or tombstoned for a given `agent_address`.
Bumping is wrapped into the same transaction as the offering write so
embed visibility is consistent with offering visibility. Cold-start
backfill runs once per boot — populates `agent_profiles` for every
distinct `agent_address` in `offerings` with `last_change_at = now()`
and `embedded_at = null`. The embedder service then drains the dirty
queue.

## Wire surfaces

### `POST /v1/searchAgents` (upgraded ranker, additive fields)

Request body unchanged from v1.6:

```jsonc
{
  "query": "string",
  "limit": 10,                        // existing, default 10, max 50
  "marketplace": "v1" | "v2"          // existing, optional
}
```

Response: `AgentSearchHit` records, additively extended:

```jsonc
{
  "agents": [
    {
      "agentAddress": "0x...",
      "agentName": "WalletIntelBot",
      "score": 0.82,                   // CHANGED — was BM25 (lower=better);
                                       //  now post-rerank score (higher=better,
                                       //  0–1 range; opaque ordering value)
      "totalOfferings": 5,
      "topOfferings": [                // CHANGED — was string[]; now records:
        { "offeringName": "wallet_scan", "priceUsdc": 0.10, "marketplaceVersion": "v2" }
      ],
      "totalJobs": 47,
      "marketplaces": ["v1", "v2"],    // NEW — sorted; subset of {"v1","v2"}
      "dominantMarketplace": "v2",     // NEW — by offeringCount; tiebreak by totalJobs
      "agentScore": 78                 // NEW — from agent_reputation_cache
                                       //  (cache-only read; nullable)
    }
  ]
}
```

Two changes to existing fields are technically breaking for strict
schema consumers:

- `score` semantics flip (BM25 lower=better → cosine higher=better) and
  the value range changes. The field is opaque (callers should sort
  by it, not interpret it), so this is low-risk in practice.
- `topOfferings` element type changes from `string` to a record. To
  preserve the array shape for old-string consumers, the response also
  carries `topOfferingNames: string[]` mirroring `topOfferings[].offeringName`.
  Strict consumers can migrate to `topOfferings`; loose consumers using
  the old string list keep working via the new sibling field.

The MCP server (`acp-find-mcp`) bumps to 0.7.0 to signal the contract
change. Existing clients running 0.6.x continue to work against the
upgraded endpoint via `topOfferingNames`; they just lose access to the
new fields.

### `POST /v1/search` (additive per-hit fields)

Request body **unchanged**.

Response `results[]` gains two fields per hit:

```jsonc
{
  "results": [
    {
      // ... existing fields preserved (offeringId, agentAddress, agentName,
      //     offeringName, description, priceUsdc, marketplaceVersion,
      //     category, ...) ...
      "saturation": {
        "nearDuplicateCount": 4,           // # other offerings in same category, cosine ≥ threshold
        "categorySize": 27
      },
      "pricePercentile": {
        "value": 78,                       // 0–100; null when lowN
        "peerN": 23,                       // peers in (category × marketplace)
        "lowN": false                      // true when peerN < 5
      }
    }
  ]
}
```

Both fields appear regardless of caller params. Existing JSON consumers
ignoring unknown fields are unaffected.

### `GET /v1/agent/{address}` (browseAgent)

Response gains a `crossPresence` block and per-offering `pricePercentile`:

```jsonc
{
  "agentAddress": "0x...",
  "agentName": "WalletIntelBot",
  // ... existing fields preserved ...
  "crossPresence": {
    "v1": {
      "offeringCount": 3,
      "firstSeenAt": "2026-01-12T08:14:00Z",
      "lastSeenAt": "2026-04-30T11:02:00Z"
    },
    "v2": {
      "offeringCount": 2,
      "firstSeenAt": "2026-03-04T15:23:00Z",
      "lastSeenAt": "2026-05-03T09:48:00Z"
    },
    "inBoth": true,
    "dominant": "v1"                       // by offeringCount; tiebreak by total job count
  },
  "offerings": [
    {
      // ... existing offering fields preserved ...
      "pricePercentile": { "value": 78, "peerN": 23, "lowN": false }
    }
  ]
}
```

When the agent is single-marketplace, `crossPresence.v1` or
`crossPresence.v2` is `null` and `inBoth` is `false`. Existing reputation
block is unchanged (remains cross-version aggregate).

### `GET /v1/digest` (extended digest / pulse)

Existing query string preserved (`days`, `marketplace`, `chain[]`,
`priceMaxUsdc`). One change: `days` cap extends from 30 → 90.

Existing response fields preserved unchanged: `newOfferings`, `gainers`,
plus whatever else `DigestResult` already carries. Six new fields added:

```jsonc
{
  // ... existing fields preserved (newOfferings, gainers, ...) ...

  "windowStart": "2026-04-04T00:00:00Z",  // NEW — UTC, days-ago boundary
  "partial": false,                        // NEW — true when backing data spans <window

  "newAgents": {                           // NEW
    "count": 47,
    "agents": [                            // top-10 by firstSeenAt desc
      {
        "address": "0x...",
        "name": "...",
        "marketplace": "v2",
        "firstSeenAt": "2026-04-29T08:14:00Z",
        "offeringCount": 1
      }
    ]
  },

  "churnRate": {                           // NEW
    "rate": 0.04,
    "churnedCount": 14,
    "baselineCount": 350
  },

  "cohortSurvival": [                      // NEW; null when days < 30; up to 12 weeks
    {
      "cohortWeek": "2026-W12",
      "cohortStart": "2026-03-16T00:00:00Z",
      "cohortSize": 17,
      "surviving": 15,
      "survivalRate": 0.88
    }
  ],

  "saturationMap": [                       // NEW; one entry per category with ≥1 offering
    {
      "category": "wallet_intelligence",
      "total": 27,
      "saturatedCount": 11,
      "saturationPct": 0.41
    }
  ]
}
```

Definitions (authoritative):

- **New agent** = agent whose `MIN(offerings.first_seen_at)` (across V1+V2,
  filtered by request `marketplace`/`chain`/`priceMaxUsdc` if set) falls
  within `[windowStart, now]`. V2 wallets in `v2_known_sellers` with no
  offerings yet are not counted.
- **Churn baseline** = agents with ≥1 non-tombstoned offering at
  `windowStart` (after request filters).
- **Churned** = baseline agents with 0 non-tombstoned offerings now
  (after request filters).
- **Cohort** = agents whose `MIN(offerings.first_seen_at)` falls in
  calendar ISO week W. Buckets are calendar-week-aligned UTC.
- **Surviving** = agent has ≥1 non-tombstoned offering today **OR** has
  had ≥1 job in the last 30 days (`agent_lifetime_snapshot.total_jobs`
  increased in last 30 days).
- **Saturation per category** = `count(offerings where
  near_duplicate_count > 0 in category) / count(offerings in category)`.
  `near_duplicate_count` uses the same `SATURATION_THRESHOLD` (default
  cosine ≥ 0.85, env-tunable) as the per-hit field on `/v1/search`.

Backward compatibility:

- All existing query params and response fields preserved unchanged.
  Existing callers see the new fields; ignoring them is safe.
- `cohortSurvival` is `null` when `days < 30` (insufficient cohort
  width).
- `partial: true` is set whenever any of the new fields had insufficient
  backing data to fill (e.g. agent_reputation_snapshots from `windowStart`
  missing because deploy is younger than the window).

## Sidecar (`acp-v2/`)

`apiClient.ts` gains:

- Extended `AgentSearchHit` type (new fields + `topOfferings` shape
  change + `topOfferingNames` mirror).
- New `OfferingHitSaturation` and `OfferingHitPercentile` type
  definitions; existing offering hit types extended to include them.
- `browseAgent` response type extended with `crossPresence` and
  per-offering `pricePercentile`.
- `digest` response type extended with the six new fields above.

Offering deliverables (in the sidecar's seller registry):

- **`acp_search_agents`** — schema gains the new `AgentSearchHit` fields.
  Description updated to mention the hybrid ranker.
- **`acp_find`** — schema gains `saturation` + `pricePercentile` per hit.
- **`acp_browse_agent`** — schema gains `crossPresence` + per-offering
  `pricePercentile`.
- **`acp_today`** — schema gains the six new digest fields. Param
  `days` cap raised to 90.

Pricing unchanged. Free tools stay free; paid tools stay paid. No new
offerings registered with the marketplace in v1.7.

## Plugin (`acp-find-plugin/`) and MCP server (`acp-find-mcp`)

MCP tool schemas mirror the sidecar deliverables. Tool name conventions
unchanged (`acp_find`, `acp_search_agents`, `acp_browse_agent`,
`acp_today`).

Slash command updates:

- `/acp-find:search` — skill prose updated to describe the new
  per-hit `saturation` and `pricePercentile` fields and how to read
  them. No new arg.
- `/acp-find:search-agents` — skill prose updated to mention the
  hybrid ranker and the new fields (`marketplaces`, `dominantMarketplace`,
  `agentScore`). No new arg.
- `/acp-find:agent` — skill prose updated to mention `crossPresence`
  and per-offering percentile.
- `/acp-find:today` — skill prose updated to describe the new pulse
  fields (inflow, churn, cohort survival, saturation map). New optional
  arg `days` (1–90; default 1) — backward compat with existing
  invocations without the arg.

Versioning:

- `acp-find-plugin` → 0.7.0
- `acp-find-mcp` (npm) → 0.7.0 (republished, requires WebAuthn TTY)
- `ACP_Metabot` semantic version → v1.7.0

## New / changed services (`Metabot.Api/Services/`)

- **`AgentProfileEmbedderService`** (NEW) — `IHostedService`. Drains the
  dirty queue on each indexer cycle. Voyage batch embedding (Voyage
  supports batch up to 128). Failure mode: rate-limit hit → log + retry
  next cycle. Cold-start: full corpus rebuild; budgeted to complete
  within ~10 min on a fresh deploy.
- **`AgentSearchService`** (NEW; or extension on `SearchService`) — the
  hybrid agent ranker. BM25 leg reuses
  `OfferingRepository.SearchAgentsAsync` (the v1.6 grouping query) so
  the agent-grouping logic is not duplicated. Dense leg reads
  `agent_profiles.embedding`; RRF fusion (k=60); Voyage rerank top-50.
  Replaces the BM25-only path inside the `/v1/searchAgents` handler.
- **`SaturationCalculator`** (NEW) — operates on
  `SearchService._corpus`. Computes per-offering `nearDuplicateCount`
  lazily (memoized) and per-category saturation rollup on demand. O(N²)
  per category; ≤25k cosine ops at current corpus size.
- **`PricePercentileCalculator`** (NEW) — operates on
  `SearchService._corpus`. Computes percentile within
  `(category × marketplace)`. Memoized per corpus refresh.
- **`DigestService`** (CHANGED) — `BuildAsync` extended with the six new
  fields. New helpers for `newAgents`, `churnRate`, `cohortSurvival`,
  `saturationMap`. Memory cache keyed on
  `(days, marketplace, chain-set, priceMaxUsdc, hourBucket)` with
  hourly TTL; single in-flight computation per cache miss.
- **`browseAgent` handler** (CHANGED) — calls a new
  `BuildCrossPresenceAsync(address)` against `OfferingRepository` that
  groups by `marketplace_version`. Per-offering `pricePercentile`
  populated by `PricePercentileCalculator`.

## Backward compatibility

| Surface | Without v1.7 changes | With v1.7 changes |
| --- | --- | --- |
| `POST /v1/searchAgents` | Existing request shape | `score` semantics changed (now opaque post-rerank); `topOfferings` shape changed (now records) — `topOfferingNames` mirror retains old string-array shape; new fields additive |
| `POST /v1/search` | Existing request + response shape | Per-hit additive `saturation` + `pricePercentile` |
| `GET /v1/agent/{addr}` | Existing fields preserved | Additive `crossPresence` and per-offering `pricePercentile` |
| `GET /v1/digest` | Existing request + existing response fields preserved | `days` cap extended to 90; six new fields populated |

Per-hit additive fields appear regardless of caller params. Strict-schema
consumers of the agent search hit shape need to update to v0.7.0 of the
MCP server — the change is mitigated by the `topOfferingNames` mirror
field for old string-array consumers and the `score` field staying
opaque (callers should sort, not interpret).

## Performance and cost

- **Embedding cost.** Voyage at $0.06 / 1M tokens (model-dependent;
  reuses existing config). Agent profile capped at ~2000 chars ≈ ~600
  tokens. Cold-start: ~500 agents × 600 tokens = 300k tokens = ~$0.018.
  Steady state (~10–30 dirty agents per ~30-min cycle): trivial.
- **Embedding latency.** Voyage batch endpoint handles 128 docs per
  call. Cold-start ~4 calls; well under any sane rate limit.
- **Saturation calc.** O(N²) within category over already-loaded
  embeddings. ~500 offerings × 10 categories = ~25k cosine ops, <100ms.
  Memoized per corpus refresh.
- **Pulse digest.** All queries over indexed columns
  (`first_seen_at`, `is_removed`, `agent_lifetime_snapshot.snapshot_date`).
  Hourly per-(filter-set) cache. <500ms cold compute, <5ms cache hit.
- **Agent search latency.** Reuses the same hybrid recipe as offering
  search; expected p95 latency comparable (~150–250ms with rerank).
  Existing rate limit `public-search-agents` (30/IP/hr) unchanged — the
  upgraded ranker is more expensive per call (rerank API), but still
  fits the existing class.

## Testing strategy

- **Unit tests** (xUnit, in `Metabot.Api.Tests/`):
  - `AgentProfileEmbedderServiceTests` — dirty-queue draining,
    cold-start backfill, Voyage failure retry, transactional bumping
    of `last_change_at`.
  - `AgentSearchServiceTests` — RRF fusion ordering, top-N truncation,
    rerank fallback on Voyage failure, marketplace filter pass-through
    to BM25 leg.
  - `SaturationCalculatorTests` — within-category cosine math; threshold
    enforcement; edge cases (1-offering category, all-similar category).
  - `PricePercentileCalculatorTests` — percentile math, lowN flag,
    marketplace scope split.
  - `DigestServiceTests` — each new field with synthetic data; cache
    hit/miss; partial-data flag; `days` cap honoured at 90;
    cohortSurvival null below 30.
  - `BrowseAgentCrossPresenceTests` — V1-only, V2-only, both,
    tombstoned-offering exclusion, dominant tiebreak by total jobs.
  - `DbMigrationTests` — additive migration applies cleanly to a v1.6
    DB (covers idempotent table + trigger creation).

- **Integration tests** (`Metabot.Api.IntegrationTests/`):
  - `/v1/searchAgents` — query that scores poorly under BM25 alone but
    well under hybrid (synonym/paraphrase test); response shape
    includes new fields and `topOfferingNames` mirror.
  - `/v1/search` — every offering hit carries `saturation` + `pricePercentile`.
  - `/v1/agent/{addr}` — single-marketplace and dual-marketplace
    fixture agents.
  - `/v1/digest` with `days` ∈ {omitted, 1, 7, 30, 60, 90}; verify
    `cohortSurvival` null for <30, present for ≥30; `partial` flag
    behaviour.

- **Smoke tests** (post-deploy, against droplet):
  - `curl -X POST .../v1/searchAgents -d '{"query":"watching whale wallets"}'`
    — non-empty `agents`, top hit's offerings actually relate to whale
    tracking.
  - `curl ".../v1/digest?days=60"` — `cohortSurvival` non-null.
  - `curl .../v1/agent/<known-multi-marketplace-addr>` —
    `crossPresence.inBoth: true`, both `v1` and `v2` blocks populated.
  - End-to-end through `mcp__plugin_acp-find_acp-find__acp_search_agents`
    and `acp_today` with new args.

## Documentation lockstep

Per the project rule: any change to a user-facing surface updates every
relevant `.md` in the same commit.

- `ACP_Metabot/ACP_Metabot/README.md` — new fields, params, version
  bump to 1.7.0; "Public gateway endpoints" table updated.
- `ACP_Metabot/ACP_Metabot/docs/user-guide.md` — explainer sections for
  hybrid agent search, cross-presence, pulse digest extensions.
- `ACP_Metabot/ACP_Metabot/docs/technical-specifications.md` — new
  fields and behaviours.
- `ACP_Metabot/ACP_Metabot/docs/design.md` — architecture diagram
  refreshed.
- `ACP_Metabot/ACP_Metabot/docs/runbook-scaling.md` — note that
  `/v1/searchAgents` is now embedding-backed (rerank) and updates the
  cost class for Lever 5; agent profile embed cost added.
- `ACP_Metabot/ACP_Metabot/docs/superpowers/specs/2026-05-04-metabot-v1-7-meta-search-design.md`
  — this file.
- `acp-find-plugin/README.md` — version bump to 0.7.0; new field /
  args descriptions; hybrid agent search note.
- `acp-find-plugin/skills/acp-find/SKILL.md` (and any related skill
  prose) — agent search hybrid note + pulse description.
- `acp-find-plugin/commands/acp-find-search.md`,
  `commands/acp-find-search-agents.md`,
  `commands/acp-find-today.md`,
  `commands/acp-find-agent.md` — argument docs and example prompts.
- `acp-find-plugin/mcp-server/README.md` — npm-published; version bump
  to 0.7.0; new tool schemas.

The other ACP bot projects (`ACP_DeFiEval`, `ACP_AgentEval`,
`ACP_Tester`) do not reference these specific tool shapes and need no
doc changes unless implementation touches a shared surface.

## Versioning and release

- **`ACP_Metabot`** → v1.7.0 on `main`. Standard droplet redeploy
  (`docker compose pull && docker compose up -d`). Migration runs at
  startup; cold-start agent re-embed begins on first indexer tick after
  boot.
- **`acp-find-plugin`** → 0.7.0. Tagged in github; users update via
  `claude plugin update acp-find`.
- **`acp-find-mcp`** → 0.7.0. Published to npm via WebAuthn TTY (Oliver
  runs `! cd .../mcp-server && npm publish` in his own terminal).

Rollback: previous Docker image tag is retained; `docker compose down
&& docker compose up -d` against the prior tag reverts everything except
the SQLite migration. The migration is additive (new tables, new
triggers, no column drops) so the prior version still reads correctly —
the new tables go unused.

## Risks and open questions

- **`/v1/searchAgents` contract change.** The `score` field flips
  semantics (BM25 lower=better → opaque higher=better) and `topOfferings`
  shape changes (string → record). Mitigated by `topOfferingNames`
  mirror for old string-array consumers and by treating `score` as
  opaque in callers. Strict-schema consumers (if any in the wild) must
  upgrade `acp-find-mcp` to 0.7.0.
- **Embedding-pipeline rate limits.** Voyage rate limit on the
  configured account is the upper bound on cold-start time. If the
  limit is lower than expected, full re-embed could span multiple
  indexer cycles — acceptable but logged. The agent search endpoint
  falls back to the BM25-only path when an agent has no embedding yet
  (so cold-start does not break the endpoint).
- **Cross-presence wallet-collision.** `crossPresence` assumes wallet
  address uniquely identifies an agent. If a single wallet is reused by
  two distinct branding identities (rare but possible), the
  `agent_profiles` row will mix both. Acceptable for v1.7; flag if
  encountered.
- **Saturation threshold tuning.** Default cosine ≥ 0.85 is a guess.
  Will need a one-shot pass on production data to confirm; threshold is
  env-tunable so this is reversible without a redeploy.
- **Cohort identity bleed.** Agents are keyed on wallet address, but a
  wallet that paused activity for >30 days then returned counts as
  "surviving" once they have a job again. This matches the intuitive
  "still around" semantic but is worth flagging in the doc.
- **`gainers` vs `topMovers` naming.** v1.7 keeps the existing `gainers`
  field on `/v1/digest` rather than renaming to `topMovers`. The
  separate `/v1/recentHires` endpoint (v1.6) returns the same data
  directly; it is unchanged.
- **Cache invalidation for digest.** Hourly TTL means a digest can be
  up to 60 minutes stale relative to the indexer. Acceptable for a
  marketplace-temperature view; flagged in user-guide.

## References

- Reconnaissance report (this conversation, 2026-05-04): current schema,
  indexer behaviour, search surface, agent tools, temporal data.
- v1.6 commit `5b71d08` — "feat(v1.6): 4 new public endpoints + V1/V2
  split + filter parity". Source of `/v1/searchAgents`,
  `/v1/recentHires`, `/v1/agentRecentJobs`, `/v1/watches/{id}`, plus
  filter/field additions on existing endpoints.
- `2026-04-30-sharper-core-engine-design.md` — v1.2 hybrid BM25 + dense.
  Same recipe extended to agents in v1.7.
- `2026-04-28-agent-reputation-v2-design.md` —
  `agent_reputation_history`, `agent_lifetime_snapshot`,
  `agent_reputation_snapshots` schema, used by pulse digest definitions.
- `2026-04-30-v2-marketplace-source-design.md` — `v2_known_sellers` and
  `marketplace_version` field semantics.
- `feedback_acp_docs_in_lockstep.md` (user memory) — doc-lockstep rule.
