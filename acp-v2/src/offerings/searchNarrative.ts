import type { Offering } from "./types.js";
import { requireString, requireOneOf } from "../validators.js";

// v1.10 Phase 3 T4: searchNarrative ($0.05) - Claude-narrated wrap of the top-5
// search results for a buyer query. The endpoint internally forces top-5 +
// no resources/expand/risk on the underlying search so the narrator wraps a
// stable, minimal payload. Cached 1h via (query_canonical, corpus_version)
// bucket (corpus_version = unixepoch() / cacheTtl, so identical queries in
// the same hour collapse to a single LLM call). Falls back gracefully to a
// degraded envelope when the LLM is unavailable or the JSON parse fails -
// the offering never errors out on a transient narrator failure.
//
// Offering name 15 chars - under marketplace 20-char cap.
export const searchNarrative: Offering = {
  name: "searchNarrative",
  description:
    "Claude-narrated summary of the top 5 ACP marketplace offerings matching your query, with a 1-line " +
    "'why this ranked high' per result. Saves you skimming raw JSON when triaging - get a 3-5 sentence " +
    "neutral overview + per-result reasoning, then hire the right one. Cached 1h per query for repeat hires; " +
    "degrades gracefully when the LLM is unavailable.",
  requirementSchema: {
    type: "object",
    properties: {
      query: {
        type: "string",
        description: "The buyer query to narrate (1-2000 chars).",
      },
      previousQueries: {
        type: "array",
        items: { type: "string" },
        maxItems: 5,
        description:
          "Optional prior queries this session (max 5; each up to 200 chars). Helps the narrator avoid restating context the buyer already saw.",
      },
      marketplace: {
        type: "string",
        enum: ["v1", "v2"],
        description: "Optional marketplace filter. Omit to search both.",
      },
    },
    required: ["query"],
  },
  requirementExample: {
    query: "watch my HF on aave",
    previousQueries: ["price feed for ETH", "aave liquidation"],
    marketplace: "v2",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["query", "count", "summary", "perResultReason", "citedOfferings", "cacheHit", "status"],
    properties: {
      query: { type: "string", description: "Echo of the input query." },
      count: { type: "integer", description: "Number of offerings narrated (up to 5)." },
      results: {
        type: "array",
        description: "The top-5 OfferingMatch hits used as input to the narrator (echoed for buyer convenience).",
        items: {
          type: "object",
          properties: {
            offeringId:         { type: "integer" },
            offeringName:       { type: "string" },
            agentName:          { type: "string" },
            agentAddress:       { type: "string" },
            description:        { type: "string" },
            priceUsdc:          { type: "number" },
            priceType:          { type: "string" },
            chain:              { type: "string" },
            score:              { type: "number" },
            marketplaceVersion: { type: "string", enum: ["v1", "v2"] },
          },
        },
      },
      summary: {
        type: "string",
        description: "3-5 sentence neutral overview of the top offerings with [name@addr] citations.",
      },
      perResultReason: {
        type: "array",
        description: "One short rationale per cited offering.",
        items: {
          type: "object",
          required: ["offering", "reason"],
          properties: {
            offering: { type: "string", description: "offeringName@agentAddress identifier." },
            reason:   { type: "string", description: "1-line: why this ranked high." },
          },
        },
      },
      citedOfferings: {
        type: "array",
        items: { type: "string" },
        description: "Full offering@addr identifiers in the narrated set.",
      },
      cacheHit: {
        type: "boolean",
        description: "True when served from the 1h (query_canonical, corpus_version) cache.",
      },
      status: {
        type: "string",
        enum: ["ok", "degraded_llm_unavailable", "degraded_parse", "degraded_no_results"],
        description: "ok = normal narration; degraded_* = fallback envelope when the narrator could not complete.",
      },
    },
  },
  deliverableExample: {
    query: "watch my HF on aave",
    count: 3,
    results: [
      {
        offeringId: 4421,
        offeringName: "hf_check",
        agentName: "LiquidGuard",
        agentAddress: "0x18362cdc06e7e5fc02d9e1cf6c5f7e3e4f8a619f",
        description: "Single-shot Aave V3 health-factor check.",
        priceUsdc: 0.05,
        priceType: "per-call",
        chain: "base",
        score: 0.93,
        marketplaceVersion: "v2",
      },
    ],
    summary:
      "Three ways to monitor an Aave position. The cheapest is [hf_check@0x18362c] at $0.05 - single-shot, no subscription. For continuous monitoring, [hf_check_pro@0x18362c] adds a deviation trigger. For attested audit trails, pair with [oracle_attest@0x935e97] at $0.50.",
    perResultReason: [
      { offering: "hf_check@0x18362c",     reason: "Cheapest single-shot HF check on Base." },
      { offering: "hf_check_pro@0x18362c", reason: "Adds trigger thresholds + alert." },
      { offering: "oracle_attest@0x935e97", reason: "Bundles attestation for audit-quality reporting." },
    ],
    citedOfferings: [
      "hf_check@0x18362cdc06e7e5fc02d9e1cf6c5f7e3e4f8a619f",
      "hf_check_pro@0x18362cdc06e7e5fc02d9e1cf6c5f7e3e4f8a619f",
      "oracle_attest@0x935e97cd0ce8c2c98bd87b03d7e3c5b2e2eb236e",
    ],
    cacheHit: false,
    status: "ok",
  },
  validate(req) {
    const q = requireString(req.query, "query", 2000);
    if (!q.valid) return q;

    if (req.previousQueries !== undefined && req.previousQueries !== null) {
      if (!Array.isArray(req.previousQueries))
        return { valid: false, reason: "previousQueries must be an array of strings" };
      if (req.previousQueries.length > 5)
        return { valid: false, reason: "previousQueries accepts at most 5 entries" };
      for (const p of req.previousQueries) {
        if (typeof p !== "string")
          return { valid: false, reason: "previousQueries entries must be strings" };
        if (p.length > 200)
          return { valid: false, reason: "previousQueries entries must be 200 chars or fewer" };
      }
    }

    const m = requireOneOf(req.marketplace, "marketplace", ["v1", "v2"] as const);
    if (!m.valid) return m;

    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.searchNarrative({
      query: String(req.query),
      previousQueries: Array.isArray(req.previousQueries)
        ? (req.previousQueries as string[])
        : undefined,
      marketplace:
        req.marketplace === "v1" || req.marketplace === "v2"
          ? (req.marketplace as "v1" | "v2")
          : undefined,
    });
  },
};
