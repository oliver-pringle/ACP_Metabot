# ACP_Metabot — User Guide

Two audiences:

- **Operators** — running the bot (setup, deploy, monitor, troubleshoot).
- **Buyers** — paying the bot for `search` / `composeStack` / `watchOffering` via ACP.

If you're skimming, [Operator quickstart](#operator-quickstart) is the
shortest useful read. For deploying to a production server, follow
[`docs/deploy/oracle-cloud.md`](deploy/oracle-cloud.md).

---

## Operator quickstart

Goal: get the bot running, indexed, and serving live ACP traffic.

### 1. Prerequisites

- Docker Desktop (Windows/Mac) or Docker Engine (Linux).
- A **Voyage AI** API key — free at https://www.voyageai.com/.
  Add a payment method at https://dashboard.voyageai.com/ to unlock
  standard rate limits.
- An **Anthropic** API key — https://console.anthropic.com/settings/keys.
- An ACP v2 agent — create or upgrade one at
  https://app.virtuals.io/acp/agents/. From the **Signers** tab, copy
  `walletAddress`, `walletId`, and `signerPrivateKey`.

### 2. Configure secrets

Two `.env` files live at:

```
.env                    ← VOYAGE_API_KEY, ANTHROPIC_API_KEY, INTERNAL_API_KEY
acp-v2/.env             ← ACP wallet credentials, ACP_CHAIN, INTERNAL_API_KEY
```

Both are gitignored. Templates:

```
# .env (project root)
VOYAGE_API_KEY=pa-...
ANTHROPIC_API_KEY=sk-ant-...
INTERNAL_API_KEY=<openssl rand -hex 32>
```

```
# acp-v2/.env
ACP_WALLET_ADDRESS=0x...
ACP_WALLET_ID=...
ACP_SIGNER_PRIVATE_KEY=...
ACP_BUILDER_CODE=
ACP_CHAIN=base
ACP_METABOT_API_URL=http://acp-metabot-api:5000
INTERNAL_API_KEY=<same value as above>
```

`INTERNAL_API_KEY` MUST be identical in both files. The C# API rejects
all calls without a matching header.

Verify Compose can resolve them:

```powershell
docker compose config | Select-String "VOYAGE_API_KEY|ANTHROPIC_API_KEY|INTERNAL_API_KEY"
```

You should see the keys expanded inline. If they show as empty, the
root `.env` isn't being read.

### 3. Bring up the API container first

```powershell
docker compose up --build acp-metabot-api
```

First build pulls .NET 10 base images (~60–90s) and publishes the app.
Subsequent builds use cache.

You will see, in this order:

1. `[indexer] starting, interval=600s`
2. `[indexer] api source: total reported=34507 pageCount=346 pageSize=100`
3. `[watch-poller] started; tick=00:30:00`
4. ~6 minutes of (suppressed) HTTP fetch logs
5. `[indexer] api source: fetched 34507 offerings across 346 page(s)`
6. ~1–2 minutes of silent SQLite upsert
7. `[indexer] fetch complete: total=34507 added=34507 updated=0 unchanged=0`
8. `[indexer] embedding 10000 offerings with model=voyage-3-large`
9. ~5–10 minutes of embedding (Voyage calls in batches)
10. Cycle repeats every 600s; the next two ticks finish the remaining ~24k.

### 4. Watch the index fill

The API has no published port. Hit it from a one-shot container with
the API key, on the same Docker network:

```powershell
$key = (Get-Content .env | Where-Object { $_ -match 'INTERNAL_API_KEY=' }) -replace 'INTERNAL_API_KEY=',''
docker run --rm --network acp_metabot_acp-metabot curlimages/curl -s `
  -H "X-API-Key: $key" `
  http://acp-metabot-api:5000/index/stats
```

`/health` is the one endpoint that does NOT need the header — useful
for liveness checks.

The index is fully loaded when `offeringsEmbedded` equals
`offeringsTotal`. Total wall time on first boot: ~30 minutes.

### 5. Smoke-test search

```powershell
docker run --rm --network acp_metabot_acp-metabot curlimages/curl -s `
  -H "X-API-Key: $key" `
  -X POST -H "Content-Type: application/json" `
  -d '{\"query\":\"verify a token contract for honeypot risk\",\"limit\":5}' `
  http://acp-metabot-api:5000/search
```

You should see WachAI's `verify_token` near the top.

### 6. Bring up the sidecar

Once the API is healthy:

```powershell
docker compose up -d --build
docker compose logs -f acp-metabot-acp
```

The sidecar logs `[seller] running — waiting for jobs`. From this point
the bot is live on the chain configured by `ACP_CHAIN`.

### 7. Register offerings on app.virtuals.io

ACP v2 has no programmatic offering registration. Print the registration
blocks for all four offerings:

```powershell
docker compose exec acp-metabot-acp npm run print-offerings
```

Copy each block (`search`, `composeStack`, `watchOffering`,
`agentReputation`) into **Offerings → New offering** in your agent's
dashboard at https://app.virtuals.io/acp/agents/. Per-offering pricing
in the dashboard must match `acp-v2/src/pricing.ts` (search 0.01,
composeStack 0.50, watchOffering 0.50, agentReputation 0.05).

---

## Operator runbook

### Force an immediate refresh

```powershell
docker run --rm --network acp_metabot_acp-metabot curlimages/curl `
  -s -H "X-API-Key: $key" -X POST --max-time 1800 `
  http://acp-metabot-api:5000/index/refresh
```

Synchronous. The request hangs until the full
fetch + upsert + embed pass completes. Returns `{ "ok": true }`.

**Don't call this while a fetch is already in progress.** Two concurrent
fetches share the same HttpClient and will saturate the connection pool,
causing 30s timeouts. Wait for `lastFetchAt` to advance before forcing
another.

### Verify a webhook is firing

After a buyer hires `watchOffering`, you can deterministically verify
their webhook delivery without waiting for new offerings to be indexed:

```powershell
docker run --rm --network acp_metabot_acp-metabot curlimages/curl `
  -s -H "X-API-Key: $key" -X POST `
  http://acp-metabot-api:5000/watches/<watch-id>/test-fire
```

Clears `watch_seen` for the watch and runs an immediate poll, so all
current matches count as "new" and the webhook fires. Returns
`{ "watchId": "...", "fired": true|false }`.

### Switch to offline mode

For development without internet, in `docker-compose.yml`:

```yaml
- Indexer__Source=json-file
- Indexer__SourcePath=/data/seed/offerings.json
```

Then place a JSON array of offerings at `./data/seed/offerings.json`
(see `data/seed/offerings.example.json` for the shape).

### Reset the index

```powershell
docker compose down
del .\data\acp_metabot.db
docker compose up --build acp-metabot-api
```

The schema bootstrap runs on every startup, so a deleted DB is recreated
empty and the next indexer tick repopulates it. **This wipes all
registered watches** — use only if you actually mean to reset state.

### Common failure modes

| Symptom | Cause | Fix |
|---------|-------|-----|
| `401 Unauthorized` on every C# call | `X-API-Key` header missing or wrong | Set `INTERNAL_API_KEY` in BOTH `.env` files to the same value, restart. |
| `500 INTERNAL_API_KEY is not configured` | Env var not picked up by the api container | Check `docker compose config` shows the var. Restart compose. |
| `429 TooManyRequests` from Voyage | No payment method on Voyage account; capped at 3 RPM / 10K TPM | Add a card at https://dashboard.voyageai.com/. Free tokens still apply. |
| `lastFetchAt: null` after fetch logs already showed | Upsert phase still running (silent, ~1–2 min for 34k rows) | Wait. |
| `offeringsEmbedded` stuck after a fail message | BackgroundService is in 600s cooldown after exception | Either wait, or hit `/index/refresh` once. |
| `[webhook] url validation failed pre-POST: ...` | Buyer-supplied webhook resolves to a private/internal IP | Working as intended; SSRF guard caught it. Watch is marked `webhook_failing` after 3 such failures. |
| `[webhook] non-2xx (5xx) on attempt N` | Buyer's endpoint is briefly down | Bot retries with backoff. After 5 consecutive cycle-level failures, status becomes `cancelled`. |
| `unable to get image ... open //./pipe/dockerDesktopLinuxEngine` | Docker Desktop isn't running | Start Docker Desktop, wait until the tray icon stops animating. |
| Two `[indexer] api source: total reported=...` lines back-to-back, then `TaskCanceledException` | Concurrent fetches (boot tick + manual refresh) | Don't call `/index/refresh` during boot. Bot recovers on next tick. |

### Production deploy

The canonical target is Oracle Cloud Ampere A1 (free tier). Full runbook
including VM provisioning, security-list lockdown, bootstrap script, and
.env wiring lives at [`docs/deploy/oracle-cloud.md`](deploy/oracle-cloud.md).

---

## Buyer guide — using `search`, `composeStack`, `watchOffering`, and `agentReputation`

This section is for ACP buyers paying the bot for results.

### Discoverability

The agent advertises four offerings on app.virtuals.io. Look for:

- **Agent:** the wallet address provisioned on app.virtuals.io.
- **Offerings:** `search`, `composeStack`, `watchOffering`, and `agentReputation`.

### V1 vs V2 marketplaces

The bot indexes **both** the legacy V1 marketplace
(`https://acpx.virtuals.io/`, ~34K offerings) and the V2 marketplace
(`https://api.acp.virtuals.io`) on every indexer cycle. Search,
`composeStack`, `watchOffering`, and digest endpoints default to
returning results across both. Each result carries a `marketplaceVersion`
field (`"v1"` or `"v2"`). To restrict to one marketplace, pass an
optional `marketplace` filter (`"v1"` or `"v2"`) on any of those
endpoints; omit it for cross-version (default).

### `search` — 0.01 USDC per call

Semantic search over every ACP offering the bot has indexed (V1 + V2 by
default).

**Requirement payload:**

```json
{
  "query": "close a trading position on a perp DEX",
  "limit": 5,
  "minScore": 0.0,
  "priceMaxUsdc": 1.00
}
```

| Field          | Type    | Required | Notes                                            |
|----------------|---------|----------|--------------------------------------------------|
| `query`        | string  | yes      | Free-form natural language. ≤ 2048 chars.        |
| `limit`        | integer | no       | Default 10, clamped to [1, 50].                  |
| `minScore`     | number  | no       | Cosine similarity threshold. Default 0.0.        |
| `priceMaxUsdc` | number  | no       | Excludes offerings priced above this from results. |
| `marketplace`  | string  | no       | `"v1"` or `"v2"`. Omit for cross-version (default). |

**Deliverable:**

```json
{
  "query": "close a trading position on a perp DEX",
  "count": 3,
  "results": [
    {
      "offeringId": 8421,
      "agentName": "Axelrod",
      "agentAddress": "0xffc60852...",
      "offeringName": "close_position",
      "description": "Close an open trading position by id...",
      "priceUsdc": 0.10,
      "priceType": "fixed",
      "chain": "base",
      "marketplaceVersion": "v1",
      "score": 0.847
    }
  ],
  "bestMatch": {
    "agentAddress": "0xffc60852...",
    "offeringName": "close_position",
    "score": 0.847
  }
}
```

`bestMatch` is `null` when the top result scores below 0.7. Use it as a
short-circuit when you want to dispatch directly to the top match
without showing the buyer a list.

### `composeStack` — 0.50 USDC per call

LLM-curated multi-offering stack for a stated use case. Useful when you
don't know the marketplace well enough to assemble the pieces yourself.

**Requirement payload:**

```json
{
  "useCase": "build a safe token-buy bot for new Base launches",
  "budgetUsdc": 5.00,
  "maxOfferings": 4
}
```

| Field          | Type    | Required | Notes                                            |
|----------------|---------|----------|--------------------------------------------------|
| `useCase`      | string  | yes      | Free-form. Describe the *goal*, not the agents. ≤ 4096 chars. |
| `budgetUsdc`   | number  | no       | Total cap in USDC. Stack will respect it.        |
| `maxOfferings` | integer | no       | Default 5, clamped to [1, 10].                   |
| `marketplace`  | string  | no       | `"v1"` or `"v2"`. Omit for cross-version (default). |

**Deliverable:**

```json
{
  "rationale": "Verifies token safety before any buy, then watches the wallet for entry, then executes the swap, then closes if drawdown exceeds threshold.",
  "stack": [
    {
      "offeringName": "verify_token",
      "agentName": "WachAI",
      "agentAddress": "0xb37230...",
      "priceUsdc": 1.0,
      "role": "Pre-buy contract safety check"
    }
  ],
  "totalPriceUsdc": 1.25
}
```

Each stack entry tells you which agent + offering to call, in what role,
and at what price. Total is summed for budget transparency.

### `watchOffering` — 0.50 USDC per watch

Standing semantic search delivered via webhook. Pay once, register a
query plus an HTTPS `webhookUrl`, and the bot polls every `intervalHours`
for `durationDays`. Each new offering matching your query is POSTed to
your webhook.

**Requirement payload:**

```json
{
  "query": "wallet intelligence and on-chain analytics",
  "webhookUrl": "https://example.com/acp-watch-webhook",
  "durationDays": 7,
  "intervalHours": 6,
  "minScore": 0.65,
  "priceMaxUsdc": 0.20,
  "maxAlerts": 20
}
```

| Field           | Type    | Required | Notes                                                        |
|-----------------|---------|----------|--------------------------------------------------------------|
| `query`         | string  | yes      | Natural-language description of offerings to watch for. ≤ 2048 chars. |
| `webhookUrl`    | string  | yes      | HTTPS only. Must not resolve to a private/internal IP.       |
| `durationDays`  | integer | no       | 1–30. Default 7.                                              |
| `intervalHours` | integer | no       | 1–24. Default 6.                                              |
| `minScore`      | number  | no       | Optional cosine threshold for an offering to count as a match. |
| `priceMaxUsdc`  | number  | no       | Optional max price filter.                                   |
| `maxAlerts`     | integer | no       | Cap on total alerts over the watch lifetime. 1–100. Default 20. |
| `marketplace`   | string  | no       | `"v1"` or `"v2"`. Omit for cross-version (default). |

**Initial deliverable** (returned on the ACP job, synchronously):

```json
{
  "watchId": "1487f6e9-4cfe-4e5a-9e91-8995016b9018",
  "expiresAt": "2026-05-03T10:32:28.141Z",
  "intervalHours": 6,
  "maxAlerts": 20,
  "initialMatches": [/* current top-N matches, NOT counted toward alert cap */]
}
```

**Webhook payload** (POSTed each time new matches appear):

```
POST <your webhookUrl>
Content-Type: application/json; charset=utf-8
User-Agent: TheMetaBot/1.0 (acp-watch)
X-Watch-Id: 1487f6e9-4cfe-4e5a-9e91-8995016b9018
X-Alert-Number: 3

{
  "watchId": "1487f6e9-4cfe-4e5a-9e91-8995016b9018",
  "alertNumber": 3,
  "remainingAlerts": 17,
  "query": "wallet intelligence and on-chain analytics",
  "matches": [/* OfferingMatch entries, only those new since last poll */],
  "polledAt": "2026-04-26T16:07:25.141Z"
}
```

**Reliability:**

- 5-second per-attempt timeout, 3 retries with exponential backoff.
- 3 consecutive cycle-level failures → bot marks the watch
  `webhook_failing` (still polled, retries continue).
- 5 consecutive failures → `cancelled`. No refund — make sure your URL
  is reachable before paying.
- Use `(watchId, alertNumber)` as an idempotency key on your side so
  retries don't double-process.

### `agentReputation` — 0.05 USDC per call

On-chain behavioural reputation for an ACP agent. Returns a 0–100 score
derived from completion rate, dispute rate, recency, 30-day throughput,
and average response time, each with concrete evidence and a corpus
percentile. Cached 24h per agent: subsequent calls inside the window are
free for the same buyer.

**Requirement payload:**

```json
{
  "agentAddress": "0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b",
  "offeringName": "swap"
}
```

| Field          | Type    | Required | Notes                                                              |
|----------------|---------|----------|--------------------------------------------------------------------|
| `agentAddress` | string  | yes      | EVM wallet address of the agent. Lower- or mixed-case is fine.    |
| `offeringName` | string  | no       | When supplied, the response includes a per-offering `hires` block alongside the agent-level behavioural score. |

**Deliverable:**

```json
{
  "agentAddress": "0xfc9f...",
  "agentName": "Some Trading Agent",
  "agentScore": 78,
  "computedAt": "2026-04-28T03:00:00Z",
  "windowDays": 90,
  "subScores": {
    "completion":   { "value": 0.92, "score": 92, "percentile": 78, "evidence": "47/51 terminal jobs completed.", "insufficientData": false },
    "dispute":      { "value": 0.04, "score": 96, "percentile": 84, "evidence": "2/51 terminal jobs rejected or expired (excluding self-rejections).", "insufficientData": false },
    "recency":      { "value": 2.1,  "score": 100, "percentile": 92, "evidence": "Last active 2.1h ago (off-chain).", "insufficientData": false },
    "volume30d":    { "value": 47,   "score": 73, "percentile": 65, "evidence": "47 jobs completed in last 30d (corpus max ..., log-scaled).", "insufficientData": false },
    "responseTime": { "value": 145,  "score": 88, "percentile": 71, "evidence": "Avg response time 2.4min over 47 samples (last 30d).", "insufficientData": false }
  },
  "rawCounts": { "totalJobs": 142, "completed": 128, "rejected": 8, "expired": 6, "completedLast30d": 47, "lastActiveAt": "2026-04-27T19:14:00Z" },
  "flags": { "isColdStart": false, "insufficientData": false, "warmCacheHit": true }
}
```

**Reading the score:**

- `agentScore` is `round(0.30 × completion + 0.25 × dispute + 0.15 ×
  recency + 0.20 × volume30d + 0.10 × responseTime)`. Quality-dominant
  by design — a buyer's question is "will this agent screw up my job?".
- A sub-score of `50` with `insufficientData: true` means we couldn't
  measure that dimension yet (e.g., agent has < 5 terminal jobs). A real
  `50` carries quality signal; an `insufficientData: true` `50` doesn't.
- `flags.isColdStart: true` means the agent has zero terminal jobs —
  recency may still be fresh but every other sub-score is neutral.
- `flags.warmCacheHit: true` means the score was pre-computed by the
  daily warmer; `false` means it was just freshly computed for you.

**Free public lookup:**

`GET https://api.acp-metabot.dev/v1/agentReputation?agent=<addr>` returns
the cached score (no auth, IP rate-limited). Returns `404 not_cached`
if the agent has never been evaluated — pay the SKU to force a live
computation. The free path never triggers compute, so it's DoS-safe and
fast.

### Cost control tips

- `search` is cheap — 100 calls = 1.00 USDC. Use it freely for discovery.
- `composeStack` is more expensive because it runs an LLM. Use it once
  per use case, not per query.
- `watchOffering` is a one-shot 0.50 USDC for monitoring up to 20 alerts
  over a window — works out cheaper than polling `search` repeatedly if
  you're watching for new entrants in a category.
- `agentReputation` is the cheapest paid offering at 0.05 USDC. Use it
  liberally before hiring an unfamiliar agent. The 24h cache means
  re-checks within the day are effectively free. For bulk reputation
  consumption, hit `GET /v1/agentReputation?agent=<addr>` (free,
  cache-only).

### Data freshness

The bot re-indexes both V1 and V2 marketplaces every 10 minutes by
default. `lastFetchAt` in `/index/stats` shows the operator side of this;
from a buyer's perspective you can assume results are at most ~10
minutes behind the public marketplace listings.
