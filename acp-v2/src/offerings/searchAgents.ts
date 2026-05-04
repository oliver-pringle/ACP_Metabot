import type { Offering } from "./types.js";
import { requireString, requirePositiveIntOrNothing } from "../validators.js";

export const searchAgents: Offering = {
  name: "searchAgents",
  description:
    "Search for ACP agents (not individual offerings) by name and capability. Returns ranked agents with their top offerings, cross-marketplace presence, and cached on-chain reputation score. Ranker upgraded to hybrid (BM25 + dense + Voyage rerank) in v1.7; fields topOfferings now records (offeringName, priceUsdc, marketplaceVersion), plus topOfferingNames mirror for backward compatibility. Optional marketplace filter restricts results to V1 or V2 agents.",
  requirementSchema: {
    type: "object",
    properties: {
      query: {
        type: "string",
        description: "Natural-language description of the type of agent you're looking for (e.g. 'DeFi evaluator', 'wallet intelligence').",
      },
      limit: {
        type: "integer",
        description: "Max number of agent results (1-50). Defaults to 5.",
        minimum: 1,
        maximum: 50,
      },
      marketplace: {
        type: "string",
        enum: ["v1", "v2"],
        description: "Optional. Restrict to agents that have offerings on this marketplace version.",
      },
    },
    required: ["query"],
  },
  deliverableSchema: {
    type: "object",
    required: ["query", "count", "agents"],
    properties: {
      query: { type: "string" },
      count: { type: "integer" },
      agents: {
        type: "array",
        items: {
          type: "object",
          required: ["agentAddress", "agentName", "score", "totalOfferings", "topOfferings", "totalJobs", "topOfferingNames", "marketplaces", "dominantMarketplace"],
          properties: {
            agentAddress: { type: "string", description: "Lowercased 0x-prefixed wallet address." },
            agentName: { type: "string" },
            score: { type: "number", description: "Post-rerank relevance score (opaque; higher = more relevant). Sort by this, don't interpret." },
            totalOfferings: { type: "integer" },
            topOfferings: {
              type: "array",
              description: "Top offerings as records (v1.7+).",
              items: {
                type: "object",
                required: ["offeringName", "priceUsdc", "marketplaceVersion"],
                properties: {
                  offeringName: { type: "string" },
                  priceUsdc: { type: "number" },
                  marketplaceVersion: { type: "string", enum: ["v1", "v2"] },
                },
              },
            },
            totalJobs: { type: "integer", description: "Total on-chain job count for this agent." },
            topOfferingNames: {
              type: "array",
              items: { type: "string" },
              description: "Backward-compat mirror of topOfferings names as a flat string array.",
            },
            marketplaces: {
              type: "array",
              items: { type: "string", enum: ["v1", "v2"] },
              description: "Sorted subset of marketplaces where this agent has at least one active offering.",
            },
            dominantMarketplace: {
              type: "string",
              enum: ["v1", "v2", "tied", "none"],
              description: "Marketplace with the most active offerings, or 'tied' / 'none'.",
            },
            agentScore: {
              type: "integer",
              minimum: 0,
              maximum: 100,
              description: "Cached on-chain behavioural reputation score (0-100). Omitted when not yet cached.",
            },
          },
        },
      },
    },
  },
  deliverableExample: {
    query: "DeFi evaluator",
    count: 2,
    agents: [
      {
        agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
        agentName: "DeFiEval",
        score: 0.91,
        totalOfferings: 3,
        topOfferings: [
          { offeringName: "evaluate_defi_agent", priceUsdc: 0.99, marketplaceVersion: "v2" },
          { offeringName: "defi_agent_deep_eval", priceUsdc: 5.00, marketplaceVersion: "v2" },
        ],
        totalJobs: 126,
        topOfferingNames: ["evaluate_defi_agent", "defi_agent_deep_eval"],
        marketplaces: ["v1", "v2"],
        dominantMarketplace: "v2",
        agentScore: 78,
      },
      {
        agentAddress: "0xcccccccccccccccccccccccccccccccccccccccc",
        agentName: "AgentEval",
        score: 0.74,
        totalOfferings: 6,
        topOfferings: [
          { offeringName: "trading_baseline", priceUsdc: 0.99, marketplaceVersion: "v2" },
        ],
        totalJobs: 41,
        topOfferingNames: ["trading_baseline"],
        marketplaces: ["v2"],
        dominantMarketplace: "v2",
      },
    ],
  },
  validate(req) {
    const q = requireString(req.query, "query", 1000);
    if (!q.valid) return q;
    const lim = requirePositiveIntOrNothing(req.limit, "limit", 50);
    if (!lim.valid) return lim;
    if (req.marketplace !== undefined && req.marketplace !== null) {
      if (req.marketplace !== "v1" && req.marketplace !== "v2") {
        return { valid: false, reason: "marketplace must be 'v1' or 'v2'" };
      }
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.searchAgents({
      query: String(req.query),
      limit: typeof req.limit === "number" ? req.limit : undefined,
      marketplace:
        req.marketplace === "v1" || req.marketplace === "v2"
          ? req.marketplace
          : undefined,
    });
  },
};
