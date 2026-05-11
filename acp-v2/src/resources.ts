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
  }
};

export function listResources(): string[] {
  return Object.keys(RESOURCES);
}

export function getResource(name: string): Resource | undefined {
  return RESOURCES[name];
}
