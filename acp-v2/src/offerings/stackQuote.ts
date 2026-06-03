import type { Offering } from "./types.js";

export const stackQuote: Offering = {
  name: "stack_quote",
  description:
    "Given a subject (a 0x address) and an intent, TheMetaBot curates a stack of complementary fixed-price " +
    "analysis offerings from across the marketplace, screens each (scam-risk + fixed-price), and returns a " +
    "price-bound plan (quoteId, per-step agent/offering/price/risk, total escrow) for stack_execute. No money moves.",
  requirementSchema: {
    type: "object",
    properties: {
      subject: { type: "string", description: "The 0x EVM address the stack will analyse (wallet, agent, or token contract). Threaded into each step." },
      intent: { type: "string", description: "Natural-language description of the analysis you want (e.g. 'full safety + reputation screen of this wallet')." },
      maxFundsUsdc: { type: "number", description: "Ceiling on total downstream spend across the stack; steps over this are blocked." },
      maxSteps: { type: "number", description: "Optional cap on the number of steps in the stack (1-5, default 5)." },
    },
    required: ["subject", "intent", "maxFundsUsdc"],
  },
  requirementExample: {
    subject: "0x9999999999999999999999999999999999999999",
    intent: "full safety + reputation screen of this wallet",
    maxFundsUsdc: 1.0, maxSteps: 4,
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["quoteId", "subject", "steps", "totalDownstreamUsdc", "executeFeeUsdc", "totalEscrowUsdc", "verdict"],
    properties: {
      quoteId: { type: "string", description: "Opaque id to pass to stack_execute. Empty when verdict is BLOCK." },
      subject: { type: "string", description: "The resolved (lowercased) subject address." },
      steps: {
        type: "array",
        description: "The curated, screened, buyable steps in execution order.",
        items: {
          type: "object",
          required: ["targetAgent", "targetOffering", "role", "priceUsdc", "riskTier", "verdict"],
          properties: {
            targetAgent: { type: "string", description: "Seller agent wallet for this step." },
            targetOffering: { type: "string", description: "Offering name purchased for this step." },
            role: { type: "string", description: "Role this step plays in the stack." },
            priceUsdc: { type: "number", description: "Quoted fixed price for this step." },
            riskTier: { type: "string", description: "Scam-risk tier of the seller: low|medium|high." },
            verdict: { type: "string", description: "PROCEED or CAUTION (high-risk) for this step." },
          },
        },
      },
      droppedCandidates: {
        type: "array",
        description: "Candidates excluded from the plan and why.",
        items: {
          type: "object",
          required: ["targetAgent", "targetOffering", "reason"],
          properties: {
            targetAgent: { type: "string", description: "Excluded seller agent wallet." },
            targetOffering: { type: "string", description: "Excluded offering name." },
            reason: { type: "string", description: "Why excluded: not_fixed_price|risk_critical|subject_unmappable|not_found." },
          },
        },
      },
      totalDownstreamUsdc: { type: "number", description: "Sum of the kept steps' prices." },
      executeFeeUsdc: { type: "number", description: "Flat stack_execute fee (0.25)." },
      totalEscrowUsdc: { type: "number", description: "What stack_execute escrows = executeFee + totalDownstream." },
      verdict: { type: "string", description: "PROCEED|CAUTION|BLOCK for the whole stack." },
      reasons: { type: "array", items: { type: "string", description: "Reason contributing to the stack verdict." }, description: "Reasons behind the verdict." },
      expiresAt: { type: "string", description: "ISO-8601 expiry; the quote is unusable after this." },
    },
  },
  deliverableExample: {
    quoteId: "stk_ab12", subject: "0x9999999999999999999999999999999999999999",
    steps: [{ targetAgent: "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c", targetOffering: "risk_snapshot", role: "risk", priceUsdc: 0.30, riskTier: "low", verdict: "PROCEED" }],
    droppedCandidates: [], totalDownstreamUsdc: 0.30, executeFeeUsdc: 0.25, totalEscrowUsdc: 0.55,
    verdict: "PROCEED", reasons: ["ok"], expiresAt: "2026-06-03T13:00:00.000Z",
  },
  validate(req) {
    if (typeof req.subject !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.subject))
      return { valid: false, reason: "subject must be a 0x EVM address" };
    if (typeof req.intent !== "string" || req.intent.trim().length === 0)
      return { valid: false, reason: "intent required" };
    if (typeof req.maxFundsUsdc !== "number" || !(req.maxFundsUsdc > 0))
      return { valid: false, reason: "maxFundsUsdc must be a positive number" };
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.stackQuote({
      subject: req.subject as string,
      intent: req.intent as string,
      maxFundsUsdc: Number(req.maxFundsUsdc),
      maxSteps: req.maxSteps === undefined ? undefined : Number(req.maxSteps),
    });
  },
};
