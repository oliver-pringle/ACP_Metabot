import type { Offering } from "./types.js";

// v1.9 marketplaceGap ($0.30)  -  "where should I build a new ACP bot?"
//
// Repackages the saturationMap surfaced for free in /digest into a ranked,
// recommendation-tagged opportunity list. The buyer pays for the ranking +
// taxonomy, not the underlying duplicate-density data.
//
// v1.10.1  -  accepts marketplace ∈ {v1, v2, both}. Default flipped from
// pre-v1.10.1 "both" to "v2" because V2 is the marketplace where new ACP
// bots actually deploy. Pass marketplace: "both" to recover the prior
// combined-corpus behaviour.
//
// Offering name 14 chars  -  under marketplace 20-char cap.
export const marketplaceGap: Offering = {
  name: "marketplaceGap",
  description:
    "Ranks ACP marketplace categories by an opportunity score (lower saturation + reasonable volume = higher score) and tags each with a recommendation: high_volume_low_density, medium_volume_emerging, niche_underserved, balanced, or saturated_avoid. v1.10.1 adds a marketplace slice (v1/v2/both, default v2  --  the marketplace new ACP bots actually deploy to). Pass marketplace:both for the pre-v1.10.1 combined-corpus view. Optional category filter for deep-dive on a single category.",
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
      marketplace: {
        type: "string",
        enum: ["v1", "v2", "both"],
        description:
          "Which marketplace pool to compute saturation against. 'v1' = acpx.virtuals.io legacy pool. 'v2' (default) = api.acp.virtuals.io modern pool  --  the relevant denominator for new ACP-v2 bot decisions. 'both' = combined pool, matches the pre-v1.10.1 unfiltered response. Near-duplicate edges count across marketplaces  --  a V1↔V2 near-dup pair marks both ids as saturated in their respective slice.",
      },
    },
    required: [],
  },
  requirementExample: {
    limit: 5,
    marketplace: "v2",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["opportunities", "marketplace", "computedAt"],
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
            total: { type: "integer", description: "Number of offerings in this category WITHIN the requested marketplace slice." },
            saturatedCount: { type: "integer", description: "Offerings (in slice) with at least one near-duplicate within the category (near-dup edges cross marketplaces)." },
            saturationPct: { type: "number", description: "Duplicate density of the slice, 0.0-1.0 (e.g. 0.65 = 65% near-duplicate)." },
            opportunityScore: { type: "number", description: "total * (1 - saturationPct)^2. Roughly 'offerings of headroom' within the slice." },
            recommendationTag: {
              type: "string",
              enum: [
                "saturated_avoid",
                "high_volume_low_density",
                "medium_volume_emerging",
                "niche_underserved",
                "balanced",
              ],
              description: "Decision-grade taxonomy: avoid, prime, solid, small-but-open, neutral. Thresholds are global  --  when marketplace=v2 is selected most categories will fall into niche_underserved or balanced (V2 has lower per-category density today).",
            },
          },
        },
      },
      filter: {
        type: "string",
        nullable: true,
        description: "Echo of the category filter argument, null when unfiltered.",
      },
      marketplace: {
        type: "string",
        enum: ["v1", "v2", "both"],
        description: "Echo of the resolved marketplace slice ('v2' when the caller omitted the field).",
      },
      note: {
        type: "string",
        nullable: true,
        description: "Set when saturationMap has not yet been computed (cold-boot) OR when the requested marketplace slice contains zero offerings; null otherwise.",
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
        total: 88,
        saturatedCount: 22,
        saturationPct: 0.25,
        opportunityScore: 49.5,
        recommendationTag: "medium_volume_emerging",
      },
      {
        category: "Stablecoin Analytics",
        description: "Stablecoin reserve analysis, peg monitoring, depeg risk assessment, supply tracking.",
        total: 18,
        saturatedCount: 4,
        saturationPct: 0.2222,
        opportunityScore: 10.89,
        recommendationTag: "niche_underserved",
      },
      {
        category: "Liquidity and AMM",
        description: "Liquidity pool analysis, AMM dynamics, LP position management, impermanent loss.",
        total: 7,
        saturatedCount: 1,
        saturationPct: 0.1429,
        opportunityScore: 5.14,
        recommendationTag: "niche_underserved",
      },
    ],
    filter: null,
    marketplace: "v2",
    note: null,
    computedAt: "2026-05-25T12:00:00Z",
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
    if (req.marketplace !== undefined && req.marketplace !== null) {
      if (typeof req.marketplace !== "string") {
        return { valid: false, reason: "marketplace must be a string when set" };
      }
      const m = req.marketplace.trim().toLowerCase();
      if (m !== "v1" && m !== "v2" && m !== "both") {
        return { valid: false, reason: "marketplace must be one of: v1, v2, both" };
      }
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    const m = typeof req.marketplace === "string"
      ? (req.marketplace.trim().toLowerCase() as "v1" | "v2" | "both")
      : undefined;
    return await client.marketplaceGap({
      category:    typeof req.category === "string" ? req.category : undefined,
      limit:       typeof req.limit === "number" ? req.limit : undefined,
      marketplace: m,
    });
  },
};
