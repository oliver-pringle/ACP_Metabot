import type { Offering } from "./types.js";
import { requirePositiveIntOrNothing } from "../validators.js";

export const today: Offering = {
  name: "today",
  description:
    "Daily marketplace pulse. Returns new and trending ACP offerings, plus extended v1.7 fields: newAgents (agents that appeared for the first time in the window), churnRate (fraction of offerings that went inactive), cohortSurvival (weekly cohort survival curves, available only for windows ≥ 30 days — null otherwise), and saturationMap (per-category duplicate density). The days argument now accepts 1–90 (default 1). Optional marketplace filter ('v1' or 'v2').",
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
  deliverableSchema: {
    type: "object",
    required: ["windowDays", "windowStart", "snapshotComparison", "partial", "newOfferings", "gainers", "newAgents", "churnRate", "saturationMap", "computedAt"],
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
          count: { type: "integer" },
          agents: {
            type: "array",
            items: {
              type: "object",
              required: ["address", "name", "marketplace", "firstSeenAt", "offeringCount"],
              properties: {
                address: { type: "string" },
                name: { type: "string" },
                marketplace: { type: "string", enum: ["v1", "v2"] },
                firstSeenAt: { type: "string", format: "date-time" },
                offeringCount: { type: "integer" },
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
          churnedCount: { type: "integer" },
          baselineCount: { type: "integer" },
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
            cohortStart: { type: "string", format: "date-time" },
            cohortSize: { type: "integer" },
            surviving: { type: "integer" },
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
            category: { type: "string" },
            total: { type: "integer" },
            saturatedCount: { type: "integer" },
            saturationPct: { type: "number", description: "Percentage of offerings that are near-duplicates, 0–100." },
          },
        },
      },
      computedAt: { type: "string", format: "date-time" },
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
