import type { Offering } from "./types.js";

export const preHireBudgetCheck: Offering = {
  name: "preHireBudgetCheck",
  description:
    "Given 1-25 marketplace offering IDs, returns per-offering price + total USDC + any missing IDs. Lets a " +
    "buyer agent compute the exact escrow it will need to setBudget(...) across a multi-step stack before " +
    "issuing any individual hire. Deterministic  -  no LLM, no chain scan.",
  requirementSchema: {
    type: "object",
    properties: {
      offeringIds: {
        type: "array",
        items: { type: "integer", minimum: 1 },
        minItems: 1,
        maxItems: 25,
        description: "1-25 numeric offering IDs from search/browseAgent results.",
      },
    },
    required: ["offeringIds"],
  },
  requirementExample: {
    offeringIds: [4421, 4422, 5132],
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["requested", "resolved", "totalUsdc", "lines"],
    properties: {
      requested:  { type: "integer", description: "Number of offering IDs in the request." },
      resolved:   { type: "integer", description: "Number of IDs successfully resolved against the indexer (== lines.length)." },
      totalUsdc:  { type: "number", description: "Sum of per-call USDC prices across resolved offerings (no gas, no subscription tiers)." },
      lines: {
        type: "array",
        description: "Per-resolved-offering breakdown in request order (skipping unresolved IDs).",
        items: {
          type: "object",
          properties: {
            offeringId:         { type: "integer", description: "Echoes the requested offering ID." },
            offeringName:       { type: "string", description: "Marketplace name of the offering." },
            agentName:          { type: "string", description: "Marketplace display name of the seller agent." },
            agentAddress:       { type: "string", description: "Lowercased 0x-prefixed wallet of the seller." },
            priceUsdc:          { type: "number", description: "Per-call USDC price of the offering." },
            priceType:          { type: "string", description: "Pricing model, e.g. 'per-call' or 'subscription'." },
            marketplaceVersion: { type: "string", enum: ["v1", "v2"], description: "ACP marketplace the offering lives on." },
          },
        },
      },
      missingIds: { type: "array", items: { type: "integer" }, description: "Offering IDs that could not be resolved (deleted / never indexed)." },
      note:       { type: "string", description: "Plain-text caveats, e.g. 'subscription tiers and gas costs are NOT included'." },
    },
  },
  deliverableExample: {
    requested: 3,
    resolved: 2,
    totalUsdc: 1.04,
    lines: [
      {
        offeringId: 4421,
        offeringName: "evaluate_defi_agent",
        agentName: "DeFiEval",
        agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
        priceUsdc: 0.99,
        priceType: "per-call",
        marketplaceVersion: "v2",
      },
      {
        offeringId: 4422,
        offeringName: "recheck_defi_agent",
        agentName: "DeFiEval",
        agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
        priceUsdc: 0.05,
        priceType: "per-call",
        marketplaceVersion: "v2",
      },
    ],
    missingIds: [5132],
    note: "Per-call prices only; subscription tiers and gas costs are NOT included.",
  },
  validate(req) {
    if (!Array.isArray(req.offeringIds))
      return { valid: false, reason: "offeringIds must be an array" };
    if (req.offeringIds.length < 1 || req.offeringIds.length > 25)
      return { valid: false, reason: "offeringIds must contain 1-25 entries" };
    for (const id of req.offeringIds) {
      if (typeof id !== "number" || !Number.isInteger(id) || id <= 0)
        return { valid: false, reason: `invalid offeringId: ${String(id)}` };
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.budgetCheck({ offeringIds: req.offeringIds as number[] });
  },
};
