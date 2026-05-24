import type { Offering } from "./types.js";
import { requireString } from "../validators.js";

// v1.10 Phase 3 T5: agentRiskCheck ($0.05) — defensive scam-risk score for a
// single agent on a single chain. Four signals × 25 = 100 binned to
// low / medium / high / critical. Cached per (agent_address, chain_id) for
// 6h (configurable via Search:AgentRiskCacheTtlSeconds). NEVER throws on
// signal evaluation — any per-signal lookup failure degrades to score 0
// with a "lookup failed" detail; only invalid agent addresses produce a 400.
// Suspicious-funder matching is exact-string against the curated OFAC-sourced
// Data/SuspiciousFunderPatterns.json (51 entries at v0.6 spec).
//
// Offering name 14 chars — under marketplace 20-char cap.
export const agentRiskCheck: Offering = {
  name: "agentRiskCheck",
  description:
    "Defensive scam-risk assessment for one ACP agent. Returns a 4-signal score (reputationDepth + " +
    "pricingOutlier + walletProvenance via OFAC sanctions match + footprintAnomaly via V1↔V2 impersonation " +
    "heuristics), 0-100 binned to low/medium/high/critical. Cached 6h per (agent, chain). Hire this before " +
    "paying a new or unfamiliar agent — catches impersonation + sanctioned-wallet patterns.",
  requirementSchema: {
    type: "object",
    properties: {
      agentAddress: {
        type: "string",
        description: "0x-prefixed 40-hex wallet of the agent to score (case-insensitive).",
      },
      chainId: {
        type: "integer",
        enum: [1, 8453],
        description: "Chain to score on. 1 = Ethereum mainnet, 8453 = Base mainnet. Default 8453.",
      },
    },
    required: ["agentAddress"],
  },
  requirementExample: {
    agentAddress: "0xecf99cda7afa4ad34c4e4a83a87bf42c3ec1558c",
    chainId: 8453,
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: [
      "agentAddress",
      "chainId",
      "riskScore",
      "riskTier",
      "signals",
      "evaluatedAt",
      "cacheTtlSeconds",
    ],
    properties: {
      agentAddress: { type: "string", description: "Lowercased 0x-prefixed agent wallet (echo)." },
      chainId:      { type: "integer", description: "Chain the score was computed against." },
      riskScore: {
        type: "integer",
        description: "Total risk score, 0-100 (higher = riskier). Sum of the four signal scores.",
        minimum: 0,
        maximum: 100,
      },
      riskTier: {
        type: "string",
        enum: ["low", "medium", "high", "critical"],
        description: "Binned tier: low 0-25 / medium 26-50 / high 51-75 / critical 76-100.",
      },
      signals: {
        type: "array",
        description: "Exactly four signals, each 0-25, with a buyer-facing detail string.",
        items: {
          type: "object",
          required: ["name", "score", "detail"],
          properties: {
            name: {
              type: "string",
              enum: [
                "reputationDepth",
                "pricingOutlier",
                "walletProvenance",
                "footprintAnomaly",
              ],
              description: "Signal identifier.",
            },
            score: {
              type: "integer",
              description: "Signal contribution, 0-25.",
              minimum: 0,
              maximum: 25,
            },
            detail: {
              type: "string",
              description: "Human-readable explanation (e.g. '10 completed jobs (lower = higher risk)').",
            },
          },
        },
      },
      evaluatedAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC timestamp the score was computed.",
      },
      cacheTtlSeconds: {
        type: "integer",
        description: "Seconds the cached result remains valid (default 21600 = 6h).",
      },
    },
  },
  deliverableExample: {
    agentAddress: "0xecf99cda7afa4ad34c4e4a83a87bf42c3ec1558c",
    chainId: 8453,
    riskScore: 28,
    riskTier: "medium",
    signals: [
      {
        name: "reputationDepth",
        score: 5,
        detail: "10 completed jobs (lower = higher risk)",
      },
      {
        name: "pricingOutlier",
        score: 8,
        detail: "avg pricing-outlier across 5 offering(s) = 8.0/25",
      },
      {
        name: "walletProvenance",
        score: 0,
        detail: "no suspicious-funder match",
      },
      {
        name: "footprintAnomaly",
        score: 15,
        detail: "offering 'today' name-matches 2 V1 agent(s) (possible impersonation)",
      },
    ],
    evaluatedAt: "2026-05-24T19:23:11Z",
    cacheTtlSeconds: 21600,
  },
  validate(req) {
    const a = requireString(req.agentAddress, "agentAddress", 64);
    if (!a.valid) return a;
    if (!/^0x[0-9a-fA-F]{40}$/.test(String(req.agentAddress))) {
      return { valid: false, reason: "agentAddress must be 0x + 40 hex chars" };
    }
    if (req.chainId !== undefined && req.chainId !== null) {
      if (typeof req.chainId !== "number" || !Number.isInteger(req.chainId)) {
        return { valid: false, reason: "chainId must be an integer" };
      }
      if (req.chainId !== 1 && req.chainId !== 8453) {
        return { valid: false, reason: "chainId must be 1 (Ethereum) or 8453 (Base)" };
      }
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.agentRiskCheck({
      agentAddress: String(req.agentAddress),
      chainId: typeof req.chainId === "number" ? req.chainId : undefined,
    });
  },
};
