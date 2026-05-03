# ACP_Metabot — ACP v2 Sidecar

Node.js sidecar that speaks ACP v2 via `@virtuals-protocol/acp-node-v2`, dispatches the four ACP_Metabot offerings, and proxies execution to the C# `ACP_Metabot.Api`.

## Offerings

| Offering | Price (USDC) | Source file |
|---|---|---|
| `search` | 0.01 | `src/offerings/search.ts` |
| `composeStack` | 0.50 | `src/offerings/composeStack.ts` |
| `watchOffering` | 0.50 | `src/offerings/watchOffering.ts` |
| `agentReputation` | 0.05 | `src/offerings/agentReputation.ts` |

Live, authoritative pricing lives in `src/pricing.ts` — the table above is for orientation only; consult that file before quoting.

## Setup

1. Provision an ACP_Metabot agent in https://app.virtuals.io/acp/agents/ (V2).
2. From the Signers tab, copy `walletId` and `signerPrivateKey`.
3. Copy `.env.example` → `.env` and fill in credentials.
4. `npm install`
5. `npm run build` — typecheck.
6. `npm start` — runs the seller against the chain in `ACP_CHAIN`.

## Register offerings

V2 has no programmatic registration. Run:

```
npm run print-offerings
```

Copy each printed block into app.virtuals.io → ACP_Metabot agent → Offerings → New offering. Keep on-chain offering names ≤ 20 chars.

## Layout

- `src/seller.ts` — entry point
- `src/offerings/` — `search.ts`, `composeStack.ts`, `watchOffering.ts`, `agentReputation.ts`
- `src/pricing.ts` — canonical USDC price table (authoritative)
- `src/deliverable.ts` — inline vs URL deliverables (50 KB threshold)
- `src/apiClient.ts` — typed HTTP client for the C# API
- `src/router.ts` — validate + dispatch
