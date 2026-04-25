# ACP_Metabot — ACP v2 Sidecar

Node.js sidecar that speaks ACP v2 via `@virtuals-protocol/acp-node-v2`, dispatches the two ACP_Metabot offerings (`search` and `composeStack`), and proxies execution to the C# `ACP_Metabot.Api`.

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

Copy each printed block into app.virtuals.io → ACP_Metabot agent → Offerings → New offering.

## Layout

- `src/seller.ts` — entry point
- `src/offerings/` — `search.ts`, `composeStack.ts`
- `src/pricing.ts` — USDC price table (search 0.05, composeStack 0.20)
- `src/deliverable.ts` — inline vs URL deliverables (50 KB threshold)
- `src/apiClient.ts` — typed HTTP client for the C# API
- `src/router.ts` — validate + dispatch
