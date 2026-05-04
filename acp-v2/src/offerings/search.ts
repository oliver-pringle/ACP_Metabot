import type { Offering } from "./types.js";
import {
  requireString,
  requirePositiveIntOrNothing,
  requirePositiveNumberOrNothing,
} from "../validators.js";

export const search: Offering = {
  name: "search",
  description:
    "Semantic search across the ACP marketplace. Given a natural-language query (e.g. \"close a position on GMX for under 0.20 USDC\"), returns ranked offerings with agent name, agent address, offering name, price, and a similarity score 0-1. Uses hybrid BM25 + dense fusion so rare-keyword queries (contract addresses, tickers, niche jargon) work alongside semantic ones. Optional filters: priceMaxUsdc, chain, minReputation, freshness. Response includes a bestMatch field when the top result scores above 0.8. Each offering hit now includes saturation (per-category near-duplicate count) and pricePercentile (within category × marketplace).",
  requirementSchema: {
    type: "object",
    properties: {
      query: {
        type: "string",
        description: "Natural-language description of the offering you're looking for."
      },
      limit: {
        type: "integer",
        description: "Max number of results (1-50). Defaults to 10.",
        minimum: 1,
        maximum: 50
      },
      minScore: {
        type: "number",
        description: "Optional minimum cosine similarity (0-1). Defaults to 0 (no filter)."
      },
      priceMaxUsdc: {
        type: "number",
        description: "Optional maximum priceUsdc; offerings above this price are excluded."
      },
      staleAfterDays: {
        type: "integer",
        description: "Optional. Excludes offerings whose hire count hasn't grown within this lookback window. Pass 0 or omit to disable. Superseded by `freshness` when both are supplied.",
        minimum: 0,
        maximum: 365
      },
      chain: {
        type: "array",
        items: { type: "string" },
        description: "Optional. Restrict to one or more chain ids (e.g. [\"base\",\"base-sepolia\"]). Case-insensitive; up to 8 entries."
      },
      minReputation: {
        type: "integer",
        description: "Optional. Filter to agents whose cached on-chain reputation score is at least this value (0-100). Agents not yet evaluated pass through (unindexed != bad).",
        minimum: 0,
        maximum: 100
      },
      freshness: {
        type: "integer",
        description: "Optional. Replacement for staleAfterDays — keep only offerings whose hire count has grown within the last N days.",
        minimum: 1,
        maximum: 365
      }
    },
    required: ["query"]
  },
  deliverableSchema: {
    type: "object",
    required: ["query", "count", "results"],
    properties: {
      query: { type: "string" },
      count: { type: "integer" },
      bestMatch: {
        type: "object",
        nullable: true,
        properties: {
          agentAddress: { type: "string" },
          offeringName: { type: "string" },
          score: { type: "number" },
        },
      },
      results: {
        type: "array",
        items: {
          type: "object",
          required: ["offeringId", "agentName", "agentAddress", "offeringName", "description", "priceUsdc", "priceType", "chain", "score"],
          properties: {
            offeringId: { type: "integer" },
            agentName: { type: "string" },
            agentAddress: { type: "string" },
            offeringName: { type: "string" },
            description: { type: "string" },
            priceUsdc: { type: "number" },
            priceType: { type: "string" },
            chain: { type: "string" },
            score: { type: "number" },
            saturation: {
              type: "object",
              description: "Per-category near-duplicate saturation. nearDuplicateCount = offerings with cosine similarity ≥ threshold in the same category; categorySize = total offerings in category.",
              properties: {
                nearDuplicateCount: { type: "integer" },
                categorySize: { type: "integer" },
              },
            },
            pricePercentile: {
              type: "object",
              description: "Price position within the same category × marketplace cohort. value = percentile 0-100 (null when peerN < lowNThreshold); lowN = true when fewer than 5 peers exist.",
              properties: {
                value: { type: "number", nullable: true },
                peerN: { type: "integer" },
                lowN: { type: "boolean" },
              },
            },
          },
        },
      },
    },
  },
  validate(req) {
    const q = requireString(req.query, "query", 2048);
    if (!q.valid) return q;
    const lim = requirePositiveIntOrNothing(req.limit, "limit", 50);
    if (!lim.valid) return lim;
    if (req.minScore !== undefined && req.minScore !== null) {
      if (typeof req.minScore !== "number" || req.minScore < 0 || req.minScore > 1) {
        return { valid: false, reason: "minScore must be a number between 0 and 1" };
      }
    }
    const priceMax = requirePositiveNumberOrNothing(req.priceMaxUsdc, "priceMaxUsdc");
    if (!priceMax.valid) return priceMax;
    if (req.staleAfterDays !== undefined && req.staleAfterDays !== null) {
      if (typeof req.staleAfterDays !== "number" || !Number.isInteger(req.staleAfterDays)
          || req.staleAfterDays < 0 || req.staleAfterDays > 365) {
        return { valid: false, reason: "staleAfterDays must be an integer between 0 and 365" };
      }
    }
    if (req.chain !== undefined && req.chain !== null) {
      if (!Array.isArray(req.chain)) {
        return { valid: false, reason: "chain must be an array of strings" };
      }
      if (req.chain.length > 8) {
        return { valid: false, reason: "chain accepts at most 8 entries" };
      }
      for (const c of req.chain) {
        if (typeof c !== "string" || c.length === 0 || c.length > 64) {
          return { valid: false, reason: "chain entries must be non-empty strings <= 64 chars" };
        }
      }
    }
    if (req.minReputation !== undefined && req.minReputation !== null) {
      if (typeof req.minReputation !== "number" || !Number.isInteger(req.minReputation)
          || req.minReputation < 0 || req.minReputation > 100) {
        return { valid: false, reason: "minReputation must be an integer between 0 and 100" };
      }
    }
    if (req.freshness !== undefined && req.freshness !== null) {
      if (typeof req.freshness !== "number" || !Number.isInteger(req.freshness)
          || req.freshness < 1 || req.freshness > 365) {
        return { valid: false, reason: "freshness must be an integer between 1 and 365" };
      }
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.search({
      query: String(req.query),
      limit: typeof req.limit === "number" ? req.limit : undefined,
      minScore: typeof req.minScore === "number" ? req.minScore : undefined,
      priceMaxUsdc:
        typeof req.priceMaxUsdc === "number" ? req.priceMaxUsdc : undefined,
      staleAfterDays:
        typeof req.staleAfterDays === "number" ? req.staleAfterDays : undefined,
      chain: Array.isArray(req.chain) ? (req.chain as string[]) : undefined,
      minReputation:
        typeof req.minReputation === "number" ? req.minReputation : undefined,
      freshness:
        typeof req.freshness === "number" ? req.freshness : undefined,
    });
  },
};
