import type { Offering } from "./types.js";
import { requireString, requireOneOf } from "../validators.js";

// risk_snapshot ($0.30)  -  Portfolio Risk Bot's headline offering, living inside
// TheMetaBot as a cross-bot orchestrator. Aggregates four cross-bot data
// sources (LiquidGuard health factor, RevokeBot approvals, MEVProtect MEV
// score, internal cached reputation) into a single 0-100 risk grade plus a
// 1-paragraph verdict. Deterministic synthesis (no LLM) so per-call cost
// stays predictable. Cross-bot calls degrade gracefully  -  when a peer bot
// is unavailable, its component is marked "unavailable" in fallbacks[] and
// the score is computed from the remaining sources.
export const riskSnapshot: Offering = {
  name: "risk_snapshot",
  description:
    "Single-call wallet risk briefing. Aggregates 4 cross-bot data sources (LiquidGuard health factor, RevokeBot approvals, MEVProtect exposure, TheMetaBot reputation) into a 0-100 risk score (grade A-F) plus a 1-paragraph verdict. Deterministic weighted synthesis. Gracefully degrades when peer bots are unreachable (component marked unavailable in fallbacks[]). Pair with risk_deep_dive ($1.00) for per-position calldata bundles.",
  requirementSchema: {
    type: "object",
    properties: {
      wallet: {
        type: "string",
        pattern: "^0x[0-9a-fA-F]{40}$",
        description: "0x-prefixed wallet address to assess. Case-insensitive; will be lowercased.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Optional. Chain to scope the risk assessment to. Defaults to 'base'.",
      },
    },
    required: ["wallet"],
  },
  requirementExample: {
    wallet: "0x693a237221e760bC7ff4968B74e25dCA17234633",
    chain: "base",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: [
      "wallet",
      "chain",
      "generatedAt",
      "riskScore",
      "riskGrade",
      "summary",
      "components",
      "fallbacks",
    ],
    properties: {
      wallet: {
        type: "string",
        description: "Lowercased 0x-prefixed wallet address that was assessed.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Chain the assessment was scoped to.",
      },
      generatedAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC timestamp when the snapshot was computed.",
      },
      riskScore: {
        type: "integer",
        minimum: 0,
        maximum: 100,
        description:
          "Composite risk score. 100 = lowest risk, 0 = highest risk. Weighted blend across the four component scores; components with status 'unavailable' are excluded and the remaining weights are renormalised.",
      },
      riskGrade: {
        type: "string",
        enum: ["A", "B", "C", "D", "F"],
        description:
          "Letter grade derived from riskScore: A (>=85), B (70-84), C (55-69), D (40-54), F (<40).",
      },
      summary: {
        type: "string",
        description:
          "1-paragraph human-readable verdict explaining the dominant risk drivers. Deterministically generated from component findings.",
      },
      components: {
        type: "object",
        required: ["healthFactor", "approvals", "mevExposure", "reputation"],
        description: "Per-source breakdown. Each subfield has its own status; unavailable peers do not throw.",
        properties: {
          healthFactor: {
            type: "object",
            required: ["score", "source", "details"],
            properties: {
              score: { type: "integer", minimum: 0, maximum: 100, description: "0-100 risk score for the lending health-factor component. Higher = safer." },
              source: { type: "string", description: "Always 'LiquidGuard'." },
              details: { type: "string", description: "Human-readable evidence (e.g. 'Aave HF 1.87, Compound HF 2.10')." },
              status: { type: "string", enum: ["fresh", "stale", "unavailable"], description: "Data freshness from the peer bot." },
            },
          },
          approvals: {
            type: "object",
            required: ["score", "source", "highRiskCount", "details"],
            properties: {
              score: { type: "integer", minimum: 0, maximum: 100, description: "0-100 score derived from open token approvals. Higher = fewer / safer." },
              source: { type: "string", description: "Always 'RevokeBot'." },
              highRiskCount: { type: "integer", description: "Number of approvals flagged as high-risk (unlimited allowances to non-trusted spenders)." },
              details: { type: "string", description: "Human-readable evidence." },
              status: { type: "string", enum: ["fresh", "stale", "unavailable"], description: "Data freshness." },
            },
          },
          mevExposure: {
            type: "object",
            required: ["score", "source", "details"],
            properties: {
              score: { type: "integer", minimum: 0, maximum: 100, description: "0-100 MEV-exposure score. Higher = less exposure." },
              source: { type: "string", description: "Always 'MEVProtect'." },
              details: { type: "string", description: "Human-readable evidence." },
              status: { type: "string", enum: ["fresh", "stale", "unavailable"], description: "Data freshness." },
            },
          },
          reputation: {
            type: "object",
            required: ["score", "source", "details"],
            properties: {
              score: { type: "integer", minimum: 0, maximum: 100, description: "0-100 on-chain behavioural reputation if the wallet is also an ACP agent. 50 (neutral) when no reputation row exists." },
              source: { type: "string", description: "Always 'TheMetaBot'." },
              details: { type: "string", description: "Human-readable evidence." },
              status: { type: "string", enum: ["fresh", "stale", "unavailable"], description: "Data freshness." },
            },
          },
        },
      },
      fallbacks: {
        type: "array",
        description: "List of component names whose data source was stale or unavailable. Empty when all four were fresh.",
        items: {
          type: "string",
          enum: ["healthFactor", "approvals", "mevExposure", "reputation"],
        },
      },
    },
  },
  deliverableExample: {
    wallet: "0x693a237221e760bc7ff4968b74e25dca17234633",
    chain: "base",
    generatedAt: "2026-05-13T12:34:56Z",
    riskScore: 72,
    riskGrade: "B",
    summary:
      "Wallet shows healthy collateralisation (Aave HF 1.87) with one elevated-risk approval (unlimited USDC to an unverified router). MEV exposure is low across the last 30 days. No on-chain reputation rows. Grade B  -  clean up the unverified approval and the score moves into A territory.",
    components: {
      healthFactor: {
        score: 85,
        source: "LiquidGuard",
        details: "Aave V3 HF 1.87, Compound V3 HF 2.10  -  both comfortably above liquidation.",
        status: "fresh",
      },
      approvals: {
        score: 55,
        source: "RevokeBot",
        details: "1 high-risk approval found (unlimited USDC to 0xabc...cdef, not in trusted registry).",
        highRiskCount: 1,
        status: "fresh",
      },
      mevExposure: {
        score: 90,
        source: "MEVProtect",
        details: "0 sandwich attempts detected over 30-day window; Flashbots Protect coverage stable.",
        status: "fresh",
      },
      reputation: {
        score: 50,
        source: "TheMetaBot",
        details: "Wallet is not a registered ACP agent  -  neutral baseline.",
        status: "fresh",
      },
    },
    fallbacks: [],
  },
  validate(req) {
    const w = requireString(req.wallet, "wallet", 128);
    if (!w.valid) return w;
    if (typeof req.wallet !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.wallet)) {
      return { valid: false, reason: "wallet must be a 0x-prefixed 40-hex address" };
    }
    const c = requireOneOf(req.chain, "chain", ["base", "ethereum"] as const);
    if (!c.valid) return c;
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.riskSnapshot({
      wallet: String(req.wallet),
      chain: typeof req.chain === "string" ? (req.chain as "base" | "ethereum") : undefined,
    });
  },
};
