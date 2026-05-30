import type { Offering } from "./types.js";
import { requireString, requireOneOf } from "../validators.js";

// risk_deep_dive ($1.00)  -  risk_snapshot plus per-position action bundles.
// For each high-risk approval, returns the RevokeBot-built revoke calldata
// (to / data / value), so a buyer agent can sign-and-broadcast directly.
// Lending-protocol "close_position" / "rebalance" hints are flagged for
// manual review since calldata bundles for lend/borrow positions are
// outside Metabot's scope in v1.
export const riskDeepDive: Offering = {
  name: "risk_deep_dive",
  description:
    "risk_snapshot plus per-position drill-down with calldata bundles for de-risking actions. Returns the full snapshot fields plus an ordered actions[] list: revoke calldata for high-risk approvals (from RevokeBot), plus prioritised rebalance / close-position hints flagged from LiquidGuard for manual review. Buyer agents can sign-and-broadcast the revoke calldata directly. Cross-bot calls degrade gracefully.",
  requirementSchema: {
    type: "object",
    properties: {
      wallet: {
        type: "string",
        pattern: "^0x[0-9a-fA-F]{40}$",
        description: "0x-prefixed wallet address to assess.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Optional. Chain to scope the assessment to. Defaults to 'base'.",
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
      "actions",
    ],
    description:
      "Same envelope as risk_snapshot plus an `actions` array. See risk_snapshot for the shared fields' descriptions.",
    properties: {
      wallet: { type: "string", description: "Lowercased 0x-prefixed wallet." },
      chain: { type: "string", enum: ["base", "ethereum"], description: "Chain in scope." },
      generatedAt: { type: "string", format: "date-time", description: "ISO-8601 UTC compute timestamp." },
      riskScore: { type: "integer", minimum: 0, maximum: 100, description: "Composite 0-100 risk score (higher=safer)." },
      riskGrade: { type: "string", enum: ["A", "B", "C", "D", "F"], description: "Letter grade." },
      summary: { type: "string", description: "Human-readable verdict paragraph." },
      components: { type: "object", description: "Same shape as risk_snapshot.components." },
      fallbacks: { type: "array", items: { type: "string" }, description: "Components whose data source was unavailable." },
      actions: {
        type: "array",
        description:
          "Ordered de-risking actions. revoke actions ship signable calldata; rebalance / close_position actions ship manual-review hints only in v1.",
        items: {
          type: "object",
          required: ["type", "priority", "calldataHint"],
          properties: {
            type: {
              type: "string",
              enum: ["revoke", "rebalance", "close_position"],
              description: "Action type. revoke = approval revoke (calldata bundled). rebalance = lending-position rebalance (hint only). close_position = unwind a lending/borrow position (hint only).",
            },
            priority: {
              type: "string",
              enum: ["high", "med", "low"],
              description: "Ordering hint. High-risk approvals always rank 'high'; underwater HF triggers 'high' on rebalance.",
            },
            calldataHint: {
              type: "object",
              description: "Action payload. For 'revoke' actions, ships signable EVM calldata (to / data / value) the buyer can broadcast directly. For 'rebalance' / 'close_position' actions in v1, ships a manual-review hint only (data field is empty).",
              required: ["to", "data", "value", "description"],
              properties: {
                to: { type: "string", description: "Target contract address for the action (token contract for revokes, lending pool for rebalances)." },
                data: { type: "string", description: "Hex-encoded calldata. Empty string when type is rebalance/close_position (manual review only in v1)." },
                value: { type: "string", description: "ETH value to send in wei. Always '0' for revokes." },
                description: { type: "string", description: "Human-readable description of what this action does." },
              },
            },
          },
        },
      },
    },
  },
  deliverableExample: {
    wallet: "0x693a237221e760bc7ff4968b74e25dca17234633",
    chain: "base",
    generatedAt: "2026-05-13T12:34:56Z",
    riskScore: 65,
    riskGrade: "C",
    summary:
      "Wallet has 2 unlimited approvals to unverified spenders and an Aave HF of 1.21 (close to the 1.0 liquidation threshold). Recommended: revoke the 2 risky approvals first, then add collateral or repay debt to raise HF above 1.5.",
    components: {
      healthFactor: { score: 50, source: "LiquidGuard", details: "Aave HF 1.21  -  within 25% of liquidation.", status: "fresh" },
      approvals: { score: 40, source: "RevokeBot", details: "2 high-risk approvals.", highRiskCount: 2, status: "fresh" },
      mevExposure: { score: 90, source: "MEVProtect", details: "Low MEV exposure.", status: "fresh" },
      reputation: { score: 50, source: "TheMetaBot", details: "Not an ACP agent.", status: "fresh" },
    },
    fallbacks: [],
    actions: [
      {
        type: "revoke",
        priority: "high",
        calldataHint: {
          to: "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913",
          data: "0x095ea7b3000000000000000000000000abcdef1234567890abcdef1234567890abcdef12000000000000000000000000000000000000000000000000000000000000000",
          value: "0",
          description: "Revoke USDC approval to 0xabc...ef12 (unverified router).",
        },
      },
      {
        type: "rebalance",
        priority: "high",
        calldataHint: {
          to: "0xA238Dd80C259a72e81d7e4664a9801593F98d1c5",
          data: "",
          value: "0",
          description: "Aave HF below 1.5  -  add collateral or repay debt. v1 returns this as a manual-review hint; v1.1 will bundle the deposit/repay calldata.",
        },
      },
    ],
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
    return await client.riskDeepDive({
      wallet: String(req.wallet),
      chain: typeof req.chain === "string" ? (req.chain as "base" | "ethereum") : undefined,
    });
  },
};
