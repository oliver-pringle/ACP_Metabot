import type { Offering } from "./types.js";

const FIXED_PRICE_TYPES = new Set(["fixed"]); // verify literal via acp_browse_agent in smoke

export const purchaseQuote: Offering = {
  name: "purchase_quote",
  description:
    "Given a target agent + offering, returns the exact downstream USDC cost, the total escrow " +
    "for purchase_execute (service fee + downstream), and a pre-hire safety verdict " +
    "(PROCEED/CAUTION/BLOCK) from the target's scam-risk tier. No money moves. The pre-flight for purchase_execute.",
  requirementSchema: {
    type: "object",
    properties: {
      targetAgent: { type: "string", description: "Wallet address of the seller agent to hire on the buyer's behalf." },
      targetOffering: { type: "string", description: "Exact offering name on the target agent to purchase." },
      requirement: { type: "object", description: "The requirement payload that purchase_execute would forward to the target offering.", properties: {} },
    },
    required: ["targetAgent", "targetOffering"],
  },
  requirementExample: {
    targetAgent: "0xbd95e7235d6f0b1a2b3c4d5e6f7a8b9c0d1e2f30",
    targetOffering: "spender_check",
    requirement: { chainId: 1, spender: "0x6b7a87899490EcE95443e979cA9485CBE7E71522" },
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["targetAgent", "downstreamUsdc", "serviceFeeUsdc", "totalEscrowUsdc", "fixedPrice", "riskTier", "verdict"],
    properties: {
      targetAgent: { type: "string", description: "Resolved target agent wallet address." },
      downstreamUsdc: { type: "number", description: "Fixed USDC price of the target offering (0 if not fixed-price)." },
      serviceFeeUsdc: { type: "number", description: "ACPPurchaser service fee charged by purchase_execute (0.10)." },
      totalEscrowUsdc: { type: "number", description: "What the buyer escrows on purchase_execute = serviceFee + downstreamUsdc." },
      fixedPrice: { type: "boolean", description: "True if the target is fixed-price and supported by purchase_execute v1." },
      riskTier: { type: "string", description: "Pre-hire scam-risk tier of the target agent: low|medium|high|critical." },
      verdict: { type: "string", description: "PROCEED|CAUTION|BLOCK recommendation for purchase_execute." },
      reasons: { type: "array", items: { type: "string", description: "Reason contributing to the verdict." }, description: "Reasons behind the verdict." },
    },
  },
  deliverableExample: {
    targetAgent: "0xbd95e7235d6f0b1a2b3c4d5e6f7a8b9c0d1e2f30",
    downstreamUsdc: 0.02, serviceFeeUsdc: 0.10, totalEscrowUsdc: 0.12,
    fixedPrice: true, riskTier: "low", verdict: "PROCEED", reasons: ["ok"],
  },
  validate(req) {
    if (typeof req.targetAgent !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.targetAgent))
      return { valid: false, reason: "targetAgent must be a 0x EVM address" };
    if (typeof req.targetOffering !== "string" || req.targetOffering.length === 0)
      return { valid: false, reason: "targetOffering required" };
    return { valid: true };
  },
  async execute(req, { client, agent }) {
    const targetAgent = req.targetAgent as string;
    const targetOffering = req.targetOffering as string;
    const detail = await agent.getAgentByWalletAddress(targetAgent);
    const off = detail?.offerings.find((o) => o.name === targetOffering);
    if (!off) {
      return { targetAgent, downstreamUsdc: 0, serviceFeeUsdc: 0.10, totalEscrowUsdc: 0,
        fixedPrice: false, riskTier: "unknown", verdict: "BLOCK", reasons: ["target_not_found"] };
    }
    const fixedPrice = FIXED_PRICE_TYPES.has((off.priceType || "").toLowerCase());
    const downstreamUsdc = fixedPrice ? Number(off.priceValue) : 0;
    return await client.purchaseQuote({ targetAgent, downstreamUsdc, fixedPrice });
  },
};
