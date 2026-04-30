# V2 Marketplace Source — TheMetaBot v1.3 design

**Date:** 2026-04-30
**Status:** Approved — proceeding with Phase 2; enumeration revised after probe
**Owner:** Oliver Pringle
**Bot:** ACP_Metabot

## Summary

TheMetaBot's indexer currently reads only the **ACP V1** marketplace
(`https://acpx.virtuals.io/api/metrics/skills`). It cannot see any ACP V2
agent — including TheMetaBot itself, DeFiEval, AgentEval, LiquidGuard,
or any of the rest of the V2 ecosystem.

This spec adds an **ACP V2 source** alongside V1. After this change the
indexer ingests both marketplaces, keys offerings by
`(marketplace_version, agent_address, offering_name)`, and every
read-side surface (`search`, `composeStack`, `digest`/`today`, agent
browse, watch poller) returns cross-version results by default. Callers
can scope to one marketplace via a new optional `marketplace` filter.

## Why

Today's investigation (this conversation) confirmed:

1. `GET /v1/agent/{0xecf9773b...}` 404s because TheMetaBot's `offerings`
   table has zero rows for its own wallet.
2. The indexer pulls from `https://acpx.virtuals.io/api/metrics/skills`.
   I scanned all 346 pages × 100 rows = 34,561 entries twice (sorted by
   `usageCount desc`, then by `createdAt desc`). Zero hits for
   TheMetaBot's wallet, name, or any of its four offering names.
3. The V2 SDK (`@virtuals-protocol/acp-node-v2 ^0.0.6`,
   `dist/core/constants.js`) hardcodes V2 server as
   `https://api.acp.virtuals.io`. `GET /agents/wallet/{addr}` returns
   TheMetaBot's full record with all four offerings on Base 8453, so V2
   has the data — TheMetaBot just isn't reading from there.

This means TheMetaBot is, by accident, a V1-only search engine while
marketing itself as **the** ACP search engine. As V2 grows the index
becomes increasingly stale relative to where new agents are launching,
and Oliver's own portfolio is invisible to its own product.

Earlier hotfix-scope note (2026-04-28) had flagged "TheMetaBot doesn't
surface in marketplace semantic search" and assumed the cause was poor
description text. Wrong: the offerings never enter the indexable corpus
at all. Description tweaks would not have helped.

## Scope and non-goals

**In scope:**

- New `AcpV2MarketplaceSource : IMarketplaceSource` reading from
  `https://api.acp.virtuals.io`.
- `MarketplaceIndexerService` runs both V1 and V2 sources each cycle,
  unions results, persists with a `marketplace_version` column.
- `offerings` schema migration: add `marketplace_version TEXT NOT NULL
  DEFAULT 'v1'` and extend the unique constraint.
- Cross-version-by-default reads. Optional `marketplace` filter on
  `/search`, `/digest`, agent browse, watch register, composeStack
  candidate pool.
- `acp-find` plugin + MCP server: optional `marketplace` argument on
  `acp_find` and `acp_today`.
- Doc lockstep across all 4 ACP repos + acp-find-plugin (incl.
  republish of `acp-find-mcp` to npm).

**Non-goals:**

- Migrating V1 callers off the V1 source. V1 is still ~34K offerings; we
  keep ingesting it.
- Indexing Base Sepolia or BSC Testnet V2. Phase-1 is Base mainnet only.
- An on-chain-events V2 source. Designed-in as a fallback (see Risks)
  but not built unless the auth path proves insufficient.
- A second Voyage API key. Current corpus growth + V2 expected size sit
  comfortably inside the existing 200M tokens/mo free tier; revisit only
  if the daily indexer logs show 429s. (Oliver offered one — banking it.)

## Architecture

```
                 ┌──────────────────────────────┐
                 │  MarketplaceIndexerService   │
                 │  (BackgroundService, 120s)   │
                 └──────────────┬───────────────┘
                                │
              ┌─────────────────┼─────────────────┐
              ▼                 ▼                 ▼
   ┌────────────────────┐  ┌──────────────┐  ┌─────────────┐
   │ V1: AcpApiMarket-  │  │ V2: AcpV2-   │  │ (future)    │
   │ placeSource        │  │ Marketplace- │  │ V2 on-chain │
   │ acpx.virtuals.io/  │  │ Source       │  │ events scan │
   │ api/metrics/skills │  │ api.acp.     │  │             │
   │                    │  │ virtuals.io  │  │             │
   └─────────┬──────────┘  └──────┬───────┘  └─────────────┘
             │                    │
             │  MarketplaceOfferingDto[]
             │  (now carries MarketplaceVersion)
             ▼                    ▼
                 ┌──────────────────────┐
                 │  OfferingRepository  │
                 │  upsert keyed on     │
                 │  (mv, addr, name)    │
                 └──────────────────────┘
```

### Schema diff (Db.cs)

```sql
ALTER TABLE offerings ADD COLUMN marketplace_version TEXT NOT NULL DEFAULT 'v1';
CREATE INDEX IF NOT EXISTS ix_offerings_mv ON offerings(marketplace_version);
-- Replace the existing UNIQUE(agent_address, offering_name) with:
--   UNIQUE(marketplace_version, agent_address, offering_name)
-- via SQLite's "create new table, copy, swap" idiom in a transaction.
```

This intentionally allows the same `(agent_address, offering_name)` to
exist in both V1 and V2. ACP V2 reuses Base wallets, and a few V1
agents have re-registered under V2 with the same name and same wallet
but different schemas, prices, and IDs. Treating them as one row would
clobber correct V2 data with stale V1 data on every cycle.

The FTS5 mirror table (`offerings_fts`) gains `marketplace_version` so
BM25 results can be filtered without a join. Existing FTS sync triggers
extended to mirror the new column.

### Read-side surfaces

| Endpoint | Default | New filter |
|---|---|---|
| `POST /search`, `/v1/search` | both versions, blended ranking | optional `marketplace: "v1"\|"v2"` |
| `POST /composeStack`, `/v1/composeStack` | candidate pool spans both | optional `marketplace` |
| `GET /digest`, `/v1/digest` (`acp_today`) | newOfferings + gainers across both | optional `marketplace` |
| `GET /agent/{addr}`, `/v1/agent/{addr}` | merges offerings from both versions | n/a — version is per offering, not per agent |
| `POST /watches` (watchOffering) | crawls both | optional `marketplace` in stored watch |
| `GET /v1/agentReputation`, `/v1/agentReputationHistory` | unchanged — reputation is on-chain, marketplace-agnostic | n/a |

`SearchResult` JSON gains a `marketplaceVersion` field per result so the
plugin can render a v1/v2 badge and so power-users can see at a glance
which side of the split they're getting.

`bestMatch` semantics unchanged.

### Auth flow (V2)

V2 read endpoints split into two camps:

- **Unauth-public:** `GET /agents/wallet/{addr}` and
  `GET /agents/search?query=...&chainIds=...` (verified empirically;
  used by SDK consumers but apparently not gated server-side).
- **Auth-required:** `GET /agents` (list-all) and everything under
  `/jobs`, `/chats`. Bearer token from
  `POST /auth/agent {walletAddress, signature, message, chainId}` where
  `message = "acp-auth:" + Date.now()` and `signature` is from the
  agent's signer over that message. Token is JWT; SDK refreshes inside
  60s of `exp`.

The C# indexer does not have an EVM signer in process today (the
sidecar holds the Privy signer). The cleanest split:

1. **Sidecar exposes a tiny internal endpoint** `GET /v2/auth-token`
   that returns a fresh Bearer (cached up to `exp - 60s`). Reuses the
   sidecar's existing `AcpClient` which already has signer ctx.
2. **C# `AcpV2MarketplaceSource`** calls the sidecar once per cycle to
   get the token, then hits `api.acp.virtuals.io` directly.

This keeps Privy WaaS strictly on the Node side and avoids porting the
P-256 / Privy signer flow into C#. The sidecar already runs on the
private docker bridge, so the token endpoint stays internal — no public
exposure.

### Enumeration strategy

**Probe findings (2026-04-30, see `acp-v2/scripts/probe-v2-agents.ts`):**

- `POST /auth/agent` with TheMetaBot's signer returns a 229-char Bearer
  cleanly. Auth flow is solid.
- `GET /agents` returns **401 even with a valid Bearer**, on every
  param variant tried (`?chainIds=8453`, `?pageSize=200`,
  `?page=1&limit=50`). The endpoint is admin/role-gated, not just
  auth-gated. **Plan A is dead.**
- `/agents/wallet/{addr}` works both with and without auth, returning
  the full agent record + offerings.
- `/agents/search?query=...&topK=N&chainIds=8453` honours `topK` up to
  a hard server ceiling of **49** (verified empirically: `topK=49`
  returns 49 rows, `topK=50+` plateau at 49). The SDK type
  `BrowseAgentParams.topK` confirms this is the supported knob.

**Final plan: A + B + C union, all three sources merged each cycle.**

**Source A — On-chain `JobCreated` event scan.**
The existing `ChainEventScanner` (production for the reputation
feature) already filters `JobCreated` by topic3 (`provider`) on the
V2 contract `0x238E541BfefD82238730D00a2208E5497F1832E0` on Base.
Extend it with a per-cycle pass that extracts the distinct set of
`provider` topic values across the full event log since
`Reputation:ContractDeployBlock`. Cache the resulting wallet set in
the existing reputation cache table (or a new
`v2_known_sellers` table) so we don't rescan the same blocks each
cycle — incremental scan from the last-seen block. Fans out to
`GET /agents/wallet/{addr}` to hydrate per-wallet offerings.

Catches every V2 agent that has been hired ≥1×.

**Source B — `/agents/search` keyword sweep.**
Curated seed list of ~80 keywords spanning English alphabet
(`a`,`b`,…,`z`), high-frequency English words (`the`,`a`,`for`,`with`),
crypto/DeFi domain terms (`token`,`swap`,`yield`,`stake`,`liquidity`,
`vault`,`bridge`,`oracle`,`bot`,`agent`,`signal`,`trade`,`alpha`,
`research`), and the canonical category list. Each query uses
`topK=Indexer:V2:KeywordSweepTopK` (default **49**, max useful value).
Union of distinct `walletAddress` values from all queries. Default
~80×49=3,920 record-fetches per sweep, deduped to wallet set
(experimentally 100s of distinct wallets for Base today).

Catches cold-start V2 agents that match common keywords and never-hired
agents that the on-chain scan misses.

**Source C — Hardcoded self-list.**
Config `Indexer:V2:KnownAgents` array seeded with TheMetaBot,
DeFiEval, AgentEval, LiquidGuard wallets. Always fanned out, even if
A and B both miss them. Cheap insurance — 4 extra `agents/wallet`
fetches per cycle.

The three sources produce a deduped wallet set. Each wallet → one
`agents/wallet/{addr}` fetch → `n` offerings. With ~500 V2 wallets and
~3 offerings each (rough Phase-1 estimate), that's ~1,500 offerings,
< 1 MB total payload, well inside Voyage embedding budget.

### Pricing / token / category mapping

V2 offering shape (from live `/agents/wallet/{0xecf9773b...}`):

```json
{
  "id": "uuid",
  "agentId": "uuid",
  "name": "search",
  "description": "...",
  "deliverable": "<JSON-as-string>",
  "requirements": "<JSON-as-string>",
  "slaMinutes": 5,
  "priceType": "fixed",
  "priceValue": 0.01,
  "requiredFunds": false,
  "isHidden": false
}
```

- `name`, `description`, `priceType`, `priceValue` map 1:1.
- V2 `requirements` is the JSON schema string we currently store in
  `requirement_schema_json`. Trim leading/trailing `"""` artifacts.
- V2 has no `usageCount` or `agentJobCount` field on the offering.
  Default to 0 on first index; the existing reputation pipeline already
  pulls these counts from on-chain events, so search ranking still
  works once a V2 agent has activity.
- V2 `agentName` from the parent agent record's `name` field.
- `chain` derives from the agent record's `chains[]` (filter `active=true`).
  Phase 1 only indexes `chainId == 8453` (Base mainnet) entries.

### Cross-version search ranking

Pure cosine + RFF with BM25 stays as-is. V2 results enter the same
candidate pool. `marketplace_version` is a result field, not a ranking
input — we do not bias toward V1 or V2. Reputation blending continues
to apply via `0.7·rrf + 0.3·(reputation/100)`; reputation is computed
the same way for both since it's on-chain.

## Plugin / MCP server changes

`acp-find-plugin` + `acp-find-mcp`:

- `acp_find`: new optional `marketplace?: "v1" | "v2"` parameter.
  Default omitted → search both. Behavior documented in skill +
  README + npm README.
- `acp_today`: same optional `marketplace` argument. Default both.
- `acp_browse_agent`: returns offerings tagged with
  `marketplaceVersion`. No new param.
- `acp_categories`: unchanged (categories are corpus-wide).

Plugin version bump: `0.1.6 → 0.2.0` (semantic-incompatible result
shape change — `marketplaceVersion` is new on every result entry).
`acp-find-mcp` npm package republished at `0.3.0`.

## Doc lockstep checklist

Per `feedback_acp_docs_in_lockstep.md` — every shipped surface change
must update **all** of:

- `ACP_Metabot/ACP_Metabot/README.md`
- `ACP_Metabot/ACP_Metabot/docs/user-guide.md`
- `ACP_Metabot/ACP_Metabot/docs/design.md`
- `ACP_Metabot/ACP_Metabot/docs/technical-specifications.md`
- `acp-find-plugin/README.md`
- `acp-find-plugin/skills/acp-find/SKILL.md`
- `acp-find-plugin/commands/*.md` (specifically `search.md`, `today.md`)
- `acp-find-plugin/mcp-server/README.md` (republished to npm)
- `acp-find-plugin/mcp-server/package.json` (version bump)
- `ACP_Tester/README.md` if any test-flow change

DeFiEval, AgentEval, LiquidGuard READMEs do not currently reference
TheMetaBot's marketplace coverage — leave them.

## Implementation phases

### Phase 1 — band-aid (today, 1–2 hr)

Goal: TheMetaBot's own four wallets become findable immediately, with
no schema migration or auth flow.

- New `AcpV2MarketplaceSource` calling only `GET /agents/wallet/{addr}`
  per known wallet from a hardcoded config list `Indexer:V2:KnownAgents`.
- Mark these rows in-memory with `marketplaceVersion = "v2"` but **store
  with the existing key** (no schema change yet) — risk: a name
  collision with a V1 agent at the same wallet would lose the V1 row.
  Mitigation: TheMetaBot, DeFiEval, AgentEval, LiquidGuard wallets do
  not exist on V1, verified by the 346-page sweep.
- No plugin changes.

This is a stopgap. Ships in one PR. Validates the V2 fetch + mapping
end-to-end before the larger schema work.

### Phase 2 — full V2 source (this week, 1–2 days)

- Schema migration in `Db.cs` (idempotent, transactional).
- `AcpV2MarketplaceSource` with sidecar token fetch + Plan A
  enumeration. Falls back to wildcard-keyword sweep + known-agents list
  while we determine `/agents` behavior.
- Cross-version `OfferingRepository` (composite-key upsert, version
  filter helpers).
- `marketplace` filter wired through search / digest / composeStack /
  watch / agent browse.
- Plugin v0.2.0 + npm republish at 0.3.0.
- All-doc lockstep update.

### Phase 3 — on-chain backstop (later, 1 day)

Only built if Plan A enumeration is insufficient. Reuses existing
`ChainEventScanner` against
`0x238E541BfefD82238730D00a2208E5497F1832E0` on Base.

## Risks / open questions

1. **What does authenticated `GET /agents` actually return?** Resolved
   in Phase 2 step 1 via a one-off probe. Fallbacks exist for all
   answers.
2. **Schema migration on the live droplet.** `ALTER TABLE … ADD COLUMN`
   with `DEFAULT 'v1'` is fine on SQLite. The
   "extend UNIQUE constraint" step needs the create-new-copy-swap
   idiom inside a `BEGIN IMMEDIATE` transaction. Tested in
   `DbMigrationTests` before deploy.
3. **FTS triggers.** Adding `marketplace_version` to the FTS mirror
   without breaking the v1.2 trigger scoping. Same pattern as the
   recent v1.2 re-ship: triggers limited to FTS-mirrored columns,
   covered by `DbMigrationTests` for the indexer touch pattern.
4. **Voyage rate-limit.** V2 corpus appears small (~hundreds of agents
   on Base today, vs 34K V1). Free tier 200M tokens/mo is
   ~67M offerings × 3K tokens-each — comfortably above any plausible
   marketplace size for the next year. No second key needed; reassess
   if the indexer logs show 429s.
5. **Sidecar internal token endpoint** widens the sidecar's surface
   slightly. Mitigation: bound to docker bridge only, no public route
   in Caddy, no rate-limit needed (called 1×/120s by C# indexer).
6. **Cross-version `composeStack` ranking.** When the LLM picks a stack
   spanning v1 and v2, it should know the version so it doesn't
   recommend a deprecated v1 offering when a v2 successor exists.
   Phase 2 includes the version in the prompt context.
7. **Buyers using v2-only / v1-only views.** The plugin's `marketplace`
   filter is the user-facing escape hatch; it's optional, defaults to
   "both" per Oliver's instruction.

## Open question for Oliver before implementing

Confirm Phase 1 ships first (closes the immediate gap on Oliver's own
portfolio in ~2 hr) before Phase 2 starts. Alternative: skip Phase 1
and go straight to Phase 2 (~2 days, no intermediate ship). Default to
Phase-1-first unless Oliver says otherwise.
