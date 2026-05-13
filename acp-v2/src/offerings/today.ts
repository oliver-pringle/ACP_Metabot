import type { Offering } from "./types.js";
import { requirePositiveIntOrNothing } from "../validators.js";

export const today: Offering = {
  name: "today",
  description:
    "Daily marketplace pulse. Returns new and trending ACP offerings, **newResources** (v1.7.4: free V2 Resources first seen in the window — buyer-orchestrator pre-hire surface that's growing in parallel with paid offerings), plus extended v1.7 fields: newAgents (agents that appeared for the first time in the window), churnRate (fraction of offerings that went inactive), cohortSurvival (weekly cohort survival curves, available only for windows ≥ 30 days — null otherwise), and saturationMap (per-category duplicate density). The days argument now accepts 1–90 (default 1). Optional marketplace filter ('v1' or 'v2').",
  requirementSchema: {
    type: "object",
    properties: {
      days: {
        type: "integer",
        description: "Lookback window in days (1-90). Defaults to 1 (today only).",
        minimum: 1,
        maximum: 90,
      },
      marketplace: {
        type: "string",
        enum: ["v1", "v2"],
        description: "Optional. Restrict to offerings on this marketplace version.",
      },
    },
    required: [],
  },
  requirementExample: {
    days: 7,
    marketplace: "v2",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["windowDays", "windowStart", "snapshotComparison", "partial", "newOfferings", "newResources", "gainers", "newAgents", "churnRate", "saturationMap", "computedAt"],
    properties: {
      windowDays: { type: "integer", description: "Lookback window in days." },
      windowStart: { type: "string", format: "date-time", description: "ISO-8601 UTC start of the window." },
      snapshotComparison: { type: "string", description: "Human-readable summary comparing snapshots." },
      partial: { type: "boolean", description: "True when the snapshot coverage for this window is incomplete (e.g. indexer hasn't run long enough)." },
      newOfferings: {
        type: "array",
        description: "Offerings first seen within the window.",
        items: { type: "object" },
      },
      newResources: {
        type: "array",
        description: "v1.7.4: free V2 Resources (AcpAgentResource: name+url+params+description) first seen within the window across the indexed corpus. Resources are buyer-orchestrator pre-hire endpoints — discovery surface adjacent to paid offerings. V1 marketplace has no Resources surface so this is V2-only.",
        items: {
          type: "object",
          required: ["agentName", "agentAddress", "name", "url", "description", "firstSeenAt", "marketplaceVersion"],
          properties: {
            agentName:          { type: "string", description: "Marketplace display name of the agent that publishes the Resource" },
            agentAddress:       { type: "string", description: "Lowercased 0x-prefixed wallet address of the publishing agent" },
            name:               { type: "string", description: "Resource name (≤30 chars per marketplace cap)" },
            url:                { type: "string", description: "Absolute HTTPS endpoint buyers call to invoke the Resource" },
            description:        { type: "string", description: "Resource description from the agent's app.virtuals.io registration" },
            firstSeenAt:        { type: "string", format: "date-time", description: "ISO-8601 UTC timestamp the Resource was first observed in the indexer" },
            marketplaceVersion: { type: "string", enum: ["v2"], description: "Always 'v2' in v1.7.4." },
          },
        },
      },
      gainers: {
        type: "array",
        description: "Offerings with the highest hire-count growth in the window.",
        items: { type: "object" },
      },
      newAgents: {
        type: "object",
        required: ["count", "agents"],
        description: "Agents that appeared on the marketplace for the first time in the window.",
        properties: {
          count: { type: "integer", description: "Number of agents that first appeared within the window" },
          agents: {
            type: "array",
            description: "List of agents that first appeared within the window",
            items: {
              type: "object",
              required: ["address", "name", "marketplace", "firstSeenAt", "offeringCount"],
              properties: {
                address: { type: "string", description: "Lowercased 0x-prefixed wallet address of the agent" },
                name: { type: "string", description: "Marketplace display name of the agent" },
                marketplace: { type: "string", enum: ["v1", "v2"], description: "ACP marketplace where the agent first appeared" },
                firstSeenAt: { type: "string", format: "date-time", description: "ISO-8601 UTC timestamp the agent was first observed" },
                offeringCount: { type: "integer", description: "Number of active offerings the agent has now" },
              },
            },
          },
        },
      },
      churnRate: {
        type: "object",
        required: ["rate", "churnedCount", "baselineCount"],
        description: "Fraction of offerings that went inactive (tombstoned) in the window relative to the start-of-window total.",
        properties: {
          rate: { type: "number", description: "Churn rate 0.0–1.0." },
          churnedCount: { type: "integer", description: "Number of offerings that went inactive within the window" },
          baselineCount: { type: "integer", description: "Total active offerings at the start of the window" },
        },
      },
      cohortSurvival: {
        type: "array",
        nullable: true,
        description: "Weekly cohort survival curves. null when the window is shorter than 30 days (insufficient data).",
        items: {
          type: "object",
          required: ["cohortWeek", "cohortStart", "cohortSize", "surviving", "survivalRate"],
          properties: {
            cohortWeek: { type: "string", description: "ISO week string (e.g. '2026-W12')." },
            cohortStart: { type: "string", format: "date-time", description: "ISO-8601 UTC timestamp of the start of the cohort week" },
            cohortSize: { type: "integer", description: "Number of offerings that joined the marketplace in this week" },
            surviving: { type: "integer", description: "Number of those offerings still active at the end of the window" },
            survivalRate: { type: "number", description: "Fraction of the cohort still active, 0.0–1.0." },
          },
        },
      },
      saturationMap: {
        type: "array",
        description: "Per-category duplicate density. saturatedCount = offerings with at least one near-duplicate in the same category.",
        items: {
          type: "object",
          required: ["category", "total", "saturatedCount", "saturationPct"],
          properties: {
            category: { type: "string", description: "Canonical marketplace category id (e.g. 'wallet-intelligence')" },
            total: { type: "integer", description: "Total offerings in the category" },
            saturatedCount: { type: "integer", description: "Offerings with at least one near-duplicate in the same category" },
            saturationPct: { type: "number", description: "Percentage of offerings that are near-duplicates, 0–100." },
          },
        },
      },
      computedAt: { type: "string", format: "date-time", description: "ISO-8601 UTC timestamp the digest was computed" },
    },
  },
  deliverableExample: {
    windowDays: 1,
    windowStart: "2026-05-03T00:00:00Z",
    snapshotComparison: "Compared 2026-05-03 → 2026-05-04: 4 new offerings, 12 gainers.",
    partial: false,
    newOfferings: [
      {
        offeringId: 6201,
        agentName: "RevokeBot",
        agentAddress: "0xdddddddddddddddddddddddddddddddddddddddd",
        offeringName: "wallet_scan",
        description: "Scan a wallet for risky token approvals.",
        priceUsdc: 0.10,
        priceType: "per-call",
        chain: "base",
        firstSeenAt: "2026-05-03T18:22:11Z",
        marketplaceVersion: "v2",
      },
    ],
    newResources: [
      {
        agentName: "TheMetaBot",
        agentAddress: "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c",
        name: "search",
        url: "https://api.acp-metabot.dev/v1/resources/search",
        description: "Semantic search across every offering in the V1 + V2 ACP marketplaces. Returns ranked matches with prices, reputation, and marketplace URLs.",
        firstSeenAt: "2026-05-13T00:18:00Z",
        marketplaceVersion: "v2",
      },
    ],
    gainers: [
      {
        offeringId: 4421,
        agentName: "DeFiEval",
        agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
        offeringName: "evaluate_defi_agent",
        hiresThen: 12,
        hiresNow: 18,
        delta: 6,
        marketplaceVersion: "v2",
      },
    ],
    newAgents: {
      count: 1,
      agents: [
        {
          address: "0xdddddddddddddddddddddddddddddddddddddddd",
          name: "RevokeBot",
          marketplace: "v2",
          firstSeenAt: "2026-05-03T18:00:00Z",
          offeringCount: 4,
        },
      ],
    },
    churnRate: { rate: 0.012, churnedCount: 4, baselineCount: 332 },
    cohortSurvival: null,
    saturationMap: [
      { category: "wallet-intelligence", total: 22, saturatedCount: 9, saturationPct: 40.9 },
      { category: "defi-evaluation",     total: 14, saturatedCount: 3, saturationPct: 21.4 },
    ],
    computedAt: "2026-05-04T12:00:00Z",
  },
  validate(req) {
    const d = requirePositiveIntOrNothing(req.days, "days", 90);
    if (!d.valid) return d;
    if (req.marketplace !== undefined && req.marketplace !== null) {
      if (req.marketplace !== "v1" && req.marketplace !== "v2") {
        return { valid: false, reason: "marketplace must be 'v1' or 'v2'" };
      }
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.digest({
      days: typeof req.days === "number" ? req.days : undefined,
      marketplace:
        req.marketplace === "v1" || req.marketplace === "v2"
          ? req.marketplace
          : undefined,
    });
  },
};
