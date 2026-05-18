import type { Offering } from "./types.js";

// v1.9 marketplaceGap ($0.30) — "where should I build a new ACP bot?"
//
// Repackages the saturationMap surfaced for free in /digest into a ranked,
// recommendation-tagged opportunity list. The buyer pays for the ranking +
// taxonomy, not the underlying duplicate-density data.
//
// Offering name 14 chars — under marketplace 20-char cap.
export const marketplaceGap: Offering = {
  name: "marketplaceGap",
  description:
    "Ranks ACP marketplace categories by an opportunity score (lower saturation + reasonable volume = higher score) and tags each with a recommendation: high_volume_low_density, medium_volume_emerging, niche_underserved, balanced, or saturated_avoid. Use to decide where a new ACP bot has the most headroom. Optional category filter for deep-dive on a single category.",
  requirementSchema: {
    type: "object",
    properties: {
      category: {
        type: "string",
        description:
          "Optional. Restrict the response to a single category (e.g. 'DEX Swap', 'Wallet Intelligence'). Use the canonical category names returned by /v1/resources/categories. Case-insensitive.",
      },
      limit: {
        type: "integer",
        description:
          "How many top-ranked categories to return (1-20). Default 5. Ignored when category is set (single-row response).",
        minimum: 1,
        maximum: 20,
      },
    },
    required: [],
  },
  requirementExample: {
    limit: 5,
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["opportunities", "computedAt"],
    properties: {
      opportunities: {
        type: "array",
        description: "Top categories sorted descending by opportunityScore.",
        items: {
          type: "object",
          required: [
            "category",
            "description",
            "total",
            "saturatedCount",
            "saturationPct",
            "opportunityScore",
            "recommendationTag",
          ],
          properties: {
            category: { type: "string", description: "Canonical category name." },
            description: { type: "string", description: "Plain-English category definition (from categories.json)." },
            total: { type: "integer", description: "Number of offerings classified into this category." },
            saturatedCount: { type: "integer", description: "Offerings with at least one near-duplicate within the category." },
            saturationPct: { type: "number", description: "Duplicate density, 0.0-1.0 (e.g. 0.65 = 65% near-duplicate)." },
            opportunityScore: { type: "number", description: "total * (1 - saturationPct)^2. Roughly 'offerings of headroom'." },
            recommendationTag: {
              type: "string",
              enum: [
                "saturated_avoid",
                "high_volume_low_density",
                "medium_volume_emerging",
                "niche_underserved",
                "balanced",
              ],
              description: "Decision-grade taxonomy: avoid, prime, solid, small-but-open, neutral.",
            },
          },
        },
      },
      filter: {
        type: "string",
        nullable: true,
        description: "Echo of the category filter argument, null when unfiltered.",
      },
      note: {
        type: "string",
        nullable: true,
        description: "Set when saturationMap has not yet been computed (cold-boot edge case); null otherwise.",
      },
      computedAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC timestamp of the snapshot.",
      },
    },
  },
  deliverableExample: {
    opportunities: [
      {
        category: "Alerts and Monitoring",
        description: "On-chain event alerts, price alerts, transaction notifications, webhook delivery.",
        total: 644,
        saturatedCount: 294,
        saturationPct: 0.4565,
        opportunityScore: 190.32,
        recommendationTag: "high_volume_low_density",
      },
      {
        category: "Stablecoin Analytics",
        description: "Stablecoin reserve analysis, peg monitoring, depeg risk assessment, supply tracking.",
        total: 118,
        saturatedCount: 42,
        saturationPct: 0.3559,
        opportunityScore: 48.96,
        recommendationTag: "high_volume_low_density",
      },
      {
        category: "Liquidity and AMM",
        description: "Liquidity pool analysis, AMM dynamics, LP position management, impermanent loss.",
        total: 27,
        saturatedCount: 8,
        saturationPct: 0.2963,
        opportunityScore: 13.36,
        recommendationTag: "niche_underserved",
      },
    ],
    filter: null,
    note: null,
    computedAt: "2026-05-18T12:00:00Z",
  },
  validate(req) {
    if (req.category !== undefined && req.category !== null) {
      if (typeof req.category !== "string" || req.category.trim().length === 0) {
        return { valid: false, reason: "category must be a non-empty string when set" };
      }
      if (req.category.length > 100) {
        return { valid: false, reason: "category must be <= 100 chars" };
      }
    }
    if (req.limit !== undefined && req.limit !== null) {
      if (typeof req.limit !== "number" || !Number.isInteger(req.limit)) {
        return { valid: false, reason: "limit must be an integer" };
      }
      if (req.limit < 1 || req.limit > 20) {
        return { valid: false, reason: "limit must be between 1 and 20" };
      }
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.marketplaceGap({
      category: typeof req.category === "string" ? req.category : undefined,
      limit: typeof req.limit === "number" ? req.limit : undefined,
    });
  },
};
