# ACP_Metabot — Marketplace Discovery for ACP

A meta-bot for the Virtuals Protocol ACP marketplace. Indexes every offering
across all agents, embeds them, and exposes three ACP offerings:

| Name             | Price        | What it does                                                                  |
|------------------|--------------|-------------------------------------------------------------------------------|
| `search`         | 0.01 USDC    | Semantic search; returns ranked offerings + a `bestMatch` flag when score ≥ 0.7. Filters by `priceMaxUsdc`. |
| `composeStack`   | 0.50 USDC    | LLM-curated multi-offering stack for a buyer's stated use case.               |
| `watchOffering`  | 0.50 USDC    | Standing semantic search delivered via buyer-supplied HTTPS webhook over a 1–30 day window. |

Built off the BasicBot boilerplate. Live on Base mainnet. Design specs:

- `docs/design.md` — overall architecture
- `docs/superpowers/specs/2026-04-26-watchoffering-design.md` — watchOffering spec

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

Default is `Indexer:Source=acp-api`, which paginates the upstream
`https://acpx.virtuals.io/` endpoint. A `json-file` source is also
supported for offline dev — see `docs/technical-specifications.md`.

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
4. Register all three offerings (`search`, `composeStack`, `watchOffering`)
   on app.virtuals.io. Print the registration JSON with:
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

## Security posture

- **`X-API-Key` between sidecar and C# API.** Required on every endpoint
  except `/health`. Fail-closed if missing or wrong.
- **SSRF guard on `webhookUrl`.** Resolves and rejects RFC1918, loopback,
  link-local (incl. cloud metadata `169.254.169.254`), CGNAT, multicast,
  reserved, and IPv6 site-/link-local/unique-local/multicast. Re-validated
  before every webhook delivery to defend against DNS rebinding.
- **Evaluator-zero enforcement.** Seller refuses jobs with
  `evaluatorAddress != 0x0` to prevent take-and-reject griefing.
- **Input length caps.** `query` 2 KB, `useCase` 4 KB, `webhookUrl` 2 KB.
  Indexer truncates third-party `description` to 4 KB.
- **Prompt-injection defense.** `composeStack` wraps untrusted content in
  delimiter tags and sanitizes closing-tag breakouts and code fences.
  System prompt instructs Claude to treat tagged content as data.

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
