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
