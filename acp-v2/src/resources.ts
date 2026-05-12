// ACP v2 Resources — public, free, parameterised endpoints that buyer /
// orchestrator agents (e.g. Butler) call BEFORE paying for an offering.
//
// First-class in @virtuals-protocol/acp-node-v2 ^0.0.6 as AcpAgentResource:
// { name, url, params, description }. Surfaced in a separate tab from
// offerings on the agent's app.virtuals.io profile.
//
// Metadata HERE (TypeScript) ↔ route handlers in ACP_Metabot.Api/Program.cs.
// The HTTP routes live under /v1/resources/* (public, IP rate-limited via the
// public-resources policy). The X-API-Key middleware ALREADY whitelists /v1/*
// so no middleware change is needed.
//
// `url` is the absolute path on the public gateway (api.acp-metabot.dev). The
// values below assume the live gateway; for local dev callers will rewrite
// the host portion themselves. Buyer agents register against the absolute URL.

export interface Resource {
  name: string;
  url: string;
  params: Record<string, unknown>;
  description: string;
}

const PUBLIC_BASE = "https://api.acp-metabot.dev";

export const RESOURCES: Record<string, Resource> = {
  searchStatus: {
    name: "searchStatus",
    url: `${PUBLIC_BASE}/v1/resources/searchStatus`,
    params: { type: "object", properties: {} },
    description:
      "Returns the live state of TheMetaBot's marketplace index: total " +
      "indexed offerings split by V1 and V2, the corpus refresh timestamp, " +
      "and the indexer's last successful fetch. Free, public, " +
      "parameterless. Lets buyer agents check freshness before paying " +
      "for `search` — a stale corpus means stale results."
  },
  capabilities: {
    name: "capabilities",
    url: `${PUBLIC_BASE}/v1/resources/capabilities`,
    params: { type: "object", properties: {} },
    description:
      "Returns the list of TheMetaBot's offerings with names, descriptions, " +
      "SLA minutes, and current USDC prices. Free, public, parameterless. " +
      "Lets buyer / orchestrator agents (Butler etc.) introspect what " +
      "TheMetaBot does end-to-end without paying for a `browseAgent` call."
  },
  chainCoverage: {
    name: "chainCoverage",
    url: `${PUBLIC_BASE}/v1/resources/chainCoverage`,
    params: { type: "object", properties: {} },
    description:
      "Returns the chains TheMetaBot operates on (where it accepts hires) " +
      "AND the chains its indexer covers (which marketplaces — V1, V2 — " +
      "are searchable per chain). Free, public, parameterless. Lets buyer " +
      "agents pre-check whether a search across a target chain will return " +
      "results."
  },

  // ── v1.7 Bundle A: Arena marketplace integration ──────────────────────────
  arenaParticipantCount: {
    name: "arenaParticipantCount",
    url: `${PUBLIC_BASE}/v1/resources/arenaParticipantCount`,
    params: { type: "object", properties: {} },
    description:
      "Total ACP agents TheMetaBot has cross-indexed against the Degen Arena " +
      "leaderboard, with the last observation timestamp. Refreshed by " +
      "ArenaSourceWorker (15-min cadence) from ArenaBot's free Resources. " +
      "Free, public, parameterless. Use to gauge how many of your candidate " +
      "agents have Arena performance signal before issuing paid hires."
  },
  lastArenaPollAt: {
    name: "lastArenaPollAt",
    url: `${PUBLIC_BASE}/v1/resources/lastArenaPollAt`,
    params: { type: "object", properties: {} },
    description:
      "Timestamp of the last successful Arena participation ingest, plus a " +
      "boolean `stale` flag (true when the last poll is > 1h old). Free, " +
      "public, parameterless. Lets buyer agents detect Arena-data outages " +
      "and degrade gracefully (skip the arenaParticipation enrichment when " +
      "stale)."
  },
  cohortOverlap: {
    name: "cohortOverlap",
    url: `${PUBLIC_BASE}/v1/resources/cohortOverlap`,
    params: { type: "object", properties: {} },
    description:
      "Of the Top-50 Arena agents TheMetaBot has indexed, how many also sell " +
      "ACP offerings on app.virtuals.io? Returns sampleSize + alsoSellOnAcp " +
      "+ overlapFraction. Free, public, parameterless. A high overlap signals " +
      "that Arena performance correlates with marketplace presence — useful " +
      "demand-side signal for buyers looking for credentialed sellers."
  },

  // ── v1.7 Bundle B: Buyer Agent Toolkit (R6-IDEA-4 promoted) ───────────────
  buyerWalletDelegationCheck: {
    name: "buyerWalletDelegationCheck",
    url: `${PUBLIC_BASE}/v1/resources/buyerWalletDelegationCheck`,
    params: { type: "object", properties: {} },
    description:
      "How to verify a buyer wallet has the EIP-7702 delegation the ACP v2 " +
      "SDK requires before issuing any hire. Returns the expected " +
      "ModularAccountV2 delegation prefix, an eth_getCode probe template, " +
      "and the recovery path when drift is detected. Free, parameterless. " +
      "Note: TheMetaBot does NOT make the RPC call for you in v1.7 — this " +
      "is the canonical procedure for the buyer agent to self-verify."
  },
  buyerUsdcReadiness: {
    name: "buyerUsdcReadiness",
    url: `${PUBLIC_BASE}/v1/resources/buyerUsdcReadiness`,
    params: { type: "object", properties: {} },
    description:
      "How to verify a buyer smart-account wallet holds enough USDC on its " +
      "target chain before attempting an ACP hire. Returns the canonical " +
      "USDC contract addresses + balanceOf call shape for Base, Base Sepolia, " +
      "and Ethereum mainnet, plus the Privy WaaS smart-account-vs-owner-EOA " +
      "reminder. Free, parameterless."
  },
  offeringSchemaTemplate: {
    name: "offeringSchemaTemplate",
    url: `${PUBLIC_BASE}/v1/resources/offeringSchemaTemplate`,
    params: {
      type: "object",
      properties: {
        offeringId: { type: "integer", description: "Marketplace offering id (from search / browseAgent results)" }
      },
      required: ["offeringId"]
    },
    description:
      "Returns the requirement schema (JSON Schema) TheMetaBot has indexed " +
      "for a given offering, so a buyer agent can pre-validate its " +
      "requirement payload BEFORE paying the seller for a failed hire. " +
      "Free; one query param (offeringId). Returns the offering's name, " +
      "agent address, marketplace version, USDC price, and schema."
  },
  supportedChainsByCategory: {
    name: "supportedChainsByCategory",
    url: `${PUBLIC_BASE}/v1/resources/supportedChainsByCategory`,
    params: { type: "object", properties: {} },
    description:
      "Canonical category list + the default chains the marketplace operates " +
      "on. Lets a buyer agent quickly check 'which chains is X category " +
      "active on?' without paying for a search. Free, parameterless. v1.7 " +
      "returns static defaults; v1.8 will rollup per-category chain " +
      "breakdowns from the live offerings corpus."
  },

  // ── v1.7 Bundle C: Seller-Success Coach + V1↔V2 portage ───────────────────
  sellerDiagnose: {
    name: "sellerDiagnose",
    url: `${PUBLIC_BASE}/v1/resources/sellerDiagnose`,
    params: {
      type: "object",
      properties: {
        agent: { type: "string", pattern: "^0x[0-9a-fA-F]{40}$", description: "Agent address to diagnose" }
      },
      required: ["agent"]
    },
    description:
      "Free pre-sales scan of an ACP agent's marketplace presence. Returns " +
      "a checklist verdict (HEALTHY / ISSUES_FOUND / NOT_INDEXED) plus " +
      "specific issues found: missing requirement schemas, name-length " +
      "violations, sub-min prices, too-short descriptions, missing Resources. " +
      "Use this to coach a seller before they ship — or to identify gaps " +
      "in a competitor's offering registry."
  },
  marketplaceVersionMap: {
    name: "marketplaceVersionMap",
    url: `${PUBLIC_BASE}/v1/resources/marketplaceVersionMap`,
    params: {
      type: "object",
      properties: {
        agent: { type: "string", pattern: "^0x[0-9a-fA-F]{40}$", description: "Agent address" }
      },
      required: ["agent"]
    },
    description:
      "Returns which marketplaces (V1 / V2) an agent has offerings on, with " +
      "per-marketplace offering counts and a dominantMarketplace verdict. " +
      "Includes a migration hint when the agent is V1-only — pointing at the " +
      "v1Tov2Migration paid offering as the next step."
  }
};

export function listResources(): string[] {
  return Object.keys(RESOURCES);
}

export function getResource(name: string): Resource | undefined {
  return RESOURCES[name];
}
