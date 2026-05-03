# ACP_Metabot — Technical Specifications

Concrete reference for the wire formats, schemas, env vars, and runtime
behaviour. Everything in this document is what the code actually does
today.

## Stack versions

| Component                          | Version      |
|------------------------------------|--------------|
| .NET SDK / runtime                 | 10.0         |
| ASP.NET Core Minimal API           | 10.0         |
| `Microsoft.Data.Sqlite`            | 9.0.x        |
| `Microsoft.AspNetCore.OpenApi`     | 10.0.x       |
| Node.js                            | 22 (`node:22-slim` in Docker) |
| TypeScript                         | ^5.7.2       |
| `@virtuals-protocol/acp-node-v2`   | ^0.0.6       |
| `viem`                             | ^2.21.0      |
| Voyage embedding model             | `voyage-3-large` (1024-dim) |
| Claude model                       | `claude-sonnet-4-6` |

## Containers

Defined in `docker-compose.yml` (project root).

### `acp-metabot-api`
- Build context: `./ACP_Metabot.Api`
- Listens on `http://+:5000` (`ASPNETCORE_URLS`)
- No published ports — reachable only on the `acp-metabot` network at
  `http://acp-metabot-api:5000`.
- Bind-mounts `./data` to `/data`. SQLite file lives at
  `/data/acp_metabot.db`.
- Healthcheck: `curl -fsS http://localhost:5000/health` every 30s,
  3 retries, 30s start period.
- JSON-file logging capped at 5 × 20 MB rotation.

### `acp-metabot-acp`
- Build context: `./acp-v2`
- Loads its env from `./acp-v2/.env` via `env_file:` plus inline
  `INTERNAL_API_KEY` injected from the host environment by docker-compose.
- Reaches the API at `http://acp-metabot-api:5000` (set via
  `ACP_METABOT_API_URL`).
- Connects out to ACP infrastructure on `ACP_CHAIN`
  (`baseSepolia` for testnet, `base` for mainnet).
- `depends_on` the api with `condition: service_healthy`, so the
  sidecar boots only after the api is taking traffic.
- JSON-file logging capped at 5 × 20 MB rotation.

## Configuration

### C# API — environment variables

Set in `docker-compose.yml` for the API service, or via shell env / user
env-vars locally. The API reads them through `IConfiguration`.

| Variable                          | Default                                     | Notes |
|-----------------------------------|---------------------------------------------|-------|
| `ASPNETCORE_URLS`                 | `http://+:5000`                             | Bind address. |
| `ConnectionStrings__Sqlite`       | `Data Source=/data/acp_metabot.db;Cache=Shared` | SQLite file path. |
| `Indexer__Source`                 | `acp-api`                                   | V1 source. Or `json-file`. V2 source runs in parallel and is gated separately. |
| `Indexer__SourcePath`             | `/data/seed/offerings.json`                 | Used when `Source=json-file`. |
| `Indexer__ApiBaseUrl`             | `https://acpx.virtuals.io/`                 | V1 Strapi endpoint root. |
| `Indexer__ApiPageSize`            | `100`                                       | Clamped to [1, 100] by upstream. |
| `Indexer__ApiMaxPages`            | `0` (unbounded)                             | Cap for testing. |
| `Indexer__ApiRequestDelayMs`      | `50`                                        | V1 pause between page requests. |
| `Indexer__ApiSortBy`              | `usageCount`                                | V1 upstream sort. |
| `Indexer__ApiSortOrder`           | `desc`                                      | V1 upstream order. |
| `Indexer__IntervalSeconds`        | `600`                                       | Min 30s, enforced in code. Applies to both V1 + V2 cycles. |
| `Indexer__EmbeddingConcurrency`   | `4`                                         | Voyage parallelism. |
| `Indexer__V2__Enabled`            | `true`                                      | Gate the V2 source. |
| `Indexer__V2__ApiBaseUrl`         | `https://api.acp.virtuals.io`               | V2 marketplace base URL (matches `@virtuals-protocol/acp-node-v2 ^0.0.6` constant). |
| `Indexer__V2__ChainId`            | `8453`                                      | Base mainnet. |
| `Indexer__V2__KnownAgents`        | *(seeded list)*                             | Hardcoded V2 wallets to fetch directly. Defaults seeded with TheMetaBot, DeFiEval, AgentEval, LiquidGuard. |
| `Indexer__V2__KeywordSweepEnabled`| `true`                                      | Run the 80-keyword sweep against `/agents/search`. |
| `Indexer__V2__KeywordSweepTopK`   | `49`                                        | Server caps result count at 49. |
| `Indexer__V2__KeywordSweepKeywords`| *(80-keyword default)*                     | Override via array config. Defaults in `AcpV2MarketplaceSource.DefaultKeywords`. |
| `Indexer__V2__ApiRequestDelayMs`  | `50`                                        | Pause between V2 calls. |
| `Indexer__V2__MaxConcurrentFetches`| `4`                                        | V2 per-wallet fan-out concurrency. |
| `Embeddings__Provider`            | `voyage`                                    | Only `voyage` supported. |
| `Embeddings__Model`               | `voyage-3-large`                            | 1024-dim. |
| `Claude__Model`                   | `claude-sonnet-4-6`                         | Used by `composeStack`. |
| `VOYAGE_API_KEY`                  | *(required)*                                | https://dash.voyageai.com/ |
| `ANTHROPIC_API_KEY`               | *(required)*                                | https://console.anthropic.com/settings/keys |
| `INTERNAL_API_KEY`                | *(required)*                                | Shared secret with sidecar. Generate with `openssl rand -hex 32`. |

### ACP sidecar — environment variables

Set in `acp-v2/.env`. Template at `acp-v2/.env.example`.

| Variable                   | Notes |
|----------------------------|-------|
| `ACP_WALLET_ADDRESS`       | Agent wallet, from app.virtuals.io → Signers tab. |
| `ACP_WALLET_ID`            | Same. |
| `ACP_SIGNER_PRIVATE_KEY`   | PKCS#8 base64 (no PEM headers). **Never commit.** |
| `ACP_BUILDER_CODE`         | Optional (Settings tab). |
| `ACP_CHAIN`                | `base` (mainnet) or `baseSepolia` (testnet). |
| `ACP_METABOT_API_URL`      | Inter-container URL. Docker: `http://acp-metabot-api:5000`. Local dev: `http://localhost:5000`. |
| `INTERNAL_API_KEY`         | Shared secret with C# API. Must match the value in the project-root `.env`. |

`acp-v2/.env` and the project-root `.env` are gitignored.

## Authentication

The C# API enforces an `X-API-Key` header on every endpoint **except
`/health`**. The middleware reads `INTERNAL_API_KEY` from configuration
at startup. Behaviour:

- Missing or wrong header → `401 Unauthorized` with body `Unauthorized`.
- Key not configured → `500` with body `INTERNAL_API_KEY is not configured`.
  Fail-closed; the service refuses to proceed without a key.

The sidecar attaches the header automatically via `apiClient.ts` on every
inter-service call.

## SQLite schema

Defined in `ACP_Metabot.Api/Data/Db.cs`. Created idempotently on startup
(`InitializeSchemaAsync`).

```sql
CREATE TABLE IF NOT EXISTS offerings (
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    marketplace_version      TEXT    NOT NULL DEFAULT 'v1',
    agent_address            TEXT    NOT NULL,
    agent_name               TEXT    NOT NULL,
    offering_name            TEXT    NOT NULL,
    description              TEXT    NOT NULL,
    requirement_schema_json  TEXT,
    price_usdc               REAL    NOT NULL,
    price_type               TEXT    NOT NULL,
    is_private               INTEGER NOT NULL DEFAULT 0,
    chain                    TEXT    NOT NULL,
    content_hash             TEXT    NOT NULL,
    first_seen_at            TEXT    NOT NULL,
    last_seen_at             TEXT    NOT NULL,
    UNIQUE(marketplace_version, agent_address, offering_name)
);
CREATE INDEX IF NOT EXISTS ix_offerings_content_hash ON offerings(content_hash);
CREATE INDEX IF NOT EXISTS ix_offerings_last_seen   ON offerings(last_seen_at);
CREATE INDEX IF NOT EXISTS ix_offerings_marketplace ON offerings(marketplace_version);

CREATE TABLE IF NOT EXISTS offering_embeddings (
    offering_id     INTEGER PRIMARY KEY,
    model           TEXT    NOT NULL,
    dimension       INTEGER NOT NULL,
    embedding_blob  BLOB    NOT NULL,
    embedded_at     TEXT    NOT NULL,
    FOREIGN KEY (offering_id) REFERENCES offerings(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS watches (
    id                            TEXT    PRIMARY KEY,
    job_id                        INTEGER NOT NULL UNIQUE,
    buyer_address                 TEXT    NOT NULL,
    query                         TEXT    NOT NULL,
    webhook_url                   TEXT    NOT NULL,
    duration_days                 INTEGER NOT NULL,
    interval_hours                INTEGER NOT NULL,
    min_score                     REAL,
    price_max_usdc                REAL,
    max_alerts                    INTEGER NOT NULL,
    alerts_delivered              INTEGER NOT NULL DEFAULT 0,
    webhook_consecutive_failures  INTEGER NOT NULL DEFAULT 0,
    status                        TEXT    NOT NULL DEFAULT 'active',
    created_at                    TEXT    NOT NULL,
    expires_at                    TEXT    NOT NULL,
    last_polled_at                TEXT
);
CREATE INDEX IF NOT EXISTS ix_watches_status_polled ON watches(status, last_polled_at);

CREATE TABLE IF NOT EXISTS watch_seen (
    watch_id        TEXT    NOT NULL,
    offering_id     INTEGER NOT NULL,
    first_seen_at   TEXT    NOT NULL,
    PRIMARY KEY (watch_id, offering_id),
    FOREIGN KEY (watch_id) REFERENCES watches(id) ON DELETE CASCADE
);
```

`embedding_blob` is a packed `float[]` (4 bytes per float, little-endian)
of length `dimension`. `content_hash` is SHA-256 of the canonical
concatenation of agent address + offering name + description + price +
chain + schema JSON, used for cheap change detection during upserts.

`watches.status` values: `active`, `expired`, `exhausted` (max alerts
hit), `webhook_failing` (still polled, but webhook unreliable),
`cancelled` (5+ consecutive POST failures).

### v1.3 schema migration (V1 + V2 dual-source)

`InitializeSchemaAsync` runs the migration idempotently on every startup:

- Adds `marketplace_version TEXT NOT NULL DEFAULT 'v1'` to `offerings`
  if absent. Existing rows keep `version='v1'`.
- Drops the legacy `UNIQUE(agent_address, offering_name)` and rebuilds
  it as `UNIQUE(marketplace_version, agent_address, offering_name)`, so
  the same `(addr, name)` can exist on both V1 and V2 marketplaces
  independently.
- Adds the `v2_known_sellers` cache table populated by the (pending)
  on-chain `JobCreated` enumeration.

Migration is automatic on the v1.2 production droplet — no manual step
required.

## Trust-boundary input caps

Untrusted data — buyer requirements and third-party agent fields ingested
by the indexer — is bounded at the boundary so it cannot inflate DB rows,
Voyage embedding inputs, or LLM prompts.

| Source                             | Field                       | Cap (chars) |
|------------------------------------|-----------------------------|-------------|
| Buyer (search, watchOffering)      | `query`                     | 2048        |
| Buyer (watchOffering)              | `webhookUrl`                | 2048        |
| Buyer (composeStack)               | `useCase`                   | 4096        |
| Indexer (third-party agent)        | `agentName`, `offeringName` | 256         |
| Indexer (third-party agent)        | `description`               | 4096        |
| Indexer (third-party agent)        | `requirementSchemaJson`     | 16384       |

TS validators (`acp-v2/src/validators.ts requireString` with `maxLen`)
enforce buyer-side caps; `MarketplaceIndexerService.Truncate` enforces
indexer-side caps before persistence and embedding.

## Marketplace fetch protocol

### V1 — `AcpApiMarketplaceSource`

```
GET https://acpx.virtuals.io/api/metrics/skills
    ?page={n}&pageSize=100&sortBy=usageCount&sortOrder=desc
```

with these headers:

```
User-Agent: ACP_Metabot/1.0 (+https://app.virtuals.io)
Accept: application/json
Origin: https://app.virtuals.io
Referer: https://app.virtuals.io/
```

No bearer token. The endpoint is gated on Origin/Referer (CORS-style),
not on credentials.

Response shape (per page):

```json
{
  "data": [
    {
      "id": 12345,
      "skill": {
        "name": "verify_token",
        "description": "...",
        "priceV2": { "type": "fixed", "value": 1.0 },
        "usageCount": 4231
      },
      "agent": {
        "name": "WachAI",
        "walletAddress": "0xb37230...",
        "jobCount": 5821
      }
    }
  ],
  "pagination": { "page": 1, "pageSize": 100, "total": 34507, "pageCount": 346 }
}
```

The fetch loop stops on: empty `data`, page count reached, or partial
last page (count < pageSize). Rows missing `skill.name` or
`agent.walletAddress` are dropped on the way through `MapToDto`
(defensive — observed loss historically ~0.01%).

### V2 — `AcpV2MarketplaceSource`

V2 has no public list-all endpoint. Enumeration combines three sources
of seller wallets, then per-wallet fans out for full payloads. Both
endpoints below are unauthenticated.

```
GET https://api.acp.virtuals.io/agents/search?query={kw}&topK=49&chainIds=8453
GET https://api.acp.virtuals.io/agents/wallet/{addr}
```

| Endpoint              | Auth | Use                                                               |
|-----------------------|------|-------------------------------------------------------------------|
| `/agents/search`      | none | Keyword sweep for seller-wallet discovery. `topK` server-capped at 49. |
| `/agents/wallet/{a}`  | none | Per-wallet fan-out for the full agent profile + offering payloads.|

Wallet sources, unioned per cycle:

- **A (pending)** — distinct sellers from on-chain `JobCreated` events
  on V2 contract `0x238E541BfefD82238730D00a2208E5497F1832E0` on Base,
  cached in `v2_known_sellers`.
- **B** — keyword sweep over `Indexer:V2:KeywordSweepKeywords` (default
  80 keywords in `AcpV2MarketplaceSource.DefaultKeywords`). Disabled via
  `Indexer:V2:KeywordSweepEnabled=false`.
- **C** — hardcoded list `Indexer:V2:KnownAgents` (defaults seeded with
  TheMetaBot, DeFiEval, AgentEval, LiquidGuard).

Per-wallet fan-out concurrency is capped by
`Indexer:V2:MaxConcurrentFetches` (default 4) with
`Indexer:V2:ApiRequestDelayMs` (default 50) between calls.

## C# HTTP API

Routes are defined in `Program.cs`. All endpoints except `/health`
require the `X-API-Key` header.

### `GET /health`
Open. No auth.
```json
{ "status": "ok", "time": "2026-04-26T11:24:39.5000189Z" }
```

### `GET /index/stats`
```json
{
  "offeringsTotal": 34508,
  "offeringsEmbedded": 34508,
  "embeddingModel": "voyage-3-large",
  "embeddingDimension": 1024,
  "lastFetchAt": "2026-04-26T11:24:39.5000189Z",
  "lastFetchCount": 34506
}
```

### `POST /index/refresh`
Operator endpoint. Synchronously runs `RunOnceAsync` (fetch + upsert +
embed up to limit). Returns `{ "ok": true }` after the full pass
finishes — long requests; set client `--max-time` accordingly.

### `POST /search`
Request:
```json
{ "query": "verify a token contract for honeypot risk",
  "limit": 5, "minScore": 0.0, "priceMaxUsdc": 1.00, "marketplace": null }
```
- `query` — required, non-blank, ≤ 2048 chars.
- `limit` — optional, default 10, clamped to [1, 50].
- `minScore` — optional, default 0.0, cosine similarity threshold.
- `priceMaxUsdc` — optional, excludes offerings priced above this from results.
- `marketplace` — optional, `"v1"` or `"v2"`. Omit for cross-version (default).

Response:
```json
{
  "query": "...",
  "count": 5,
  "results": [
    {
      "offeringId": 14122,
      "agentName": "WachAI",
      "agentAddress": "0xb37230d74933ec98bcfabb1f9eda2fc51d948c8e",
      "offeringName": "verify_token",
      "description": "Verify a token contract by chain and address...",
      "priceUsdc": 1.0,
      "priceType": "fixed",
      "chain": "base",
      "marketplaceVersion": "v1",
      "score": 0.8721
    }
  ],
  "bestMatch": {
    "agentAddress": "0xb37230d74933ec98bcfabb1f9eda2fc51d948c8e",
    "offeringName": "verify_token",
    "score": 0.8721
  }
}
```

Each result carries `marketplaceVersion` (`"v1"` or `"v2"`). Same shape
applies to `/v1/search` (public gateway), `/digest`, `/v1/digest`,
`/composeStack`, `/v1/composeStack`, and the `RegisterWatchRequest`
payload to `/watches`.

`bestMatch` is the top result echoed at the top level when `score >= 0.7`,
else `null`. Lets downstream LLM agents branch on "is the top hit good
enough to use directly?".

### `POST /composeStack`
Request:
```json
{ "useCase": "build a safe token-buy bot", "budgetUsdc": 5.00, "maxOfferings": 4, "marketplace": null }
```
- `useCase` — required, non-blank, ≤ 4096 chars.
- `budgetUsdc` — optional cap, in USDC.
- `maxOfferings` — optional, default 5, clamped to [1, 10].
- `marketplace` — optional, `"v1"` or `"v2"`. Omit for cross-version (default).

Response (`ComposedStack`):
```json
{
  "rationale": "...",
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

Untrusted user input (`useCase`) and third-party content (`description`,
`agentName`, `offeringName`) are wrapped in delimiter tags and the
system prompt instructs Claude to treat tagged content as data only.

### `POST /watches`
Sidecar-only call site (registers a `watchOffering` job in SQLite and
returns the initial snapshot).

Request:
```json
{
  "jobId": 1978,
  "buyerAddress": "0xa3d8...529b",
  "query": "wallet intelligence and on-chain analytics",
  "webhookUrl": "https://example.com/webhook",
  "durationDays": 7,
  "intervalHours": 6,
  "minScore": 0.65,
  "priceMaxUsdc": 0.20,
  "maxAlerts": 20
}
```

Validation:
- `query` non-blank, ≤ 2048 chars.
- `webhookUrl` HTTPS only; resolves to a non-private IP (full SSRF guard).
- `durationDays` clamped to [1, 30] (default 7).
- `intervalHours` clamped to [1, 24] (default 6).
- `maxAlerts` clamped to [1, 100] (default 20).
- `marketplace` (optional) — `"v1"` or `"v2"`. Omit for cross-version (default).

Response:
```json
{
  "watchId": "1487f6e9-4cfe-4e5a-9e91-8995016b9018",
  "expiresAt": "2026-05-03T10:32:28.3077090Z",
  "intervalHours": 6,
  "maxAlerts": 20,
  "initialMatches": [/* OfferingMatch entries — top-N at registration time */]
}
```

### `GET /watches/{id}` (operator)
Returns the full Watch row including `buyer_address` and `webhook_url`.
Used for debugging.

### `POST /watches/{id}/test-fire` (operator)
Clears `watch_seen` for the given watch and immediately runs a single poll.
Returns `{ "watchId": "...", "fired": bool }`. Use to verify webhook
delivery without waiting for genuinely new offerings to be indexed.

## Public gateway (`/v1/*`)

Mirrors most read paths under `/v1/*` for the public `acp-find` plugin
and direct callers. **No `X-API-Key` required**; each policy has its
own per-IP `FixedWindowRateLimiter`. Source classification on each
request log row uses `User-Agent` to distinguish `mcp_plugin` from
`public_other`.

| Route | Policy | Permits/IP/hr | Notes |
|---|---|---|---|
| `POST /v1/search` | `public-search` | 30 | Same handler as internal `/search`. Accepts extra `offset` field (0–1000) for pagination. |
| `POST /v1/composeStack` | `public-compose` | 5 | Same handler as `/composeStack`. Accepts extra `chain[]` filter (≤8 entries). |
| `POST /v1/searchAgents` | `public-search-agents` | 30 | Agent-level search via offering BM25 group-by. Returns `{query, count, results: [{agentAddress, agentName, score, totalOfferings, topOfferings, reputation}]}`. |
| `GET /v1/agentReputation?agent=<addr>` | `public-reputation` | 60 | Cache-only. 404 = not yet evaluated. ETag + 1h Cache-Control. |
| `GET /v1/agentReputationHistory?agent=&days=<1-90>` | `public-reputation` | 60 | Day-by-day trajectory from `agent_reputation_history`. |
| `GET /v1/agentRecentJobs?agent=&days=<1-90>&limit=<1-100>` | `public-agent-recent-jobs` | 20 | Per-job on-chain ledger via `ChainEventScanner.ListAgentRecentJobsAsync`. RPC-heavy; tighter limit. Returns `[{jobId, createdAt, status, counterparty, amountUsdc?}]`. |
| `GET /v1/digest?days=<1-30>` | `public-digest` | 60 | Accepts `chain[]` and `priceMaxUsdc` filters (applied to both newOfferings and gainers). |
| `GET /v1/recentHires?days=<1-30>&limit=<1-50>` | `public-recent-hires` | 60 | Top gainers only (no newOfferings). Same filters as digest. |
| `GET /v1/agent/{address}` | `public-browse-agent` | 60 | Full agent profile. |
| `GET /v1/watches/{id}` | `public-browse-agent` | 60 | **Public-redacted** view: omits `buyerAddress` and `webhookUrl`. Returns watchId, status, query, expiry, alertsDelivered, intervalHours, maxAlerts, marketplace. |
| `GET /v1/categories` | unlimited | — | Each category now carries `offeringCount` computed from the live corpus. |
| `GET /v1/health` | unlimited | — | Adds `corpus.v1Count` and `corpus.v2Count` to the response. |

**Why `/v1/watches/{id}` is redacted publicly while the operator path is not:**
the watch's `webhook_url` is buyer-private and would let an abuser spam
the destination endpoint if leaked; `buyer_address` identifies the
purchaser. The operator path (`GET /watches/{id}`, X-API-Key gated) returns
the full row including those fields for support / debugging.

**Cross-bot consumers.** Sibling bots on the `acp-shared` docker bridge
that need the full (un-rate-limited, un-redacted) shape call the internal
endpoints with `X-API-Key: $INTERNAL_API_KEY` instead of going through
`/v1/*`. ACP_DeFiEval's deep-eval tier does this for live reputation
lookups via `POST /agentReputation`. New cross-bot integrations should
prefer the internal path so they don't compete with public callers for
rate-limit budget.

## Webhook delivery

Each webhook POST:

```
POST <buyer-supplied URL>
Content-Type: application/json; charset=utf-8
User-Agent: TheMetaBot/1.0 (acp-watch)
X-Watch-Id: <uuid>
X-Alert-Number: <int>

{
  "watchId": "1487f6e9-4cfe-4e5a-9e91-8995016b9018",
  "alertNumber": 3,
  "remainingAlerts": 17,
  "query": "the original query text",
  "matches": [/* OfferingMatch entries — only those new since last poll */],
  "polledAt": "2026-04-26T16:07:25.141Z"
}
```

- 5-second timeout per attempt.
- 3 retries with exponential backoff (1s, 4s, 16s).
- Considered success on any 2xx response.
- 3 consecutive failures → `webhook_failing` (still polled).
- 5 consecutive failures → `cancelled` (no further polling).
- `WebhookUrlValidator.ValidateAsync` runs before every attempt
  (DNS-rebinding defense).

## Sidecar — ACP wire behaviour

Defined in `acp-v2/src/seller.ts`. Per `JobSession`:

1. **`job.created`** — logged. No action.
2. **`message` with `contentType="requirement"`** — parsed as the raw
   requirement payload (the SDK sends just the buyer's payload; the
   offering name lives on the on-chain `AcpJob.description` field, not
   in the message body). Job is fetched if not cached.
3. **Evaluator check** — if `job.evaluatorAddress != 0x0`, refuses with
   a `sendMessage` and returns *before* `setBudget`. Prevents
   take-and-reject griefing.
4. **Validate + budget** — looks up the offering in the in-code registry
   (`offerings/registry.ts`). Validates the requirement (length caps,
   type checks). Budget set to `AssetToken.usdc(price, chainId)` from
   `pricing.ts`. Pending state stashed by `jobId`.
5. **`job.funded`** — pulls the stashed requirement, calls `route()`
   which forwards to the C# API (with `X-API-Key`), packages the
   response via `toDeliverable()`, calls `session.submit()`.
6. **`job.completed` / `job.rejected` / `job.expired`** — drops the
   pending entry.

Pricing (`acp-v2/src/pricing.ts`):

| Offering         | USDC  |
|------------------|-------|
| `search`         | 0.01  |
| `composeStack`   | 0.50  |
| `watchOffering`  | 0.50  |

Anything not in the table falls through to `DEFAULT_PRICE_USDC = 0.01`.

## Logging

ASP.NET Core defaults plus this filter (`appsettings.json`):

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft.AspNetCore": "Warning",
    "System.Net.Http.HttpClient": "Warning"
  }
}
```

The HttpClient filter exists to suppress the four-line-per-request info
spam from the Voyage and ACP-API HTTP clients. Indexer / watch logs
(anything under `ACP_Metabot.Api.Services`) still emit at Information.

Log driver in production: `json-file`, capped at 5 × 20 MB rotation per
container.

Key log lines:
- `[indexer] starting, interval={N}s` — indexer boot
- `[indexer] api source: total reported={T} pageCount={P} pageSize={S}` — first page
- `[indexer] fetch complete: total={T} added={A} updated={U} unchanged={U}` — upsert done
- `[watch-poller] started; tick=00:30:00` — watch poller boot
- `[watch] registered id={Id} jobId={JobId} expires={Expires} initial={Count}` — new watch
- `[watch] polling {Count} due watches` — start of poll cycle
- `[watch] id={Id} exhausted at {Delivered}/{Max} alerts` — alert cap hit
- `[watch] id={Id} marked webhook_failing at {Failures} failures`
- `[watch] id={Id} cancelled after {Failures} consecutive failures`
- `[webhook] non-2xx ({Status}) on attempt {N} for {Url}`
- `[webhook] url validation failed pre-POST: {Reason} ({Url})` — SSRF / rebinding catch

## Operational thresholds

- Indexer interval is enforced ≥ 30s (`Math.Max(30, seconds)` in
  `MarketplaceIndexerService.cs`).
- Embedding concurrency default is 4. Higher is fine for paid Voyage
  tiers; on the free tier (3 RPM, 10K TPM) the embed pass effectively
  fails until billing is added.
- The embed-per-tick limit is hardcoded in
  `MarketplaceIndexerService.cs` (`ListNeedingEmbeddingAsync(limit: ...)`).
  Default 10000. With 34k rows that's three ticks for full backfill.
- Watch poller runs every 30 minutes regardless of how many watches are
  due. With `intervalHours=1` watches, expect actual poll cadence of
  ~30–60 min. With `intervalHours=24` watches, daily.

## File index — what to read first

- `Program.cs` — DI wiring, route table, X-API-Key middleware.
- `Services/MarketplaceIndexerService.cs` — the indexer loop, trust-boundary truncation.
- `Services/MarketplaceSource/AcpApiMarketplaceSource.cs` — pagination shape.
- `Services/SearchService.cs` — cosine search loop, priceMaxUsdc filter.
- `Services/StackComposerService.cs` — Claude prompt with delimiter tags + sanitization.
- `Services/WatchService.cs` — watch register / poll one / poll due.
- `Services/WatchPollerBackgroundService.cs` — 30-min tick loop.
- `Services/WebhookDeliveryService.cs` — POST + retries + headers.
- `Services/WebhookUrlValidator.cs` — SSRF/DNS-rebinding guard.
- `Data/Db.cs` — schema.
- `Data/OfferingRepository.cs` — upserts, embedding storage, listing.
- `Data/WatchRepository.cs` — watch CRUD.
- `acp-v2/src/seller.ts` — ACP event handlers, evaluator-zero check.
- `acp-v2/src/router.ts` — sidecar → API forwarding.
- `acp-v2/src/offerings/{search,composeStack,watchOffering}.ts` — schemas + validators.
- `acp-v2/src/apiClient.ts` — X-API-Key wiring + retry logic.
