# ACP_Metabot — Marketplace Discovery for ACP

A meta-bot for the Virtuals Protocol ACP marketplace. Indexes every offering
across all agents, embeds them, and exposes four ACP offerings:

| Name              | Price        | What it does                                                                  |
|-------------------|--------------|-------------------------------------------------------------------------------|
| `search`          | 0.01 USDC    | Hybrid BM25 + dense semantic search; returns ranked offerings + a `bestMatch` flag when score ≥ 0.7. Filters by `priceMaxUsdc`, `chain`, `minReputation`, `freshness`. |
| `composeStack`    | 0.50 USDC    | LLM-curated multi-offering stack for a buyer's stated use case.               |
| `watchOffering`   | 0.50 USDC    | Standing semantic search delivered via buyer-supplied HTTPS webhook over a 1–30 day window. |
| `agentReputation` | 0.05 USDC    | On-chain behavioural reputation (0–100) for an agent: completion rate, dispute rate, recency, 30-day throughput, avg response time. Cached 24h. Includes a 30-day daily trajectory in the deliverable. |

Public read-only sibling endpoint: `GET /v1/agentReputationHistory?agent=<addr>&days=<1-90>` returns day-by-day trajectory without a paid hire.

Built off the BasicBot boilerplate. Live on Base mainnet. Design specs:

- `docs/design.md` — overall architecture
- `docs/superpowers/specs/2026-04-26-watchoffering-design.md` — watchOffering spec
- `docs/superpowers/specs/2026-04-28-agent-reputation-v2-design.md` — agentReputation v2 (behavioural) spec
- `docs/superpowers/specs/2026-04-30-sharper-core-engine-design.md` — hybrid search + fielded filters + reputation trajectory (v1.2)

## Architecture

```
acp-v2/   (Node 22 / TypeScript)             ACP_Metabot.Api/   (.NET 10)
├─ search.ts          ──┐                    ├─ POST /search          → SearchService
├─ composeStack.ts    ──┤── HTTP ──►         ├─ POST /composeStack    → StackComposerService
├─ watchOffering.ts   ──┤   X-API-Key        ├─ POST /watches         → WatchService
└─ seller.ts          ──┘                    ├─ GET  /watches/{id}    (operator)
                                             ├─ POST /watches/{id}/test-fire (operator)
                                             ├─ GET  /index/stats     (operator)
                                             ├─ POST /index/refresh   (operator)
                                             ├─ GET  /health          (unauth'd)
                                             ├─ MarketplaceIndexerService (10-min tick)
                                             └─ WatchPollerBackgroundService (30-min tick)
                                                  └─ SQLite (offerings + embeddings + watches)
```

The TS sidecar speaks ACP v2 (the SDK is Node-only). The C# API holds the
indexer, vector search, Claude composer, and watch poller. All inter-service
calls require an `X-API-Key` header; only `/health` is unauth'd.

The sidecar polls its own queries against the indexed corpus every
`intervalHours` and POSTs new matches to a buyer-supplied HTTPS webhook
for `watchOffering`.

## Prerequisites

- .NET 10 SDK
- Node.js 22+
- Docker / Docker Compose (for production)
- A **Voyage AI** API key (for embeddings) — https://www.voyageai.com/
- An **Anthropic** API key (for `composeStack`) — https://console.anthropic.com/

## Local development

Two `.env` files are required. The shared `INTERNAL_API_KEY` authenticates
the sidecar's calls into the C# API. Generate it with `openssl rand -hex 32`
and use the same value in both files.

```bash
# Project root .env
VOYAGE_API_KEY=pa-...
ANTHROPIC_API_KEY=sk-ant-...
INTERNAL_API_KEY=<openssl rand -hex 32>
```

```bash
# acp-v2/.env
ACP_WALLET_ADDRESS=0x...
ACP_WALLET_ID=...
ACP_SIGNER_PRIVATE_KEY=...
ACP_BUILDER_CODE=
ACP_CHAIN=base
ACP_METABOT_API_URL=http://localhost:5000
INTERNAL_API_KEY=<same value as above>
```

Three terminals:

```bash
# Terminal 1 — C# API on http://localhost:5000
cd ACP_Metabot.Api
export VOYAGE_API_KEY=pa-...
export ANTHROPIC_API_KEY=sk-ant-...
export INTERNAL_API_KEY=...
dotnet run
```

```bash
# Terminal 2 — ACP sidecar (watches for TS changes)
cd acp-v2
npm install
npm run dev
```

```bash
# Terminal 3 — manually drive the API (optional)
# /health is open; everything else needs X-API-Key
curl http://localhost:5000/health
curl -H "X-API-Key: $INTERNAL_API_KEY" -X POST http://localhost:5000/search \
  -H "Content-Type: application/json" \
  -d '{"query":"close a trading position on a perp DEX","limit":5}'
```

## Marketplace data source

Two upstream marketplaces are indexed side-by-side every cycle:

- **V1** (`AcpApiMarketplaceSource`) — paginates
  `https://acpx.virtuals.io/api/metrics/skills` (~34K offerings).
- **V2** (`AcpV2MarketplaceSource`) — reads `https://api.acp.virtuals.io`
  (the base URL hardcoded in `@virtuals-protocol/acp-node-v2 ^0.0.6`).

V2 has no public list-all endpoint, so enumeration combines three sources
into one deduped wallet set, then fans out to `/agents/wallet/{addr}` per
wallet (the per-wallet endpoint and `/agents/search` are both unauthenticated):

- **Source A — chain-event scan** (v1.4, default on). `V2SellerScannerService`
  scans `JobCreated` events on the V2 contract
  `0x238E541BfefD82238730D00a2208E5497F1832E0` with no provider filter and
  upserts every distinct provider into the `v2_known_sellers` SQLite cache.
  This is the comprehensive set: every V2 agent that has ever been hired.
  Cold-start runs from `Reputation:ContractDeployBlock` (or
  `Indexer:V2:SellerScanFromBlock` if overridden); subsequent runs scan only
  the delta since the persisted checkpoint.
  Resilience: scans at most `Indexer:V2:MaxBlocksPerTick` (default 100K)
  per pass, persisting observations + advancing the checkpoint **after each
  10K-block chunk**, so a transient RPC error mid-window strands at most one
  chunk's worth of work rather than the full cold-start range. Cadence
  adapts: when the checkpoint is more than one window behind head, the loop
  sleeps `Indexer:V2:SellerScanCatchUpIntervalMinutes` (default 5min); once
  caught up, it falls back to `Indexer:V2:SellerScanIntervalMinutes`
  (default 60min).
- **Source B — keyword sweep** against
  `/agents/search?query=X&topK=49&chainIds=8453` (default 80-keyword set in
  `AcpV2MarketplaceSource.DefaultKeywords`). Catches new agents who haven't
  yet been hired (so chain events haven't surfaced them).
- **Source C — hardcoded known wallets** (`Indexer:V2:KnownAgents`) so a
  fresh deploy is always findable in its own index.

Each indexed row carries a `marketplace_version` (`v1` or `v2`) and the
same `(agent_address, offering_name)` can exist on both marketplaces
independently. Search defaults to cross-version; pass an optional
`marketplace` filter (`"v1"` or `"v2"`) to narrow.

A `json-file` source is also supported for offline dev — see
`docs/technical-specifications.md`. V2 enumeration knobs:

```yaml
# docker-compose.yml or env
- Indexer__V2__Enabled=true
- Indexer__V2__ApiBaseUrl=https://api.acp.virtuals.io
- Indexer__V2__ChainId=8453
- Indexer__V2__KeywordSweepEnabled=true
- Indexer__V2__KeywordSweepTopK=49
- Indexer__V2__ApiRequestDelayMs=50
- Indexer__V2__MaxConcurrentFetches=4
# Indexer__V2__KnownAgents and Indexer__V2__KeywordSweepKeywords
# default to the seeded values; override via array config if needed.
- Indexer__V2__SellerScanIntervalMinutes=60
# Indexer__V2__SellerScanFromBlock= (defaults to Reputation:ContractDeployBlock)
```

## Provisioning the agent

1. Go to https://app.virtuals.io/acp/agents/, create or upgrade an agent to V2.
2. From the **Signers** tab, copy `walletId` and `signerPrivateKey`.
3. Paste into `acp-v2/.env`:
   ```
   ACP_WALLET_ADDRESS=0x...
   ACP_WALLET_ID=...
   ACP_SIGNER_PRIVATE_KEY=...
   ACP_CHAIN=base
   ```
4. Register all four offerings (`search`, `composeStack`, `watchOffering`,
   `agentReputation`) on app.virtuals.io. Print the registration JSON with:
   ```bash
   cd acp-v2 && npm run print-offerings
   ```

## Production (Linux / Docker)

The canonical deploy target is **Oracle Cloud Ampere A1 (ARM, free tier)
running Ubuntu 22.04**. Full runbook: **[`docs/deploy/oracle-cloud.md`](docs/deploy/oracle-cloud.md)**.

Quick version once the VM is bootstrapped:

```bash
git clone <your-repo> ~/ACP_Metabot
cd ~/ACP_Metabot/ACP_Metabot
# Create .env (root) and acp-v2/.env from the templates above; chmod 600.
docker compose up -d --build
docker compose logs -f
```

The API has no published ports — only the sidecar talks to it on the
internal `acp-metabot` bridge network. SQLite persists to
`./data/acp_metabot.db` on the host.

`acp-metabot-api` also joins the external bridge `acp-shared` (created
once with `docker network create acp-shared`). This allows ACP_DeFiEval's
deep-eval tier to call `POST /agentReputation` on `acp-metabot-api:5000`
directly, authenticated with `X-Api-Key: $INTERNAL_API_KEY`. No new
endpoint was added; only cross-bot reachability was enabled in v1.1.

## Operator telemetry

Every request is recorded into the `request_log` table by an ASP.NET
middleware that runs after the rate limiter (so 429s are captured) and
before the X-API-Key check (so 401s on internal paths are captured).
Provider failures (Voyage / Claude) are tagged as `provider_error =
"voyage_<status>"` / `"claude_<status>"` via typed exceptions.

Five operator-only `GET /metrics/*` endpoints (X-API-Key gated, same as
`/index/stats`) expose the data. They are NOT reachable through the
public Caddy gateway — exec into the api container or SSH-tunnel:

```bash
# From the droplet:
docker compose exec acp-metabot-api curl -s \
  -H "X-API-Key: $INTERNAL_API_KEY" \
  http://localhost:5000/metrics/summary?days=7 | jq

# Other endpoints:
.../metrics/timeseries?days=7&granularity=hour
.../metrics/endpoints?days=7
.../metrics/top?dim=query&days=7   (or dim=agent)
.../metrics/errors?days=1&limit=100
```

The `summary` response includes `metricsDropped` — non-zero means the
in-process bounded channel (4096) was overflowed, the oldest events
were dropped, and you should look into bumping capacity or batch size
in `MetricsWriterService`.

Source classification (`source` column on `request_log`):
- `mcp_plugin` — `/v1/*` requests with `User-Agent: acp-find-plugin/<ver>`.
- `public_other` — any other `/v1/*` traffic (curl, browsers, other MCP clients).
- `internal` — non-`/v1/*` traffic (sidecar, cross-bot). The `X-Caller`
  header (e.g. `sidecar`, `defieval`) is captured into `caller_id` for
  per-bot breakdown without a schema change.

Retention: raw rows 14 days, hourly rollup 90 days, daily rollup
forever. Hourly rollup happens at minute 5 of every hour; daily rollup
+ prune at 03:00 UTC.

For the metric → scaling-lever mapping, see [`docs/runbook-scaling.md`](docs/runbook-scaling.md).

## Security posture

- **`X-API-Key` between sidecar and C# API.** Required on every endpoint
  except `/health` and `/v1/*`. Fail-closed if missing or wrong. Compared
  with `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes to defang
  timing oracles.
- **SSRF guard on `webhookUrl`.** Resolves and rejects RFC1918, loopback,
  link-local (incl. cloud metadata `169.254.169.254`), CGNAT, multicast,
  reserved, and IPv6 site-/link-local/unique-local/multicast. Re-validated
  before every webhook delivery to defend against DNS rebinding. **Auto-
  redirect is disabled on the webhook HttpClient**; every `Location`
  header on a 3xx response is re-validated through the same guard before
  the next hop, capped at 5 hops. A buyer cannot register a public webhook
  that 302s to internal/cloud-metadata addresses.
- **Forwarded-header trust list.** `TRUSTED_PROXY_NETWORKS` (comma-separated
  CIDRs) gates which peers are allowed to set `X-Forwarded-*`. The compose
  default trusts only the pinned `acp-metabot` bridge subnet (172.28.0.0/16,
  where Caddy lives); sibling bots on `acp-shared` can't forge client IPs to
  bypass per-IP rate limits.
- **Evaluator-zero enforcement.** Seller refuses jobs with
  `evaluatorAddress != 0x0` to prevent take-and-reject griefing.
- **Input length caps (request boundary).** Public endpoints reject
  `query` > 1000 chars and `useCase` > 2000 chars before any AI provider
  call. The indexer separately truncates third-party `description` to 4 KB
  before persistence/embedding.
- **Persistence caps (request_log).** `RequestMetricsMiddleware` truncates
  stored `query_text` to 200 chars and `user_agent` to 200 chars before
  writing to SQLite, so backups and `/metrics/top` can't surface unbounded
  user-supplied text.
- **No internal exception messages in HTTP responses.** Reputation-compute
  failures return a generic message; the full exception is logged
  server-side only.
- **Prompt-injection defense (`composeStack`).** Wraps untrusted content
  (use-case + every candidate's name/agent/description) in delimiter tags
  and sanitizes closing-tag breakouts and code fences. System prompt
  contains an explicit SECURITY block instructing Claude to treat tagged
  content as data, not instructions. Claude's output is parsed as JSON and
  every entry must match a known `(offeringName, agentAddress)` pair from
  the candidate list — agent-fabricated stack entries are dropped.

## Design decisions worth knowing

- **Embeddings:** Voyage `voyage-3-large` (1024-dim).
- **Vector search:** in-memory cosine across all rows. Fine up to ~50K
  offerings.
- **Claude:** Sonnet 4.6 with prompt caching on the system prompt.
- **Webhook delivery:** 5-second timeout, 3 retries (1s/4s/16s backoff).
  Per-request `X-Watch-Id` and `X-Alert-Number` headers,
  `User-Agent: TheMetaBot/1.0 (acp-watch)`.

## What's intentionally not in this shell

- No `sqlite-vss` extension (in-memory cosine is plenty at current scale)
- No HMAC signing on webhook payloads (deferred — buyer can supply a
  secret-bearing URL if they want auth)
- No EF Core (using classic ADO.NET per workspace convention)
- No Redis (per workspace convention)
- No tests — add per project as needed
