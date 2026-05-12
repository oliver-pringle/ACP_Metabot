import type { Offering } from "./types.js";
import { requireString } from "../validators.js";

const ADDRESS_RX = /^0x[a-fA-F0-9]{40}$/;

export const v1Tov2Migration: Offering = {
  name: "v1Tov2Migration",
  description:
    "Per-offering V1→V2 migration plan for a single agent. Returns the V1 vs V2 split, overall verdict " +
    "(MIGRATE_RECOMMENDED / ALREADY_V2 / PARTIAL_MIGRATION), and an ordered list of migration steps — most-" +
    "hired V1 offering first. Each step lists the V2 marketplace requirements you must satisfy " +
    "(slaMinutes ≥ 5, deliverableSchema, subscription tier flavor where appropriate). Pairs with the free " +
    "/v1/resources/marketplaceVersionMap Resource — start free, upgrade to this when you want the full plan.",
  requirementSchema: {
    type: "object",
    properties: {
      agent: { type: "string", pattern: "^0x[a-fA-F0-9]{40}$", description: "Agent address." },
    },
    required: ["agent"],
  },
  requirementExample: { agent: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d" },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["agentAddress", "verdict"],
    properties: {
      agentAddress:    { type: "string" },
      agentName:       { type: "string" },
      verdict:         { type: "string", enum: ["MIGRATE_RECOMMENDED", "ALREADY_V2", "PARTIAL_MIGRATION", "NOT_INDEXED", "UNKNOWN"] },
      topNote:         { type: "string" },
      v1OfferingCount: { type: "integer" },
      v2OfferingCount: { type: "integer" },
      v1TotalHires:    { type: "integer" },
      v2TotalHires:    { type: "integer" },
      migrationSteps: {
        type: "array",
        items: {
          type: "object",
          properties: {
            step:         { type: "integer" },
            offeringName: { type: "string" },
            priorHires:   { type: "integer" },
            actions:      { type: "array", items: { type: "string" } },
          },
        },
      },
      cachedAt: { type: "string", format: "date-time" },
    },
  },
  deliverableExample: {
    agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
    agentName: "DeFiEval",
    verdict: "MIGRATE_RECOMMENDED",
    topNote: "You have 3 V1 offering(s) and 0 on V2.",
    v1OfferingCount: 3,
    v2OfferingCount: 0,
    v1TotalHires: 41,
    v2TotalHires: 0,
    migrationSteps: [
      {
        step: 1,
        offeringName: "evaluate_defi_agent",
        priorHires: 38,
        actions: [
          "Confirm the offering's requirementSchema is JSON-Schema-valid.",
          "Add slaMinutes (min 5).",
          "Add deliverableSchema + deliverableExample.",
        ],
      },
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
    return await client.v1Tov2Migration({ agent: String(req.agent) });
  },
};
