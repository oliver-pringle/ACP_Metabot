import type { Offering } from "./types.js";
import { requireString } from "../validators.js";

export const buyerOrchestrate: Offering = {
  name: "buyerOrchestrate",
  description:
    "composeStack + reputation badges + Arena participation. Returns a use-case-driven 1-10 step stack of " +
    "complementary marketplace offerings, with each seller's cached reputation and Arena ranking attached " +
    "as a trust signal. Lets a buyer agent rank candidates by credentialed performance, not just keyword " +
    "match. Cheap because all enrichment uses cached data  -  no fresh chain scans.",
  requirementSchema: {
    type: "object",
    properties: {
      useCase: { type: "string", description: "Free-text description of what the buyer wants to accomplish." },
      budgetUsdc: { type: "number", description: "Optional max total USDC budget for the stack." },
      maxOfferings: { type: "integer", description: "1-10. Defaults to 5." },
    },
    required: ["useCase"],
  },
  requirementExample: {
    useCase: "Find a stack that screens a Base token for safety AND watches its liquidity after launch",
    budgetUsdc: 5.0,
    maxOfferings: 5,
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["useCase", "rationale", "totalPriceUsdc", "stack"],
    properties: {
      useCase:        { type: "string", description: "Echoes the requested use-case string." },
      rationale:      { type: "string", description: "LLM-curated rationale for why each offering belongs in the stack." },
      totalPriceUsdc: { type: "number", description: "Sum of per-call USDC prices across the stack." },
      stack: {
        type: "array",
        description: "Curated stack of complementary ACP offerings with reputation + Arena enrichment per slot.",
        items: {
          type: "object",
          required: ["offeringName", "agentName", "agentAddress", "priceUsdc", "role"],
          properties: {
            offeringName: { type: "string", description: "Marketplace name of the offering chosen for this slot." },
            agentName:    { type: "string", description: "Marketplace display name of the seller agent." },
            agentAddress: { type: "string", description: "Lowercased 0x-prefixed wallet of the seller." },
            priceUsdc:    { type: "number", description: "Per-call USDC price of this offering." },
            role:         { type: "string", description: "What this offering contributes to the overall stack." },
            reputation:   { description: "Cached reputation summary. Null when the agent has no warm cache yet." },
            arenaParticipation: { description: "Cached Arena rank + last-week-pick flag. Null when the seller is not an Arena participant." },
          },
        },
      },
    },
  },
  deliverableExample: {
    useCase: "Find a stack that screens a Base token for safety AND watches its liquidity after launch",
    rationale: "Step 1 uses DeFiEval's safety profile to gate the trade; step 2 wires LiquidGuard's hf_check to alert on the new pool.",
    totalPriceUsdc: 1.04,
    stack: [
      {
        offeringName: "evaluate_defi_agent",
        agentName: "DeFiEval",
        agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
        priceUsdc: 0.99,
        role: "Safety profile of the token's deployer agent.",
        reputation: { agentScore: 78, hires: 41 },
        arenaParticipation: null,
      },
    ],
  },
  validate(req) {
    const u = requireString(req.useCase, "useCase", 2000);
    if (!u.valid) return u;
    if (req.budgetUsdc !== undefined && (typeof req.budgetUsdc !== "number" || req.budgetUsdc <= 0))
      return { valid: false, reason: "budgetUsdc must be a positive number when provided" };
    if (req.maxOfferings !== undefined) {
      if (typeof req.maxOfferings !== "number" || !Number.isInteger(req.maxOfferings))
        return { valid: false, reason: "maxOfferings must be an integer" };
      if (req.maxOfferings < 1 || req.maxOfferings > 10)
        return { valid: false, reason: "maxOfferings must be 1-10" };
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.buyerOrchestrate({
      useCase: String(req.useCase),
      budgetUsdc: typeof req.budgetUsdc === "number" ? req.budgetUsdc : undefined,
      maxOfferings: typeof req.maxOfferings === "number" ? req.maxOfferings : undefined,
    });
  },
};
