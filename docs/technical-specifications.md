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

### `acp-metabot-acp`
- Build context: `./acp-v2`
- Loads its env from `./acp-v2/.env` via `env_file:`.
- Reaches the API at `http://acp-metabot-api:5000` (set via
  `ACP_METABOT_API_URL`).
- Connects out to ACP infrastructure on `ACP_CHAIN`
  (`baseSepolia` for testnet, `base` for mainnet).

## Configuration

### C# API — environment variables

Set in `docker-compose.yml` for the API service, or via shell env / user
env-vars locally. The API reads them through `IConfiguration`, except
the API keys (read directly via `Environment.GetEnvironmentVariable`).

| Variable                          | Default                                     | Notes |
|-----------------------------------|---------------------------------------------|-------|
| `ASPNETCORE_URLS`                 | `http://+:5000`                             | Bind address. |
| `ConnectionStrings__Sqlite`       | `Data Source=/data/acp_metabot.db;Cache=Shared` | SQLite file path. |
| `Indexer__Source`                 | `acp-api`                                   | Or `json-file`. |
| `Indexer__SourcePath`             | `/data/seed/offerings.json`                 | Used when `Source=json-file`. |
| `Indexer__ApiBaseUrl`             | `https://acpx.virtuals.io/`                 | Strapi endpoint root. |
| `Indexer__ApiPageSize`            | `100`                                       | Clamped to [1, 100] by upstream. |
| `Indexer__ApiMaxPages`            | `0` (unbounded)                             | Cap for testing. |
| `Indexer__ApiRequestDelayMs`      | `50`                                        | Pause between page requests. |
| `Indexer__ApiSortBy`              | `usageCount`                                | Upstream sort. |
| `Indexer__ApiSortOrder`           | `desc`                                      | Upstream order. |
| `Indexer__IntervalSeconds`        | `600`                                       | Min 30s, enforced in code. |
| `Indexer__EmbeddingConcurrency`   | `4`                                         | Voyage parallelism. |
| `Embeddings__Provider`            | `voyage`                                    | Only `voyage` supported. |
| `Embeddings__Model`               | `voyage-3-large`                            | 1024-dim. |
| `Claude__Model`                   | `claude-sonnet-4-6`                         | Used by `composeStack`. |
| `VOYAGE_API_KEY`                  | *(required)*                                | https://dash.voyageai.com/ |
| `ANTHROPIC_API_KEY`               | *(required)*                                | https://console.anthropic.com/settings/keys |

### ACP sidecar — environment variables

Set in `acp-v2/.env`. Template at `acp-v2/.env.example`.

| Variable                   | Notes |
|----------------------------|-------|
| `ACP_WALLET_ADDRESS`       | Agent wallet, from app.virtuals.io → Signers tab. |
| `ACP_WALLET_ID`            | Same. |
| `ACP_SIGNER_PRIVATE_KEY`   | Same. **Never commit.** |
| `ACP_BUILDER_CODE`         | Optional (Settings tab). |
| `ACP_CHAIN`                | `baseSepolia` (default) or `base`. |
| `ACP_METABOT_API_URL`      | Inter-container URL. Docker: `http://acp-metabot-api:5000`. Local dev: `http://localhost:5000`. |

`acp-v2/.env` is gitignored via root `.gitignore` (`.env` rule).

## SQLite schema

Defined in `ACP_Metabot.Api/Data/Db.cs`. Created idempotently on startup
(`InitializeSchemaAsync`).

```sql
CREATE TABLE IF NOT EXISTS offerings (
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
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
    UNIQUE(agent_address, offering_name)
);
CREATE INDEX IF NOT EXISTS ix_offerings_content_hash ON offerings(content_hash);
CREATE INDEX IF NOT EXISTS ix_offerings_last_seen   ON offerings(last_seen_at);

CREATE TABLE IF NOT EXISTS offering_embeddings (
    offering_id     INTEGER PRIMARY KEY,
    model           TEXT    NOT NULL,
    dimension       INTEGER NOT NULL,
    embedding_blob  BLOB    NOT NULL,
    embedded_at     TEXT    NOT NULL,
    FOREIGN KEY (offering_id) REFERENCES offerings(id) ON DELETE CASCADE
);
```

`embedding_blob` is a packed `float[]` (4 bytes per float, little-endian)
of length `dimension`. `content_hash` is SHA-256 of the canonical
concatenation of agent address + offering name + description + price +
chain + schema JSON, used for cheap change detection during upserts.

## Marketplace fetch protocol

`AcpApiMarketplaceSource` calls:

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

## C# HTTP API

Routes are defined in `Program.cs`. There is no auth.

### `GET /health`
```json
{ "status": "ok", "time": "2026-04-25T11:24:39.5000189Z" }
```

### `GET /index/stats`
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
`lastFetchCount` reflects rows accepted by the defensive filter.
`lastFetchAt` is set after upsert completes.

### `POST /index/refresh`
Operator endpoint. Synchronously runs `RunOnceAsync` (fetch + upsert +
embed up to limit). Returns `{ "ok": true }` after the full pass
finishes — long requests; set client `--max-time` accordingly.

### `POST /search`
Request:
```json
{ "query": "verify a token contract for honeypot risk", "limit": 5, "minScore": 0.0 }
```
- `query` — required, non-blank.
- `limit` — optional, default 10, clamped to [1, 50].
- `minScore` — optional, default 0.0, cosine similarity threshold.

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
      "score": 0.8721
    }
  ]
}
```

### `POST /composeStack`
Request:
```json
{ "useCase": "build a safe token-buy bot", "budgetUsdc": 5.00, "maxOfferings": 4 }
```
- `useCase` — required, non-blank.
- `budgetUsdc` — optional cap, in USDC.
- `maxOfferings` — optional, default 5, clamped to [1, 10].

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

## Sidecar — ACP wire behaviour

Defined in `acp-v2/src/seller.ts`. Per `JobSession`:

1. **`job.created`** — logged. No action.
2. **`message` with `contentType="requirement"`** — parsed as
   `{ name, requirement }`. Looked up against the in-code registry
   (`offerings/registry.ts`). Validated. Budget set to
   `AssetToken.usdc(price, chainId)` from `pricing.ts`. Pending state
   stashed by `jobId`.
3. **`job.funded`** — pulls the stashed requirement, calls `route()`
   which forwards to the C# API, packages the response via
   `toDeliverable()`, calls `session.submit()`.
4. **`job.completed` / `job.rejected` / `job.expired`** — drops the
   pending entry.

Pricing:

| Offering        | USDC  | Source                                 |
|-----------------|-------|----------------------------------------|
| `search`        | 0.05  | `acp-v2/src/pricing.ts` `PRICE_USDC`   |
| `composeStack`  | 0.20  | `acp-v2/src/pricing.ts` `PRICE_USDC`   |

Anything not in the table falls through to `DEFAULT_PRICE_USDC = 0.05`.

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
spam from the Voyage and ACP-API HTTP clients. Indexer logs (anything
under `ACP_Metabot.Api.Services`) still emit at Information.

Key indexer log lines:
- `[indexer] starting, interval={N}s` — boot
- `[indexer] api source: total reported={T} pageCount={P} pageSize={S}` — first page
- `[indexer] api source: fetched {N} offerings across {P} page(s)` — pagination done
- `[indexer] fetch complete: total={T} added={A} updated={U} unchanged={U}` — upsert done
- `[indexer] embedding {N} offerings with model={M}` — embed pass starting
- `[indexer] fetch tick failed — retrying after interval` — exception caught, sleeping until next tick
- `fail: ...embedding batch failed; will retry next tick` — Voyage call threw inside the embed pass

## Operational thresholds

- Indexer interval is enforced ≥ 30s (`Math.Max(30, seconds)` in
  `MarketplaceIndexerService.cs`).
- Embedding concurrency default is 4. Higher is fine for paid Voyage
  tiers; on the free tier (3 RPM, 10K TPM) the embed pass effectively
  fails until billing is added.
- The embed-per-tick limit is hardcoded in
  `MarketplaceIndexerService.cs` (`ListNeedingEmbeddingAsync(limit: ...)`).
  Default 10000. With 34k rows that's three ticks for full backfill.

## File index — what to read first

- `Program.cs` — DI wiring, route table.
- `Services/MarketplaceIndexerService.cs` — the indexer loop.
- `Services/MarketplaceSource/AcpApiMarketplaceSource.cs` — pagination + auth shape.
- `Services/SearchService.cs` — cosine search loop.
- `Services/StackComposerService.cs` — Claude prompt + parsing.
- `Data/Db.cs` — schema.
- `Data/OfferingRepository.cs` — upserts, embedding storage, listing.
- `acp-v2/src/seller.ts` — ACP event handlers.
- `acp-v2/src/router.ts` — sidecar → API forwarding.
- `acp-v2/src/offerings/registry.ts` — requirement validators.
