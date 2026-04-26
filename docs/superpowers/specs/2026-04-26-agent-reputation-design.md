# `agentReputation` — design

**Status:** approved 2026-04-26.
**Owner:** Oliver Pringle.
**Implementation target:** ship behind the existing `acp-metabot-api` and ACP sidecar; deploy via the standard `git pull && docker compose up -d --build` flow on the DigitalOcean droplet.

## Goal

Surface a per-offering and per-agent reputation signal derived from the ACP marketplace's *lifetime* job counts (`skill.usageCount` and `agent.jobCount`), already fetched by the indexer but currently discarded. Two consumers:

1. **Paid ACP offering** at `0.05 USDC` — buyers can hire the bot to look up an agent's reputation and (optionally) a specific offering's reputation.
2. **Free `reputation` field** in every search result returned by `/v1/search` — plugin users get reputation context inline, no extra call.

## Non-goals (v1)

- Time-windowed metrics (30-day deltas, recent activity). The upstream API exposes lifetime totals only; daily snapshots accumulate from day 1 so v2 can layer in deltas after ~30 days.
- Direct on-chain contract event reads. Out of scope.
- Completion rate, response time, dispute rate. Not exposed by the upstream API.
- Reputation as a search *ranking* factor (it is only a *displayed* field in v1).
- Backfilled snapshot history. The first snapshot row appears the first indexer cycle past 00:00 UTC after deploy.

## Why now

- The bot just went live on the public gateway (`api.acp-metabot.dev`) so any improvement that adds value to `/v1/search` immediately reaches plugin users.
- `usageCount` and `jobCount` are already on the wire — the cost to capture them is one column-add and a few lines of mapping.
- The marketplace contains a lot of dead listings (offerings with `usageCount=0`); a reputation signal is the cheapest way to help buyers filter them out.

## Data sources

### Upstream API — already pulled, currently discarded

`GET https://acpx.virtuals.io/api/metrics/skills?...`

Each row already contains:

```json
{
  "skill":  { "name": "...", "usageCount": 111725, ... },
  "agent":  { "walletAddress": "0x...", "jobCount": 1139030, ... }
}
```

Today, `AcpApiMarketplaceSource.MapToDto` parses these fields into the DTO classes (`SkillInfo.UsageCount` at line 164, `AgentInfo.JobCount` at line 178) but does not propagate them into `MarketplaceOfferingDto`. v1 simply forwards the values through to storage.

### Derived

- `score(count, maxCount) = round(100 * log(1 + count) / log(1 + maxCount))` — log-scaled because the corpus follows a power-law (top offering has 111,725 hires, long tail near zero).
- `percentile(count, sortedCounts) = round(100 * rank / corpusSize, 1)` — linear rank position. Top item ≈ 100, bottom ≈ 0.

`maxCount` is recomputed on each indexer cycle from the in-memory snapshot. Sort cost on 34K rows ≈ 5 ms — cheap enough that on-demand computation is fine; no precomputed reputation table.

## Architecture

```
Upstream ACP API
   │ (already polled every 10 min)
   ▼
MarketplaceIndexerService
   │  populates offerings.usage_count + offerings.agent_job_count
   │  once per UTC day, also writes a row per offering into
   │  agent_reputation_snapshots(snapshot_date, offering_id, ...)
   ▼
SQLite
   │
   ▼
SearchService (in-memory cache of all offerings)
   │
   ├──► /search and /v1/search responses each gain a `reputation` field
   │
   └──► ReputationService computes /agentReputation responses on demand
            │
            ▼
       /agentReputation endpoint  ──◄  ACP sidecar  ──◄  buyer hires
                                                            (0.05 USDC)
```

## Schema changes

```sql
ALTER TABLE offerings ADD COLUMN usage_count     INTEGER NOT NULL DEFAULT 0;
ALTER TABLE offerings ADD COLUMN agent_job_count INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS agent_reputation_snapshots (
    snapshot_date    TEXT    NOT NULL,    -- YYYY-MM-DD UTC
    offering_id      INTEGER NOT NULL,
    usage_count      INTEGER NOT NULL,
    agent_job_count  INTEGER NOT NULL,
    PRIMARY KEY (snapshot_date, offering_id)
);
CREATE INDEX IF NOT EXISTS idx_repsnap_offering ON agent_reputation_snapshots(offering_id);
```

Storage estimate: 34K offerings × ~50 bytes/row × 365 days ≈ 600 MB/year. Negligible at SQLite scale.

## API contract

### `POST /agentReputation` (internal, X-API-Key required)

Request:

```json
{ "agentAddress": "0x...", "offeringName": "swap" }     // offeringName optional
```

Response (with `offeringName` supplied):

```json
{
  "agentAddress": "0xfc9f...",
  "agentName":    "Ethy AI",
  "agentScore":   100,
  "agentTotalJobs": 1139030,
  "agentPercentile": 100.0,
  "computedAt":     "2026-04-26T17:00:00Z",
  "offering": {
    "name":       "swap",
    "score":      100,
    "hires":      111725,
    "percentile": 100.0
  }
}
```

Response (without `offeringName`) — same agent block, plus `offerings: [...]` array containing every offering owned by the agent, each with `{ name, score, hires, percentile }`. Sorted by `hires` descending.

Errors:
- `404 { error: "agent not found" }` — `agentAddress` not present in any offering row.
- `404 { error: "offering not found for this agent" }` — `agentAddress` exists but does not own the supplied `offeringName`.
- `503 { error: "reputation unavailable, indexer warming up" }` — corpus is empty (cold start before first successful indexer cycle).

### `POST /v1/search` and `/search` — augmented response

Each result entry gains:

```json
{
  "offeringId": 378, "agentName": "ButlerLiquid", ...,
  "reputation": {
    "score":          60,
    "offeringHires":  250,
    "agentTotalJobs": 850
  }
}
```

`reputation` is null only if the offering has not been re-indexed since the migration that added the new columns (i.e., the very first indexer cycle after deploy is in flight).

### Plugin

No new MCP tool. The plugin's `acp_find` already proxies `/v1/search`, so the new `reputation` field shows up automatically. `skills/acp-find/SKILL.md` adds a paragraph instructing Claude to render `reputation.score` as a column when the field is present and to surface it in the `bestMatch` summary.

`acp-find/.claude-plugin/plugin.json` bumps to `0.1.2`. `mcp-server/server.js` does not change. `claude plugin tag --push` produces `acp-find--v0.1.2`.

## ACP sidecar — new offering registration

- New file: `acp-v2/src/offerings/agentReputation.ts`. Requirement schema:
  ```ts
  {
    type: "object",
    properties: {
      agentAddress: { type: "string", description: "Wallet address (lowercased) of the agent to look up" },
      offeringName: { type: "string", description: "Optional. If supplied, response also includes per-offering reputation." }
    },
    required: ["agentAddress"]
  }
  ```
- `acp-v2/src/registry.ts` — add to the offerings array.
- `acp-v2/src/pricing.ts` — `agentReputation: 0.05` USDC.
- `acp-v2/src/seller.ts` — route the `agentReputation` job to a new `apiClient.fetchAgentReputation(req)` call.
- `acp-v2/src/apiClient.ts` — add `fetchAgentReputation({ agentAddress, offeringName? })` helper that POSTs to `/agentReputation` with the X-API-Key.
- Operator: print the new offering registration with `npm run print-offerings` and paste into `app.virtuals.io` to register the SKU.

## Indexer changes

1. `MarketplaceOfferingDto` gains `UsageCount` and `AgentJobCount` (long).
2. `AcpApiMarketplaceSource.MapToDto` propagates `row.Skill.UsageCount` and `row.Agent.JobCount` (default 0 if null).
3. `OfferingRepository.UpsertAsync` writes the new columns. Existing content-hash logic continues to gate "did this offering change?" — but the hash now must include `usageCount` and `agentJobCount` so a popularity-only change still triggers a row update.
4. New step in `MarketplaceIndexerService` cycle, after a successful fetch:
   ```
   today_utc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd")
   if not EXISTS (SELECT 1 FROM agent_reputation_snapshots WHERE snapshot_date = today_utc):
       INSERT INTO agent_reputation_snapshots ...  // batched, single transaction
   ```

## ReputationService

Owns the score/percentile math and the in-memory caches. Refreshed on each successful indexer cycle by hooking into `MarketplaceIndexerService` (the existing `LastFetchAt` event hook).

```csharp
public class ReputationService
{
    private long _maxUsageCount;
    private long _maxAgentJobCount;
    // sorted lists of usage_count and agent_job_count for percentile lookup
    private long[] _sortedUsageCounts = Array.Empty<long>();
    private long[] _sortedAgentJobCounts = Array.Empty<long>();

    public void OnIndexRefreshed(IReadOnlyList<Offering> offerings) { ... }

    public ReputationScore Score(long count, long max) { ... }
    public double Percentile(long count, long[] sortedAsc) { ... }  // binary search

    public AgentReputationResult Build(string agentAddress, string? offeringName, ...) { ... }
}
```

Registered in `Program.cs` as a singleton; injected into:
- The new `/agentReputation` handler.
- `SearchService` for embedding the `reputation` field in search results.

## Out-of-scope explicit non-goals (recap)

- Time-windowed deltas (waiting on snapshot accumulation).
- On-chain contract reads.
- Completion / response-time / dispute metrics.
- Search ranking changes (reputation is displayed only).
- HMAC-signed webhook payloads (separate spec).

## Verification after deploy

Manual smoke tests via `curl` (run from local against the live `api.acp-metabot.dev`):

```bash
# Top of corpus — Ethy AI's "swap"
curl -fsS https://api.acp-metabot.dev/v1/search \
  -H "Content-Type: application/json" \
  -d '{"query":"swap any token","limit":3}' | jq '.results[].reputation'

# Internal endpoint, hit the local docker network on the droplet
ssh root@138.68.174.116 'docker compose exec acp-metabot-api curl -fsS \
  -H "X-API-Key: $INTERNAL_API_KEY" \
  -H "Content-Type: application/json" \
  -d "{\"agentAddress\":\"0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b\",\"offeringName\":\"swap\"}" \
  http://localhost:5000/agentReputation'

# Plugin smoke test
/acp-find:search wallet intelligence and risk scoring
# expect: each result shows reputation in the rendered table
```

Acceptance:
- The Ethy AI / `swap` lookup returns `score=100`, `percentile≈100`, `hires=111725`-ish.
- A randomly-picked agent with `usageCount=0` returns `score=0`, `percentile=0`-ish.
- Plugin search results visibly include reputation.
- A snapshot row appears in `agent_reputation_snapshots` with today's UTC date within the first indexer cycle past midnight.

## Operator: ACP offering registration

Once deployed, run on the host:

```bash
cd /root/ACP_Metabot/acp-v2&& npm run print-offerings
```

Copy the printed `agentReputation` block into `app.virtuals.io` → Offerings → New offering. Confirm the price is set to 0.05 USDC and the requirement schema matches.

## Rollback plan

If the new endpoint or schema migration causes problems:

```bash
# Stop the stack
docker compose down

# Roll back code
git revert <sha-of-feat-commit>
git push

# Restore on droplet
ssh root@138.68.174.116 "cd /root/ACP_Metabot && git pull && docker compose up -d --build"

# The schema additions are additive (ALTER TABLE ADD COLUMN, CREATE TABLE IF
# NOT EXISTS) so old code reads/writes still work; no DB rollback needed.
```

If the ACP offering itself misbehaves, deactivate it on `app.virtuals.io` to stop accepting new jobs. Existing infra continues to serve the old offerings.
