import type { Offering } from "./types.js";
import { requireString } from "../validators.js";

export const browseAgent: Offering = {
  name: "browseAgent",
  description:
    "Full agent profile by wallet address. Returns all active offerings with price, description, requirement schema, reputation summary, and a per-offering pricePercentile (position within the same category × marketplace cohort). Response now includes crossPresence (V1/V2 footprint: offering count, first/last seen per marketplace, inBoth flag, dominant marketplace).",
  requirementSchema: {
    type: "object",
    properties: {
      agentAddress: {
        type: "string",
        description:
          "Wallet address of the agent to browse. Lower- or mixed-case is fine; will be normalised.",
      },
    },
    required: ["agentAddress"],
  },
  requirementExample: {
    agentAddress: "0x693a237221e760bC7ff4968B74e25dCA17234633",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["agentAddress", "agentName", "reputation", "offerings"],
    properties: {
      agentAddress: { type: "string", description: "Lowercased 0x-prefixed wallet." },
      agentName: { type: "string", description: "Marketplace display name of the agent" },
      reputation: { type: "object", description: "Reputation summary for the agent." },
      offerings: {
        type: "array",
        description: "Active offerings sorted by hire count descending.",
        items: {
          type: "object",
          required: ["offeringId", "offeringName", "description", "priceUsdc", "priceType", "chain", "isPrivate", "firstSeenAt", "lastSeenAt", "marketplaceVersion"],
          properties: {
            offeringId: { type: "integer", description: "Internal numeric id of the offering in the index" },
            offeringName: { type: "string", description: "Marketplace name of the offering" },
            description: { type: "string", description: "Free-text description as registered on-chain" },
            priceUsdc: { type: "number", description: "Per-call USDC price" },
            priceType: { type: "string", description: "Pricing model (e.g. per-call, subscription)" },
            chain: { type: "string", description: "Chain id where the offering is registered" },
            isPrivate: { type: "boolean", description: "True when the offering is marked private (invitation-only)" },
            requirementSchema: { description: "JSON schema for the offering's requirement payload. May be null." },
            firstSeenAt: { type: "string", format: "date-time", description: "ISO-8601 UTC the offering was first indexed" },
            lastSeenAt: { type: "string", format: "date-time", description: "ISO-8601 UTC the offering was most recently observed active" },
            marketplaceVersion: { type: "string", enum: ["v1", "v2"], description: "ACP marketplace version this offering lives on" },
            pricePercentile: {
              type: "object",
              description: "Price position within the same category × marketplace cohort. value = percentile 0-100 (null when peerN < lowNThreshold); lowN = true when fewer than 5 peers exist.",
              properties: {
                value: { type: "number", nullable: true, description: "Percentile rank 0-100, null when peer set is too small" },
                peerN: { type: "integer", description: "Number of peer offerings used to compute the percentile" },
                lowN: { type: "boolean", description: "True when fewer than 5 peers exist" },
              },
            },
          },
        },
      },
      crossPresence: {
        type: "object",
        description: "V1/V2 cross-marketplace footprint for this agent.",
        properties: {
          v1: {
            type: "object",
            nullable: true,
            description: "Footprint on the V1 marketplace, or null when the agent has none",
            properties: {
              offeringCount: { type: "integer", description: "Number of active offerings on V1" },
              firstSeenAt: { type: "string", format: "date-time", description: "ISO-8601 UTC of first V1 sighting" },
              lastSeenAt: { type: "string", format: "date-time", description: "ISO-8601 UTC of most recent V1 sighting" },
            },
          },
          v2: {
            type: "object",
            nullable: true,
            description: "Footprint on the V2 marketplace, or null when the agent has none",
            properties: {
              offeringCount: { type: "integer", description: "Number of active offerings on V2" },
              firstSeenAt: { type: "string", format: "date-time", description: "ISO-8601 UTC of first V2 sighting" },
              lastSeenAt: { type: "string", format: "date-time", description: "ISO-8601 UTC of most recent V2 sighting" },
            },
          },
          inBoth: { type: "boolean", description: "True when the agent has active offerings on both V1 and V2." },
          dominant: {
            type: "string",
            enum: ["v1", "v2", "tied", "none"],
            description: "Marketplace with more active offerings, or 'tied' / 'none'.",
          },
        },
      },
    },
  },
  deliverableExample: {
    agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
    agentName: "DeFiEval",
    reputation: {
      agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
      agentName: "DeFiEval",
      agentScore: 78,
      agentTotalJobs: 126,
      agentPercentile: 82.4,
      computedAt: "2026-05-04T12:34:56Z",
    },
    offerings: [
      {
        offeringId: 4421,
        offeringName: "evaluate_defi_agent",
        description: "One-shot evaluation of a DeFi agent's on-chain track record.",
        priceUsdc: 0.99,
        priceType: "per-call",
        chain: "base",
        isPrivate: false,
        requirementSchema: { type: "object", properties: { agentAddress: { type: "string" } }, required: ["agentAddress"] },
        firstSeenAt: "2026-04-26T10:00:00Z",
        lastSeenAt: "2026-05-04T11:00:00Z",
        marketplaceVersion: "v2",
        pricePercentile: { value: 22.5, peerN: 18, lowN: false },
      },
    ],
    crossPresence: {
      v1: { offeringCount: 1, firstSeenAt: "2026-04-26T10:00:00Z", lastSeenAt: "2026-05-03T11:00:00Z" },
      v2: { offeringCount: 2, firstSeenAt: "2026-04-29T10:00:00Z", lastSeenAt: "2026-05-04T11:00:00Z" },
      inBoth: true,
      dominant: "v2",
    },
  },
  validate(req) {
    const addr = requireString(req.agentAddress, "agentAddress", 128);
    if (!addr.valid) return addr;
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.browseAgent(String(req.agentAddress));
  },
};
