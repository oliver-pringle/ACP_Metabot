import type { Offering } from "./types.js";

// purchase_execute's execute() is NEVER called via the normal router — seller.ts
// special-cases it (Require-Funds + inner hire). This object exists for
// registration, schema, and validate() only.
export const purchaseExecute: Offering = {
  name: "purchase_execute",
  description:
    "Hires a fixed-price offering on another agent on your behalf and returns its deliverable + on-chain job id. " +
    "You escrow a $0.10 service fee plus the downstream cost (Require-Funds); the bot pays the seller and is " +
    "reimbursed on completion. Set maxFundsUsdc to cap the downstream spend.",
  requirementSchema: {
    type: "object",
    properties: {
      targetAgent: { type: "string", description: "Wallet address of the seller agent to hire on your behalf." },
      targetOffering: { type: "string", description: "Exact offering name on the target agent to purchase." },
      requirement: { type: "object", description: "The requirement payload forwarded verbatim to the target offering.", properties: {} },
      maxFundsUsdc: { type: "number", description: "Your ceiling on the downstream cost; the hire is rejected if the target costs more." },
    },
    required: ["targetAgent", "targetOffering", "maxFundsUsdc"],
  },
  requirementExample: {
    targetAgent: "0xbd95e7235d6f0b1a2b3c4d5e6f7a8b9c0d1e2f30",
    targetOffering: "spender_check",
    requirement: { chainId: 1, spender: "0x6b7a87899490EcE95443e979cA9485CBE7E71522" },
    maxFundsUsdc: 0.05,
  },
  slaMinutes: 10,
  deliverableSchema: {
    type: "object",
    required: ["status", "targetAgent", "targetOffering"],
    properties: {
      status: { type: "string", description: "DELIVERED|REJECTED outcome of the buy-on-behalf." },
      targetAgent: { type: "string", description: "The agent that was hired." },
      targetOffering: { type: "string", description: "The offering that was purchased." },
      innerJobId: { type: "string", description: "On-chain job id of the inner hire, or null if it never started." },
      downstreamUsdc: { type: "number", description: "USDC paid to the target seller for the inner hire." },
      serviceFeeUsdc: { type: "number", description: "Service fee retained by ACPPurchaser (0.10)." },
      deliverable: { type: "object", description: "The parsed deliverable from the target offering, or null on REJECTED.", properties: {} },
      reason: { type: "string", description: "On REJECTED, why (over_max_funds, risk_critical, daily_cap_exceeded, downstream_failed, ...)." },
    },
  },
  deliverableExample: {
    status: "DELIVERED",
    targetAgent: "0xbd95e7235d6f0b1a2b3c4d5e6f7a8b9c0d1e2f30",
    targetOffering: "spender_check", innerJobId: "7704", downstreamUsdc: 0.02, serviceFeeUsdc: 0.10,
    deliverable: { verdict: "high_risk" }, reason: "",
  },
  validate(req) {
    if (typeof req.targetAgent !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.targetAgent))
      return { valid: false, reason: "targetAgent must be a 0x EVM address" };
    if (typeof req.targetOffering !== "string" || req.targetOffering.length === 0)
      return { valid: false, reason: "targetOffering required" };
    if (typeof req.maxFundsUsdc !== "number" || !(req.maxFundsUsdc > 0))
      return { valid: false, reason: "maxFundsUsdc must be a positive number" };
    return { valid: true };
  },
  // Never invoked — seller.ts handles purchase_execute. Throw to make a routing bug loud.
  async execute() {
    throw new Error("purchase_execute is handled in seller.ts, not via the router");
  },
};
