# ACP_Metabot — Semantic Search for the ACP Marketplace

A meta-bot for the Virtuals Protocol ACP marketplace. Indexes every offering across all agents, embeds them, and exposes two ACP offerings:

| Name           | Price        | What it does                                                 |
|----------------|--------------|--------------------------------------------------------------|
| `search`       | 0.05 USDC    | Semantic search over the ACP marketplace                     |
| `composeStack` | 0.20 USDC    | LLM-curated multi-offering stack for a buyer's use case      |

Built off the BasicBot boilerplate. See `docs/superpowers/specs/2026-04-25-acp-metabot-semantic-search-design.md` for the full design.

## Architecture

```
acp-v2/   (Node 22 / TypeScript)             ACP_Metabot.Api/   (.NET 10)
├─ search.ts        ──┐                     ├─ POST /search          → SearchService
├─ composeStack.ts  ──┤── HTTP ──►          ├─ POST /composeStack    → StackComposerService
└─ seller.ts        ──┘                     ├─ GET  /health
                                            ├─ GET  /index/stats
                                            ├─ POST /index/refresh   (operator)
                                            └─ MarketplaceIndexerService (BackgroundService)
                                                  └─ SQLite (offerings + embeddings)
```

The TS sidecar speaks the ACP v2 protocol (the SDK is Node-only). The C# API holds the indexer, vector search, and Claude composer.

## Prerequisites

- .NET 10 SDK
- Node.js 22+
- Docker / Docker Compose (for production)
- A **Voyage AI** API key (for embeddings) — https://www.voyageai.com/
- An **Anthropic** API key (for `composeStack`) — https://console.anthropic.com/

## Local development

Three terminals:

```bash
# Terminal 1 — C# API on http://localhost:5000
cd ACP_Metabot.Api
export VOYAGE_API_KEY=pa-...
export ANTHROPIC_API_KEY=sk-ant-...
dotnet run
```

```bash
# Terminal 2 — ACP sidecar (watches for TS changes)
cd acp-v2
cp .env.example .env       # then fill in agent credentials
# IMPORTANT: for local dev, set ACP_METABOT_API_URL=http://localhost:5000 in .env
npm install
npm run dev
```

```bash
# Terminal 3 — manually drive the API (optional)
curl http://localhost:5000/health
curl -X POST http://localhost:5000/index/refresh    # triggers an immediate fetch
curl http://localhost:5000/index/stats
curl -X POST http://localhost:5000/search \
  -H "Content-Type: application/json" \
  -d '{"query":"close a trading position on a perp DEX","limit":5}'
curl -X POST http://localhost:5000/composeStack \
  -H "Content-Type: application/json" \
  -d '{"useCase":"build a safe token-buy bot","maxOfferings":4}'
```

## Marketplace data source

The C# tier needs a source of "all current ACP offerings". V1 ships a
**JSON file source** (`data/seed/offerings.example.json`) — the operator
populates this manually from `app.virtuals.io/acp/scan/offerings`.

When the upstream ACP marketplace JSON API is identified, swap in
`AcpApiMarketplaceSource` (a stub `// TODO:` is in
`Services/MarketplaceSource/JsonFileMarketplaceSource.cs`).

## Provisioning the agent

1. Go to https://app.virtuals.io/acp/agents/, create or upgrade an agent to V2.
2. From the **Signers** tab, copy `walletId` and `signerPrivateKey`.
3. Paste into `acp-v2/.env`:
   ```
   ACP_WALLET_ADDRESS=0x...
   ACP_WALLET_ID=...
   ACP_SIGNER_PRIVATE_KEY=0x...
   ACP_CHAIN=baseSepolia
   ```
4. Register offerings:
   ```bash
   cd acp-v2
   npm run print-offerings
   ```
   Copy each printed block (search and composeStack) into
   **Offerings → New offering** in the dashboard.

## Production (Linux / Docker)

```bash
git clone <your-repo>
cd ACP_Metabot
cp acp-v2/.env.example acp-v2/.env  # then fill in credentials
export VOYAGE_API_KEY=pa-...
export ANTHROPIC_API_KEY=sk-ant-...
docker compose up -d --build
docker compose logs -f acp-metabot-acp
```

The API has no published ports — only the sidecar talks to it on the
internal `acp-metabot` bridge network. SQLite persists to
`./data/acp_metabot.db` on the host. The seed JSON lives at
`./data/seed/offerings.json`.

## Design decisions worth knowing

- **Embeddings:** Voyage `voyage-3-large` (1024-dim). Anthropic doesn't
  ship a public embeddings endpoint; Voyage is Anthropic's recommended
  partner. Swap path documented in the design spec.
- **Vector search:** in-memory cosine across all rows. Fine up to ~50K
  offerings. Re-evaluate (e.g. add `sqlite-vss`) past that.
- **Claude:** Sonnet 4.6 with prompt caching on the system prompt.
- **Subscription tiers** (`watchCategory`, `apiAccess`, `dataLicense`) are
  deferred to v2 — ACP doesn't natively support recurring billing.

## What's intentionally not in this shell

- No `sqlite-vss` extension (in-memory cosine is plenty at current scale)
- No subscription / recurring billing (ACP v2 limitation)
- No EF Core (using classic ADO.NET per workspace convention)
- No Redis (per workspace convention)
- No real ACP marketplace API client wired up (JSON file source for v1)
- No tests — add per project as needed
