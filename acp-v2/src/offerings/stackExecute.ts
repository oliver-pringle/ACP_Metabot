import type { Offering } from "./types.js";

// stack_execute's execute() is NEVER called via the router — seller.ts special-cases
// it (Require-Funds + N serialized inner hires, all-or-nothing). This object exists
// for registration, schema, and validate() only.
export const stackExecute: Offering = {
  name: "stack_execute",
  description:
    "Executes a stack_quote plan: hires each curated offering on your behalf over the subject and returns one " +
    "combined report. You escrow a $0.25 fee + the quoted total downstream (Require-Funds). ALL-OR-NOTHING: if " +
    "any step fails the whole job is rejected and you are fully refunded. Pass the quoteId from stack_quote.",
  requirementSchema: {
    type: "object",
    properties: {
      quoteId: { type: "string", description: "The quoteId returned by stack_quote." },
      subject: { type: "string", description: "The same 0x subject address used in stack_quote (must match the quote)." },
      maxFundsUsdc: { type: "number", description: "Your ceiling on total downstream spend; must be >= the quote's totalDownstreamUsdc." },
    },
    required: ["quoteId", "subject", "maxFundsUsdc"],
  },
  requirementExample: {
    quoteId: "stk_ab12", subject: "0x9999999999999999999999999999999999999999", maxFundsUsdc: 1.0,
  },
  slaMinutes: 30,
  deliverableSchema: {
    type: "object",
    required: ["status", "subject", "steps"],
    properties: {
      status: { type: "string", description: "DELIVERED (all steps delivered) | REJECTED (any step failed; full refund)." },
      subject: { type: "string", description: "The subject address the stack analysed." },
      steps: {
        type: "array",
        description: "Per-step result, in execution order.",
        items: {
          type: "object",
          required: ["targetAgent", "targetOffering", "role", "status"],
          properties: {
            targetAgent: { type: "string", description: "Seller agent hired for this step." },
            targetOffering: { type: "string", description: "Offering purchased for this step." },
            role: { type: "string", description: "Role this step played." },
            status: { type: "string", description: "delivered | failed." },
            innerJobId: { type: "string", description: "On-chain job id of this inner hire, or null." },
            deliverable: { type: "object", description: "Parsed deliverable from this step (present when delivered).", properties: {} },
            error: { type: "string", description: "Failure reason (present when failed)." },
          },
        },
      },
      downstreamChargedUsdc: { type: "number", description: "Total downstream charged (0 on REJECTED)." },
      executeFeeUsdc: { type: "number", description: "Stack execute fee (0.25; refunded on REJECTED)." },
      reason: { type: "string", description: "On REJECTED, why (e.g. quote_expired_or_not_found, daily_cap_exceeded, downstream_failed:<step>)." },
    },
  },
  deliverableExample: {
    status: "DELIVERED", subject: "0x9999999999999999999999999999999999999999",
    steps: [{ targetAgent: "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c", targetOffering: "risk_snapshot", role: "risk", status: "delivered", innerJobId: "7801", deliverable: { score: 79 }, error: "" }],
    downstreamChargedUsdc: 0.30, executeFeeUsdc: 0.25, reason: "",
  },
  validate(req) {
    if (typeof req.quoteId !== "string" || req.quoteId.length === 0)
      return { valid: false, reason: "quoteId required" };
    if (typeof req.subject !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.subject))
      return { valid: false, reason: "subject must be a 0x EVM address" };
    if (typeof req.maxFundsUsdc !== "number" || !(req.maxFundsUsdc > 0))
      return { valid: false, reason: "maxFundsUsdc must be a positive number" };
    return { valid: true };
  },
  async execute() {
    throw new Error("stack_execute is handled in seller.ts, not via the router");
  },
};
