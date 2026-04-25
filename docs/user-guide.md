# ACP_Metabot — User Guide

Two audiences:

- **Operators** — running the bot (setup, deploy, monitor, troubleshoot).
- **Buyers** — paying the bot for `search` / `composeStack` via ACP.

If you're skimming, [Operator quickstart](#operator-quickstart) is the
shortest useful read.

---

## Operator quickstart

Goal: get the bot running, indexed, and serving live ACP traffic.

### 1. Prerequisites

- Docker Desktop (Windows/Mac) or Docker Engine (Linux).
- A **Voyage AI** API key — free at https://www.voyageai.com/.
  Add a payment method at https://dashboard.voyageai.com/ to unlock
  standard rate limits. The 200M free tokens still apply afterwards;
  a full marketplace embed uses ~7M tokens.
- An **Anthropic** API key — https://console.anthropic.com/settings/keys.
  Load some credits in **Settings → Billing**.
- An ACP v2 agent — create or upgrade one at
  https://app.virtuals.io/acp/agents/. From the **Signers** tab, copy
  `walletAddress`, `walletId`, and `signerPrivateKey`.

### 2. Configure secrets

Two `.env` files live at:

```
.env                    ← VOYAGE_API_KEY, ANTHROPIC_API_KEY
acp-v2/.env             ← ACP wallet credentials, ACP_CHAIN
```

Both are gitignored. Templates:

```
# .env (project root)
VOYAGE_API_KEY=pa-...
ANTHROPIC_API_KEY=sk-ant-...
```

```
# acp-v2/.env
ACP_WALLET_ADDRESS=0x...
ACP_WALLET_ID=...
ACP_SIGNER_PRIVATE_KEY=0x...
ACP_BUILDER_CODE=
ACP_CHAIN=baseSepolia
ACP_METABOT_API_URL=http://acp-metabot-api:5000
```

Verify Compose can resolve them:

```powershell
docker compose config | Select-String "VOYAGE_API_KEY|ANTHROPIC_API_KEY"
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
3. ~6 minutes of (suppressed) HTTP fetch logs
4. `[indexer] api source: fetched 34507 offerings across 346 page(s)`
5. ~1–2 minutes of silent SQLite upsert
6. `[indexer] fetch complete: total=34507 added=34507 updated=0 unchanged=0`
7. `[indexer] embedding 10000 offerings with model=voyage-3-large`
8. ~5–10 minutes of embedding (Voyage calls in batches of 4)
9. Cycle repeats every 600s; the next two ticks finish the remaining ~24k.

### 4. Watch the index fill

Open a second PowerShell window. The API has no published port, so we
hit it from a one-shot container on the same Docker network:

```powershell
docker run --rm --network acp_metabot_acp-metabot curlimages/curl -s http://acp-metabot-api:5000/index/stats
```

Sample output:

```json
{
  "offeringsTotal": 34508,
  "offeringsEmbedded": 34508,
  "embeddingModel": "voyage-3-large",
  "embeddingDimension": 1024,
  "lastFetchAt": "2026-04-25T11:24:39.5000189Z",
  "lastFetchCount": 34506
}
```

The index is fully loaded when `offeringsEmbedded` equals
`offeringsTotal`. Total wall time on first boot: ~30 minutes.

### 5. Smoke-test search

```powershell
docker run --rm --network acp_metabot_acp-metabot curlimages/curl -s `
  -X POST -H "Content-Type: application/json" `
  -d '{\"query\":\"verify a token contract for honeypot risk\",\"limit\":5}' `
  http://acp-metabot-api:5000/search
```

You should see WachAI's `verify_token` near the top, with a cosine score
above ~0.7.

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
blocks:

```powershell
docker compose exec acp-metabot-acp npm run print-offerings
```

Copy each block (search and composeStack) into
**Offerings → New offering** in your agent's dashboard at
https://app.virtuals.io/acp/agents/.

---

## Operator runbook

### Force an immediate refresh

```powershell
docker run --rm --network acp_metabot_acp-metabot curlimages/curl `
  -s -X POST --max-time 1800 http://acp-metabot-api:5000/index/refresh
```

Synchronous. The request hangs until the full
fetch + upsert + embed pass completes. Returns `{ "ok": true }`.

**Don't call this while a fetch is already in progress.** Two concurrent
fetches share the same HttpClient and will saturate the connection pool,
causing 30s timeouts. Wait for `lastFetchAt` to advance before forcing
another.

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
empty and the next indexer tick repopulates it.

### Common failure modes

| Symptom | Cause | Fix |
|---------|-------|-----|
| `429 TooManyRequests` from Voyage | No payment method on Voyage account; capped at 3 RPM / 10K TPM | Add a card at https://dashboard.voyageai.com/. Free tokens still apply. |
| `lastFetchAt: null` after fetch logs already showed | Upsert phase still running (silent, ~1–2 min for 34k rows) | Wait. |
| `offeringsEmbedded` stuck after a fail message | BackgroundService is in 600s cooldown after exception | Either wait, or hit `/index/refresh` once. |
| `unable to get image ... open //./pipe/dockerDesktopLinuxEngine` | Docker Desktop isn't running | Start Docker Desktop, wait until the tray icon stops animating. |
| `wget: executable file not found in $PATH` when `docker compose exec` | The aspnet base image is minimal | Use `docker run --rm --network acp_metabot_acp-metabot curlimages/curl ...` instead. |
| Two `[indexer] api source: total reported=...` lines back-to-back, then `TaskCanceledException` | Concurrent fetches (boot tick + manual refresh) | Don't call `/index/refresh` during boot. Bot recovers on next tick. |

---

## Buyer guide — using `search` and `composeStack`

This section is for ACP buyers paying the bot for results.

### Discoverability

The agent advertises two offerings on app.virtuals.io. Look for:

- **Agent:** the wallet address you provisioned.
- **Offerings:** `search` and `composeStack`.

### `search` — 0.05 USDC per call

Semantic search over every ACP offering the bot has indexed.

**Requirement payload:**

```json
{
  "query": "close a trading position on a perp DEX",
  "limit": 5,
  "minScore": 0.0
}
```

| Field      | Type    | Required | Notes                                       |
|------------|---------|----------|---------------------------------------------|
| `query`    | string  | yes      | Free-form natural language.                 |
| `limit`    | integer | no       | Default 10, clamped to [1, 50].             |
| `minScore` | number  | no       | Cosine similarity threshold. Default 0.0.   |

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
      "score": 0.847
    }
  ]
}
```

Use the result list to drive your own job creation against the
recommended agent + offering pair.

### `composeStack` — 0.20 USDC per call

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
| `useCase`      | string  | yes      | Free-form. Describe the *goal*, not the agents.  |
| `budgetUsdc`   | number  | no       | Total cap in USDC. Stack will respect it.        |
| `maxOfferings` | integer | no       | Default 5, clamped to [1, 10].                   |

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

### Cost control tips

- `search` is cheap — 50 calls = 2.50 USDC. Use it freely for discovery.
- `composeStack` is more expensive because it runs Claude. Use it once
  per use case, not per query.
- The bot itself is rate-limited only by ACP infrastructure. There are
  no per-buyer quotas inside the bot.

### Data freshness

The bot re-indexes the marketplace every 10 minutes by default.
`lastFetchAt` in `/index/stats` shows the operator side of this; from a
buyer's perspective you can assume results are at most ~10 minutes
behind the public marketplace listing.
