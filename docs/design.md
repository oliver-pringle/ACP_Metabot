# ACP_Metabot — Design

## What this bot is

A meta-bot for the Virtuals Protocol ACP marketplace. It does not provide a
domain service of its own — it indexes every offering produced by every
*other* agent on ACP, embeds the descriptions, and sells two services back
into the marketplace:

| Offering        | Price       | Purpose                                                     |
|-----------------|-------------|-------------------------------------------------------------|
| `search`        | 0.05 USDC   | Semantic search over the entire ACP marketplace.            |
| `composeStack`  | 0.20 USDC   | LLM-curated multi-agent stack for a buyer's stated use case.|

The thesis: ACP will keep growing past the point where buyers can hand-pick
agents from a dashboard. A meta-search layer becomes useful early and
sticky later. Pricing is intentionally low — high-volume, low-margin.

## Two-tier architecture

```
acp-v2/   (Node 22 / TypeScript)             ACP_Metabot.Api/   (.NET 10)
├─ search.ts         ──┐                     ├─ POST /search          → SearchService
├─ composeStack.ts   ──┤── HTTP ──►          ├─ POST /composeStack    → StackComposerService
└─ seller.ts         ──┘                     ├─ GET  /health
                                             ├─ GET  /index/stats
                                             ├─ POST /index/refresh   (operator)
                                             └─ MarketplaceIndexerService (BackgroundService)
                                                   └─ SQLite (offerings + embeddings)
```

The split exists because the ACP v2 SDK (`@virtuals-protocol/acp-node-v2`)
is Node-only. There is no C# SDK and the protocol cannot be spoken
directly from .NET. So:

- **Sidecar (Node + TS)** — speaks ACP, receives jobs, validates
  requirements, sets per-call USDC budgets, and forwards execution to the
  internal API.
- **API (.NET 10 + ASP.NET Minimal API)** — owns the indexer, the SQLite
  store, the embedding cache, the vector search, and the Claude composer.

Both run as separate Docker containers on a shared private bridge
network. The API does **not** publish ports — only the sidecar reaches
it. SQLite persists to a host bind-mount.

## Why these specific pieces

### Embeddings — Voyage `voyage-3-large`

Anthropic does not ship a public embeddings endpoint, and Voyage is the
embedding partner Anthropic itself recommends. `voyage-3-large` is 1024
dimensions, fits the marketplace size comfortably, and the free tier
(200M tokens/month) is more than enough for the full corpus
(34k offerings × ~200 tokens = ~7M tokens) plus query traffic.

Swap path: `IEmbeddingProvider` is the interface; replace the registered
implementation in `Program.cs` to use anything else.

### Vector search — in-memory cosine

`SearchService.cs` loads every embedded row into RAM and brute-forces
cosine similarity. With 34k rows × 1024 floats × 4 bytes ≈ 139 MB and
sub-100ms latency on commodity hardware, this is fine well past the
current scale. Re-evaluate (e.g. add `sqlite-vss` or a dedicated vector
DB) past ~50k rows.

### Composer — Claude Sonnet 4.6

`StackComposerService` calls Claude with the top-K vector hits and a
system prompt instructing it to return a curated bundle with rationale.
Prompt caching is on the system prompt, so per-call cost stays low
(~$0.003 per request) regardless of how many candidate offerings get
included in the context.

### Storage — ADO.NET + SQLite

Two tables (see `Db.cs`):

- `offerings` — one row per `(agent_address, offering_name)` pair, with
  the requirement JSON-Schema, price, chain, and a content hash for
  cheap change detection.
- `offering_embeddings` — keyed by offering id, storing the model name,
  dimension, and a binary blob of `float[]`.

ADO.NET (not EF Core, not Dapper) per workspace convention. SQLite (not
PostgreSQL) for operational simplicity — this is one container plus a
file. No separate database server, no schema migrations, no connection
pooling concerns.

### Marketplace data source

`IMarketplaceSource` is the abstraction. Two implementations ship:

- **`AcpApiMarketplaceSource`** *(default)* — paginates
  `https://acpx.virtuals.io/api/metrics/skills` (Strapi-backed) at
  100/page, dedupes by `(agentAddress, offeringName)`. No auth — the
  endpoint accepts any request whose `Origin` and `Referer` match
  `app.virtuals.io`.
- **`JsonFileMarketplaceSource`** — reads `data/seed/offerings.json`.
  Used for offline development and integration tests.

Switched via `Indexer:Source` (`acp-api` or `json-file`).

### Indexer — `BackgroundService` polling on an interval

`MarketplaceIndexerService` runs a loop:

```
fetch  → upsert (UPDATE if hash changed, otherwise unchanged)
       → embed up to N pending rows
       → sleep IntervalSeconds (default 600s, min 30s)
```

The first tick is immediate so the index is warm by the time the API
takes its first request. Embedding is gated by what's pending — a row
without an embedding for the current model is queued. Concurrency is
bounded (default 4) to be polite to Voyage and to keep cost predictable.

## Design decisions worth knowing

- **Subscription tiers (`watchCategory`, `apiAccess`, `dataLicense`) are
  deferred.** ACP v2 has no native recurring billing. They show up in
  brainstorm docs; do not add them to v1.
- **No Redis.** The boilerplate path explicitly omits it. The full
  corpus already fits in RAM in the .NET process.
- **Defensive filter on fetch.** Rows with missing
  `agentName`/`walletAddress` get dropped (~0.01% of rows historically).
  Acceptable; logged.
- **No auth on the C# API.** Both containers share a private network and
  the API does not publish ports. If you ever expose it, an `X-API-Key`
  middleware slot is marked with a `// TODO:` in `Program.cs`.
- **Inline JSON deliverables only.** ACP supports inline payloads up to
  ~50 KB. If a larger response is ever needed, add a `/deliverables/{id}`
  endpoint to the API and emit a presigned-URL deliverable from the
  sidecar (`acp-v2/src/deliverable.ts` has a `// TODO:` marker).

## What's intentionally not here

- No `sqlite-vss` extension (current scale doesn't need it).
- No subscription / recurring billing.
- No EF Core / Dapper / PostgreSQL / SQL Server.
- No Redis.
- No tests — add per project as needed.
