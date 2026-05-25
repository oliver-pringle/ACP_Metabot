import type { Offering } from "./types.js";
import { requireOneOf } from "../validators.js";

// risk_compare ($0.20)  -  bulk risk_snapshot for 2-5 wallets, ranked from
// safest to riskiest with a single-line conclusion plus per-component
// "best in cohort" callouts. Cheap relative to N x risk_snapshot because
// the fan-out runs in parallel and the result is summarised, not the
// full snapshot per wallet.
export const riskCompare: Offering = {
  name: "risk_compare",
  description:
    "Side-by-side risk comparison of 2-5 wallets. Fans out risk_snapshot in parallel, returns a ranked array (safest first) with a one-line conclusion plus per-component winners (best HF, fewest risky approvals, lowest MEV exposure). Use this to pick the safest wallet in a multi-sig candidate set or to compare a portfolio against peers.",
  requirementSchema: {
    type: "object",
    properties: {
      wallets: {
        type: "array",
        description: "List of 2-5 wallet addresses to compare. Duplicates are deduped, invalid addresses are rejected.",
        minItems: 2,
        maxItems: 5,
        items: {
          type: "string",
          pattern: "^0x[0-9a-fA-F]{40}$",
        },
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Optional. Chain to scope every wallet to. Defaults to 'base'.",
      },
    },
    required: ["wallets"],
  },
  requirementExample: {
    wallets: [
      "0x693a237221e760bC7ff4968B74e25dCA17234633",
      "0x4e47B82DAA50ABc41AB3d6F95F0Fb5DC5dd16C0b",
    ],
    chain: "base",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["wallets", "chain", "ranked", "conclusion", "diffs"],
    properties: {
      wallets: {
        type: "array",
        items: { type: "string" },
        description: "Echo of normalised (lowercased) wallets in request order.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Chain the comparison was scoped to.",
      },
      ranked: {
        type: "array",
        description: "Wallets sorted by riskScore descending (safest first).",
        items: {
          type: "object",
          required: ["wallet", "score", "grade"],
          properties: {
            wallet: { type: "string", description: "Lowercased 0x-prefixed address." },
            score: { type: "integer", minimum: 0, maximum: 100, description: "Composite risk score (higher=safer)." },
            grade: { type: "string", enum: ["A", "B", "C", "D", "F"], description: "Letter grade." },
          },
        },
      },
      conclusion: {
        type: "string",
        description: "Single-sentence verdict, e.g. 'Wallet 0xabc...1234 is safest.'",
      },
      diffs: {
        type: "object",
        required: ["healthFactorBest", "approvalsBest", "mevExposureBest"],
        description: "Per-component cohort winners.",
        properties: {
          healthFactorBest: { type: "string", description: "Wallet with the highest health-factor sub-score (lowercased address)." },
          approvalsBest:    { type: "string", description: "Wallet with the highest approvals sub-score (i.e. fewest high-risk approvals)." },
          mevExposureBest:  { type: "string", description: "Wallet with the highest MEV-exposure sub-score (i.e. least MEV-exposed)." },
        },
      },
    },
  },
  deliverableExample: {
    wallets: [
      "0x693a237221e760bc7ff4968b74e25dca17234633",
      "0x4e47b82daa50abc41ab3d6f95f0fb5dc5dd16c0b",
    ],
    chain: "base",
    ranked: [
      { wallet: "0x4e47b82daa50abc41ab3d6f95f0fb5dc5dd16c0b", score: 88, grade: "A" },
      { wallet: "0x693a237221e760bc7ff4968b74e25dca17234633", score: 65, grade: "C" },
    ],
    conclusion: "Wallet 0x4e47...6c0b is safest (grade A, 88).",
    diffs: {
      healthFactorBest: "0x4e47b82daa50abc41ab3d6f95f0fb5dc5dd16c0b",
      approvalsBest:    "0x4e47b82daa50abc41ab3d6f95f0fb5dc5dd16c0b",
      mevExposureBest:  "0x693a237221e760bc7ff4968b74e25dca17234633",
    },
  },
  validate(req) {
    if (!Array.isArray(req.wallets)) {
      return { valid: false, reason: "wallets must be an array of 2-5 addresses" };
    }
    if (req.wallets.length < 2 || req.wallets.length > 5) {
      return { valid: false, reason: "wallets must contain between 2 and 5 addresses" };
    }
    for (const w of req.wallets) {
      if (typeof w !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(w)) {
        return { valid: false, reason: "every wallet must be a 0x-prefixed 40-hex address" };
      }
    }
    const c = requireOneOf(req.chain, "chain", ["base", "ethereum"] as const);
    if (!c.valid) return c;
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.riskCompare({
      wallets: (req.wallets as string[]).map((w) => String(w)),
      chain: typeof req.chain === "string" ? (req.chain as "base" | "ethereum") : undefined,
    });
  },
};
