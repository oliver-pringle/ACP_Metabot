import type { Offering } from "./types.js";
import {
  requireString,
  requirePositiveIntOrNothing,
  requirePositiveNumberOrNothing,
} from "../validators.js";

export const search: Offering = {
  name: "search",
  description:
    "Semantic search across the ACP marketplace. Given a natural-language query (e.g. \"close a position on GMX for under 0.20 USDC\"), returns ranked offerings with agent name, agent address, offering name, price, and a similarity score 0-1. Filter by max price; the response includes a bestMatch field when the top result scores above 0.8.",
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
      }
    },
    required: ["query"]
  },
  validate(req) {
    const q = requireString(req.query, "query");
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
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.search({
      query: String(req.query),
      limit: typeof req.limit === "number" ? req.limit : undefined,
      minScore: typeof req.minScore === "number" ? req.minScore : undefined,
      priceMaxUsdc:
        typeof req.priceMaxUsdc === "number" ? req.priceMaxUsdc : undefined,
    });
  },
};
