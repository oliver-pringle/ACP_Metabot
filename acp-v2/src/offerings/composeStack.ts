import type { Offering } from "./types.js";
import {
  requireString,
  requirePositiveNumberOrNothing,
  requirePositiveIntOrNothing,
} from "../validators.js";

export const composeStack: Offering = {
  name: "composeStack",
  description:
    "Given a buyer's use case (e.g. \"build a swap agent that checks token safety, gets a DEX quote, and executes via private mempool\"), returns a curated stack of complementary ACP offerings with their roles and a rationale. Powered by an LLM over the indexed marketplace.",
  requirementSchema: {
    type: "object",
    properties: {
      useCase: {
        type: "string",
        description: "Plain-English description of what the buyer is trying to build."
      },
      budgetUsdc: {
        type: "number",
        description: "Optional budget cap (USDC) per full stack run."
      },
      maxOfferings: {
        type: "integer",
        description: "Max number of offerings in the recommended stack (1-10). Defaults to 5.",
        minimum: 1,
        maximum: 10
      }
    },
    required: ["useCase"]
  },
  requirementExample: {
    useCase: "build a swap agent that checks token safety, gets a DEX quote, and executes via private mempool",
    budgetUsdc: 1.0,
    maxOfferings: 5,
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["rationale", "stack", "totalPriceUsdc"],
    properties: {
      rationale: { type: "string", description: "LLM-generated explanation of why this stack fits the use case." },
      stack: {
        type: "array",
        description: "Curated stack of complementary ACP offerings, ordered by role.",
        items: {
          type: "object",
          required: ["offeringName", "agentName", "agentAddress", "priceUsdc", "role"],
          properties: {
            offeringName: { type: "string", description: "Marketplace name of the offering chosen for this slot" },
            agentName: { type: "string", description: "Marketplace display name of the seller agent" },
            agentAddress: { type: "string", description: "Lowercased 0x-prefixed wallet of the seller." },
            priceUsdc: { type: "number", description: "Per-call price of this offering in USDC." },
            role: { type: "string", description: "Human-readable role this offering plays in the stack (e.g. 'token-safety check')." },
          },
        },
      },
      totalPriceUsdc: { type: "number", description: "Sum of priceUsdc across the stack." },
    },
  },
  deliverableExample: {
    rationale: "Pre-trade safety + DEX route + private-mempool execution covers the swap end-to-end at low cost.",
    stack: [
      {
        offeringName: "token_safety",
        agentName: "TokenGuard",
        agentAddress: "0x1111111111111111111111111111111111111111",
        priceUsdc: 0.05,
        role: "Pre-trade token risk + honeypot screen.",
      },
      {
        offeringName: "dex_quote",
        agentName: "QuoteAgent",
        agentAddress: "0x2222222222222222222222222222222222222222",
        priceUsdc: 0.10,
        role: "Best-of-N DEX quote with slippage estimate.",
      },
      {
        offeringName: "mev_protect",
        agentName: "MEVProtect",
        agentAddress: "0x3333333333333333333333333333333333333333",
        priceUsdc: 0.30,
        role: "Forward signed tx through Flashbots Protect.",
      },
    ],
    totalPriceUsdc: 0.45,
  },
  validate(req) {
    const u = requireString(req.useCase, "useCase", 4096);
    if (!u.valid) return u;
    const b = requirePositiveNumberOrNothing(req.budgetUsdc, "budgetUsdc");
    if (!b.valid) return b;
    const m = requirePositiveIntOrNothing(req.maxOfferings, "maxOfferings", 10);
    if (!m.valid) return m;
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.composeStack({
      useCase: String(req.useCase),
      budgetUsdc: typeof req.budgetUsdc === "number" ? req.budgetUsdc : undefined,
      maxOfferings: typeof req.maxOfferings === "number" ? req.maxOfferings : undefined,
    });
  },
};
