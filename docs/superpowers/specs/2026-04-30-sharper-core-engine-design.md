# Sharper Core Engine — TheMetaBot v1.2 design

**Date:** 2026-04-30
**Status:** Approved + implemented
**Owner:** Oliver Pringle
**Bot:** ACP_Metabot

## Summary

Make the existing offerings feel markedly better — no new SKUs, no price
changes. Three changes:

1. **Hybrid BM25 + dense search.** Replace pure cosine in `SearchService`
   with Reciprocal Rank Fusion of FTS5 BM25 and Voyage cosine. Catches
   rare-keyword queries (hex contract addresses, niche tickers, jargon)
   that pure cosine collapses on.
2. **Fielded filters on `/search`.** Three optional fields: `chain` (array),
   `minReputation` (0-100), `freshness` (days). The plugin gets these
   questions daily but the API can't currently express them.
3. **Reputation time-series.** New `agent_reputation_history` table holds
   one row per `(agent, UTC date)` so trajectory is queryable. Public
   `GET /v1/agentReputationHistory` endpoint, plus a `trajectory` array
   on the existing paid `agentReputation` deliverable. Same SKU, same
   $0.05 — just more useful.

## Why

- `search` is the first impression of the bot for plugin users. Pure cosine
  is excellent for natural-language intent but weak when the user pastes a
  contract address or types a niche ticker. BM25 catches these without a
  new dependency — `Microsoft.Data.Sqlite` 9.0.* bundles FTS5.
- The plugin sees questions like "agents on Base only", "agents with rep > 70",
  "agents still active this week" — currently the API only supports `priceMaxUsdc`,
  `staleAfterDays`, and `category`.
- `agentReputation` ships a point-in-time score. Buyers want trajectory:
  "is this agent improving?". The warmer already runs daily, so a daily
  history snapshot is a free side-effect of moving the cache write into
  a small new repository.

## Scope and non-goals

In scope (this spec):

- `offerings_fts` virtual table + sync triggers + idempotent backfill in
  `Db.cs`.
- `OfferingRepository.SearchBm25Async` with sanitised match query.
- `SearchService` Reciprocal Rank Fusion (k=60 TREC default).
- `SearchService.SearchAsync` signature gains `chainFilter`, `minReputation`.
- `Program.cs` `SearchRequest` extended with `chain`, `minReputation`,
  `freshness`.
- `AgentReputationCacheRepository.ListAllAgentScoresAsync` snapshot used by
  the `minReputation` filter.
- `agent_reputation_history` table + `AgentReputationHistoryRepository`.
- `ReputationService.ComputeAsync` writes history; `GetOrComputeAsync` and
  the new cache-only `GetCachedAsync` attach a 30-day trajectory.
- `GET /v1/agentReputationHistory` (rate-limited, reuses the existing
  `public-reputation` policy at 60/h).
- Internal `GET /agentReputationHistory` mirror.
- Sidecar `agentReputation` deliverable schema gains `trajectory`.
- Sidecar `apiClient.ts` types extended with the new fields.
- Plugin `acp_find` adds three filter args + a hybrid description.
- New plugin tool `acp_agent_reputation_history` + bumped description on
  `acp_agent_reputation`.
- Daily history prune (older than 90 days) in the warmer's post-pass.

Out of scope (deferred to v1.3 or later):

- **B7 — Event-driven indexer freshness.** Off-chain feed already gives
  near-real-time signals at 10-min cadence; a Base mainnet `JobCreated`
  websocket subscription would add long-running infra (reconnect, dedupe,
  back-pressure) for marginal payoff.
- Per-offering behavioural breakdown (already roadmapped in rep v2 v1.0).
- HMAC-signed reputation attestations (rep v2 v1.2 roadmap item).
- Cross-chain reputation (rep v2 v2.0 roadmap item).
- Sybil/cluster-aware scoring.

## Architecture deltas

```
                        upstream (acpx.virtuals.io)
                                 │
                                 ▼
                  MarketplaceIndexerService (10-min ticks)
                                 │
                                 ▼
   ┌─────────────────────────────┴──────────────────────────────┐
   │                            SQLite                          │
   │  offerings ◄─ triggers ─► offerings_fts (NEW, FTS5)        │
   │  offering_embeddings                                       │
   │  agent_reputation_cache                                    │
   │  agent_reputation_history (NEW)                            │
   └────────────────────────────────────────────────────────────┘
                                 │
       ┌─────────────────────────┴────────────────────────┐
       ▼                                                  ▼
  SearchService                                   ReputationService
  (hybrid BM25 + dense                            (history capture +
   via Reciprocal Rank                             trajectory attach)
   Fusion, filters)
       │                                                  │
       ▼                                                  ▼
  POST /search                                   POST /agentReputation
  POST /v1/search                                GET  /v1/agentReputation (now with trajectory)
                                                 GET  /v1/agentReputationHistory (NEW)
```

## Schema (additions in `Db.cs`)

```sql
-- B1: hybrid search lexical leg
CREATE VIRTUAL TABLE IF NOT EXISTS offerings_fts USING fts5(
    offering_name, agent_name, description,
    content='offerings', content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);
CREATE TRIGGER IF NOT EXISTS offerings_ai AFTER INSERT ON offerings BEGIN
    INSERT INTO offerings_fts(rowid, offering_name, agent_name, description)
    VALUES (new.id, new.offering_name, new.agent_name, new.description);
END;
CREATE TRIGGER IF NOT EXISTS offerings_ad AFTER DELETE ON offerings BEGIN
    INSERT INTO offerings_fts(offerings_fts, rowid, offering_name, agent_name, description)
    VALUES ('delete', old.id, old.offering_name, old.agent_name, old.description);
END;
CREATE TRIGGER IF NOT EXISTS offerings_au AFTER UPDATE ON offerings BEGIN
    INSERT INTO offerings_fts(offerings_fts, rowid, offering_name, agent_name, description)
    VALUES ('delete', old.id, old.offering_name, old.agent_name, old.description);
    INSERT INTO offerings_fts(rowid, offering_name, agent_name, description)
    VALUES (new.id, new.offering_name, new.agent_name, new.description);
END;

-- C1: reputation history
CREATE TABLE IF NOT EXISTS agent_reputation_history (
    agent_address      TEXT    NOT NULL,
    snapshot_date      TEXT    NOT NULL,                 -- 'YYYY-MM-DD' UTC
    agent_score        INTEGER NOT NULL,
    sub_scores_json    TEXT    NOT NULL,
    raw_counts_json    TEXT    NOT NULL,
    PRIMARY KEY (agent_address, snapshot_date)
);
CREATE INDEX IF NOT EXISTS ix_rep_history_agent_date
    ON agent_reputation_history(agent_address, snapshot_date DESC);
```

Idempotent FTS backfill runs once per boot — only inserts rowids that aren't
already in `offerings_fts` (handles existing prod DBs on first deploy).

## Wire surfaces

### `POST /search` and `POST /v1/search`

Request body gains three optional fields (existing fields unchanged):

```jsonc
{
  "query": "string",
  "chain": ["base", "base-sepolia"],   // optional, ≤8 entries, case-insensitive
  "minReputation": 60,                  // optional, integer 0-100
  "freshness": 30                       // optional, integer 1-365 days
}
```

Backwards compat:

- `staleAfterDays` still works; if both `staleAfterDays` and `freshness` are
  passed, `freshness` wins.
- All new fields are optional.

### `GET /v1/agentReputation` (now includes trajectory)

Response gains an optional `trajectory` field (omitted when null):

```jsonc
{
  "agentAddress": "0x...",
  "agentScore": 78,
  // ... existing fields ...
  "trajectory": [
    { "date": "2026-04-15", "agentScore": 71, "subScores": { /* ... */ } },
    { "date": "2026-04-16", "agentScore": 72, "subScores": { /* ... */ } },
    // ... up to 30 most recent days, oldest → newest
  ]
}
```

### `GET /v1/agentReputationHistory` (NEW)

Cache-only read against `agent_reputation_history`. Reuses the existing
`public-reputation` rate limit policy (60/h per IP).

```jsonc
GET /v1/agentReputationHistory?agent=0x...&days=30
{
  "agentAddress": "0x...",
  "days": 30,
  "history": [{"date":"2026-04-15","agentScore":71,"subScores":{}}, ...]
}
```

`days` clamps to [1, 90].

### Sidecar `agentReputation` deliverable

`deliverableSchema` gains a `trajectory` array of
`{date, agentScore, subScores}` objects. Existing fields unchanged.
Pricing unchanged at 0.05 USDC.

### Plugin (`acp-find-plugin@0.3.0` / `acp-find-mcp@0.3.0`)

- `acp_find`: three new optional args (`chain`, `minReputation`, `freshness`).
- `acp_agent_reputation`: description updated to mention 30-day trajectory.
- `acp_agent_reputation_history`: NEW tool. Args: `agentAddress` + optional
  `days` (1-90, default 30).

## Cross-bot impact

ACP_DeFiEval's deep-eval calls `POST /agentReputation` and embeds the response
verbatim as `JsonElement?` in its deliverable. The additive `trajectory`
field appears inside that snapshot and is silently retained — no DeFiEval
code change required.

## Observability

- `RefreshCorpusAsync` logs the size of the agent-score lookup snapshot
  alongside the corpus count.
- BM25 leg failures fall through to dense-only with a warning log; the
  search endpoint never breaks because of FTS issues.
- Warmer post-pass logs the number of history rows pruned each day.

## Test surface

- `SearchFusionEvaluationTests` — fixture-validation + RRF unit tests
  (run on every CI build) plus a skip-by-default integration test
  (`HybridBeatsCosineOn30Q`) that requires a populated fixture and prod DB
  snapshot.
- Existing `BlockRangeChunkerTests` continue to pass.
- Manual smoke tests: rare-keyword search, fielded-filter search, paid
  `agentReputation` hire (verify trajectory in deliverable), public
  `/v1/agentReputationHistory` GET.

## Phasing (as shipped)

| Phase | Scope                                                                  |
|-------|------------------------------------------------------------------------|
| 0     | FTS5 availability probe in `Db.cs`; eval fixture stub.                 |
| 1     | offerings_fts schema + triggers + backfill + `SearchBm25Async` + RRF. |
| 2     | `chain` + `minReputation` + `freshness` filters end-to-end.            |
| 3     | `agent_reputation_history` table + repo + capture + trajectory + new endpoint + sidecar/plugin updates + warmer prune. |

Each phase ends in a green `dotnet build`, `dotnet test`, and `npm run build`.

## Configuration

No new env vars. `BASE_RPC_URL`, `INTERNAL_API_KEY`, `VOYAGE_API_KEY`, and
`ANTHROPIC_API_KEY` continue to be the only required keys.

## Open items

- Held-out eval fixture has 5 placeholder rows with `expectedAgentAddress`
  and `expectedOfferingName` set to `TBD`. Oliver to fill in real values
  from a prod DB snapshot or a `request_log.query_text` extract once
  metrics have ≥7 days of data. Until then, `HybridBeatsCosineOn30Q`
  remains skip-by-default.

## References

- Implementation plan:
  `docs/superpowers/plans/2026-04-30-sharper-core-engine.md`
- Original brainstorm + punch list:
  `~/.claude/plans/ultrathink-on-ways-themetabot-humble-fox.md`
- Sister-bot consumption shape:
  `../../../ACP_DeFiEval/ACP_DeFiEval/ACP_DeFiEval.Api/Services/MetabotReputationClient.cs`
- Reputation v2 (prerequisite):
  `2026-04-28-agent-reputation-v2-design.md`
