import type { Offering } from "./types.js";
import { requireString } from "../validators.js";

const ADDRESS_RX = /^0x[a-fA-F0-9]{40}$/;

export const sellerCoachingPack: Offering = {
  name: "sellerCoachingPack",
  description:
    "Premium seller-success report. Extends the free /v1/resources/sellerDiagnose Resource with per-offering " +
    "health scores (0-100), overall verdict (STRONG / OK_WITH_GAPS / WEAK), and a prioritised action list " +
    "(missing schemas, sub-min prices, short descriptions, zero-hire offerings, missing Resources, V1-only " +
    "agents that should migrate). Deterministic; no LLM. Buy this before deploying your bot, or for any " +
    "agent whose offerings aren't converting.",
  requirementSchema: {
    type: "object",
    properties: {
      agent: { type: "string", pattern: "^0x[a-fA-F0-9]{40}$", description: "Agent address to coach." },
    },
    required: ["agent"],
  },
  requirementExample: { agent: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d" },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["agentAddress", "overallVerdict"],
    properties: {
      agentAddress:    { type: "string", description: "Lowercased 0x-prefixed agent wallet that was coached." },
      agentName:       { type: "string", description: "Marketplace display name of the agent (empty string when not indexed)." },
      overallVerdict:  { type: "string", enum: ["NOT_INDEXED", "STRONG", "OK_WITH_GAPS", "WEAK"], description: "Composite verdict on the agent's marketplace fitness. NOT_INDEXED = agent not in TheMetaBot index yet." },
      avgHealthScore:  { type: "integer", minimum: 0, maximum: 100, description: "Mean health score across the agent's offerings (0-100; higher = better)." },
      offeringCount:   { type: "integer", description: "Number of offerings the agent has registered." },
      resourceCount:   { type: "integer", description: "Number of free Resources the agent has registered." },
      totalHires:      { type: "integer", description: "Lifetime paid hires across the agent's offerings (indexer count)." },
      perOffering: {
        type: "array",
        description: "Per-offering breakdown ordered by hires desc.",
        items: {
          type: "object",
          properties: {
            offeringName: { type: "string", description: "Marketplace name of the offering." },
            healthScore:  { type: "integer", minimum: 0, maximum: 100, description: "Composite health 0-100 for this offering (schema completeness + price floor + hire activity)." },
            hires:        { type: "integer", description: "Lifetime paid hires of this offering." },
            notes:        { type: "array", items: { type: "string" }, description: "Per-offering remediation notes (e.g. 'description too short', 'price below floor')." },
          },
        },
      },
      priority: { type: "array", items: { type: "string" }, description: "Ordered remediation steps, highest-impact first." },
      cachedAt: { type: "string", format: "date-time", description: "ISO-8601 UTC of the cached snapshot." },
    },
  },
  deliverableExample: {
    agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
    agentName: "DeFiEval",
    overallVerdict: "OK_WITH_GAPS",
    avgHealthScore: 67,
    offeringCount: 3,
    resourceCount: 2,
    totalHires: 41,
    perOffering: [
      {
        offeringName: "evaluate_defi_agent",
        healthScore: 95,
        hires: 38,
        notes: [],
      },
      {
        offeringName: "defi_agent_deep_eval",
        healthScore: 50,
        hires: 1,
        notes: [
          "Description < 100 chars  -  too short for marketplace search to surface reliably.",
          "Only 1 paid hire  -  consider promoting through a Resource-tab teaser + lower introductory price.",
        ],
      },
    ],
    priority: [
      "URGENT: Register at least a `capabilities` Resource so buyer agents can introspect you without paying.",
      "Fill in requirementSchema for every offering.",
    ],
    cachedAt: "2026-05-12T08:30:00Z",
  },
  validate(req) {
    const a = requireString(req.agent, "agent", 128);
    if (!a.valid) return a;
    if (!ADDRESS_RX.test(req.agent as string))
      return { valid: false, reason: "agent must be a 0x-prefixed 40-hex EVM address" };
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.sellerCoaching({ agent: String(req.agent) });
  },
};
