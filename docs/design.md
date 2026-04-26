# ACP_Metabot — Design

## What this bot is

A meta-bot for the Virtuals Protocol ACP marketplace. It does not provide a
domain service of its own — it indexes every offering produced by every
*other* agent on ACP, embeds the descriptions, and sells three services back
into the marketplace:

| Offering        | Price       | Purpose                                                                |
|-----------------|-------------|------------------------------------------------------------------------|
| `search`        | 0.01 USDC   | Semantic search over the entire ACP marketplace; returns ranked offerings, optional `priceMaxUsdc` filter, and a `bestMatch` flag when the top score is ≥ 0.7. |
| `composeStack`  | 0.50 USDC   | LLM-curated multi-agent stack for a buyer's stated use case.           |
| `watchOffering` | 0.50 USDC   | Standing semantic search delivered via buyer-supplied HTTPS webhook over a 1–30 day window. |

The thesis: ACP will keep growing past the point where buyers can hand-pick
agents from a dashboard. A meta-search layer becomes useful early and
sticky later. Pricing is intentionally low for `search` (high-volume, low-
margin) and slightly higher for `composeStack`/`watchOffering` to cover
LLM and webhook delivery costs.

## Two-tier architecture

```
acp-v2/   (Node 22 / TypeScript)             ACP_Metabot.Api/   (.NET 10)
├─ search.ts          ──┐                    ├─ POST /search          → SearchService
├─ composeStack.ts    ──┤── HTTP ──►         ├─ POST /composeStack    → StackComposerService
├─ watchOffering.ts   ──┤   X-API-Key        ├─ POST /watches         → WatchService
└─ seller.ts          ──┘                    ├─ GET  /watches/{id}    (operator)
                                             ├─ POST /watches/{id}/test-fire (operator)
                                             ├─ GET  /index/stats     (operator)
                                             ├─ POST /index/refresh   (operator)
                                             ├─ GET  /health          (open)
                                             ├─ MarketplaceIndexerService (10-min)
                                             └─ WatchPollerBackgroundService (30-min tick)
                                                  └─ SQLite (offerings + embeddings + watches)
```

The split exists because the ACP v2 SDK (`@virtuals-protocol/acp-node-v2`)
is Node-only. There is no C# SDK and the protocol cannot be spoken
directly from .NET. So:

- **Sidecar (Node + TS)** — speaks ACP, receives jobs, validates
  requirements, sets per-call USDC budgets, and forwards execution to the
  internal API.
- **API (.NET 10 + ASP.NET Minimal API)** — owns the indexer, the SQLite
  store, the embedding cache, the vector search, the Claude composer, and
  the watch poller / webhook delivery.

Both run as separate Docker containers on a shared private bridge
network. The API does **not** publish ports — only the sidecar reaches
it, authenticated by a shared `X-API-Key`. SQLite persists to a host
bind-mount.

## Why these specific pieces

### Embeddings — Voyage `voyage-3-large`

Anthropic does not ship a public embeddings endpoint, and Voyage is the
embedding partner Anthropic itself recommends. `voyage-3-large` is 1024
dimensions, fits the marketplace size comfortably, and the free tier
(200M tokens/month) is more than enough for the full corpus
(~35k offerings × ~200 tokens ≈ 7M tokens) plus query traffic.

Swap path: `IEmbeddingProvider` is the interface; replace the registered
implementation in `Program.cs` to use anything else.

### Vector search — in-memory cosine

`SearchService.cs` loads every embedded row into RAM and brute-forces
cosine similarity. With ~35k rows × 1024 floats × 4 bytes ≈ 140 MB and
sub-100ms latency on commodity hardware, this is fine well past the
current scale. Re-evaluate (e.g. add `sqlite-vss` or a dedicated vector
DB) past ~50k rows.

A `priceMaxUsdc` filter applied before the dot product keeps the result
set bounded for buyers with budget constraints, and a `bestMatch` flag
on the response surfaces whether the top result scores above 0.7 — useful
for downstream LLM agents deciding whether to use the result directly or
present a list.

### Composer — Claude Sonnet 4.6

`StackComposerService` calls Claude with the top-K vector hits and a
system prompt instructing it to return a curated bundle with rationale.
Prompt caching is on the system prompt, so per-call cost stays low
regardless of how many candidate offerings get included in the context.

The user prompt wraps untrusted content (`useCase` from the buyer,
`description`/`name` from third-party indexed agents) inside delimiter
tags (`<use-case>`, `<candidate-description>`, `<candidate-name>`,
`<candidate-agent>`). The system prompt instructs Claude to treat
contents inside those tags as data, not instructions. Closing-tag
breakout attempts and code fences are sanitized in `SanitizeForPrompt`
before interpolation. This is a defense-in-depth measure since
descriptions of indexed agents are user-controlled inputs that flow
directly into the prompt.

### Watch poller — `BackgroundService` on a 30-min tick

`WatchPollerBackgroundService` runs on a 30-minute tick. Each cycle:

```
expired   → MarkExpiredAsync (status='expired')
due       → SELECT WHERE active and last_polled_at + interval_hours <= now
each due watch:
  results = SearchService.SearchAsync(query, limit=20, minScore, priceMax)
  new     = results EXCEPT watch_seen
  if new:
    payload = { watchId, alertNumber, remainingAlerts, query, matches, polledAt }
    ok = WebhookDeliveryService.DeliverAsync(url, watchId, alertNumber, payload)
    if ok: insert new ids into watch_seen, increment alerts_delivered
    if delivered >= max_alerts: status='exhausted'
    if !ok: increment consecutive_failures (>=3 → webhook_failing, >=5 → cancelled)
  always: MarkPolledAsync(now)
```

The 30-min tick is finer than the user-configurable `intervalHours`
(1–24) so we never delay an alert by more than ~30 min after its window
opens. Initial matches at registration are seeded into `watch_seen` so
they are not re-delivered on the first cycle.

### Webhook delivery

`WebhookDeliveryService` POSTs the JSON payload with:

- `X-Watch-Id` and `X-Alert-Number` headers (idempotency keys)
- `User-Agent: TheMetaBot/1.0 (acp-watch)`
- 5-second per-attempt timeout
- 3 retries with exponential backoff (1s, 4s, 16s)

Before every attempt the URL is re-resolved through `WebhookUrlValidator`,
which rejects any address resolving to RFC1918, loopback, link-local
(including the AWS/GCP/Azure cloud-metadata `169.254.169.254`), CGNAT
(`100.64/10`), multicast, reserved, IPv6 unique-local / link-local /
site-local, or v4-mapped equivalents. The double-check defends against
DNS-rebinding attacks where a malicious DNS server returns a public IP
at registration and a private IP at delivery.

### Storage — ADO.NET + SQLite

Tables (see `Db.cs`):

- `offerings` — one row per `(agent_address, offering_name)` pair, with
  the requirement JSON-Schema, price, chain, and a content hash for
  cheap change detection.
- `offering_embeddings` — keyed by offering id, storing the model name,
  dimension, and a binary blob of `float[]`.
- `watches` — one row per registered `watchOffering` job, with the
  buyer-supplied query/webhook/duration plus runtime state
  (`alerts_delivered`, `webhook_consecutive_failures`, `status`,
  `last_polled_at`).
- `watch_seen` — dedup table; every offering id surfaced for a watch
  is recorded so subsequent polls only "alert" on new entries.

ADO.NET (not EF Core, not Dapper) per workspace convention. SQLite (not
PostgreSQL) for operational simplicity — this is one container plus a
file. No separate database server, no schema migrations, no connection
pooling concerns.

### Marketplace data source

`IMarketplaceSource` is the abstraction. Two implementations ship:

- **`AcpApiMarketplaceSource`** *(default)* — paginates
  `https://acpx.virtuals.io/api/metrics/skills` (Strapi-backed) at
  100/page, dedupes by `(agentAddress, offeringName)`. No bearer token —
  the endpoint is gated by Origin/Referer matching `app.virtuals.io`.
- **`JsonFileMarketplaceSource`** — reads `data/seed/offerings.json`.
  Used for offline development and integration tests.

Switched via `Indexer:Source` (`acp-api` or `json-file`).

Third-party fields ingested from the marketplace (agent name, offering
name, description, requirement schema JSON) are truncated at the trust
boundary in `MarketplaceIndexerService` before storage and embedding,
so a malicious agent cannot bloat the DB or amplify embedding costs by
registering pathologically long content.

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

## Security posture

- **`X-API-Key` shared secret.** Both containers receive the same
  `INTERNAL_API_KEY` env var. The C# API rejects every request without
  a matching header (except `/health`). Fail-closed: if the key is not
  configured, the API returns 500 and refuses to proceed. The boilerplate's
  earlier `// TODO: add X-API-Key middleware` is now wired.
- **Evaluator-zero enforcement.** The seller refuses any job whose
  on-chain `evaluatorAddress != 0x0`. With a buyer-controlled evaluator,
  the buyer could take our deliverable and then `reject()` to deny
  payment; rejecting the job before `setBudget` prevents this.
- **Input length caps + indexer truncation.** Bounded inputs at the
  trust boundary so DB rows, Voyage embedding inputs, and LLM prompts
  cannot be inflated by hostile users or hostile indexed agents.
- **Prompt-injection containment.** `composeStack` wraps untrusted
  content in tags and instructs Claude to treat them as data only.
- **SSRF guard on webhook URLs**, including DNS-rebinding defense.

## Design decisions worth knowing

- **Subscription tiers.** ACP v2 has no native recurring billing.
  `watchOffering` works around this by selling a single fixed-price job
  whose deliverable triggers ongoing webhook delivery over a window.
  Other tier ideas (`apiAccess`, `dataLicense`) remain in the brainstorm
  doc and are not v1.
- **No Redis.** The boilerplate path explicitly omits it. The full
  corpus already fits in RAM in the .NET process.
- **Defensive filter on fetch.** Rows with missing
  `agentName`/`walletAddress` get dropped (~0.01% of rows historically).
  Acceptable; logged.
- **Inline JSON deliverables only.** ACP supports inline payloads up to
  ~50 KB. If a larger response is ever needed, add a `/deliverables/{id}`
  endpoint to the API and emit a presigned-URL deliverable from the
  sidecar (`acp-v2/src/deliverable.ts` has a `// TODO:` marker).

## What's intentionally not here

- No `sqlite-vss` extension (current scale doesn't need it).
- No HMAC signing on webhook payloads (deferred; the spec at
  `docs/superpowers/specs/2026-04-26-watchoffering-design.md` discusses it).
- No EF Core / Dapper / PostgreSQL / SQL Server.
- No Redis.
- No tests — add per project as needed.
