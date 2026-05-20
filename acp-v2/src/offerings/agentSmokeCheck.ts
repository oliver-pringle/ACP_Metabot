import type { Offering } from "./types.js";

// R12 Tier 1.3 — agent_smoke_check ($0.10)
//
// Productises ACP_Tester's existing test-hire primitive into a paid
// marketplace offering. Buyer supplies a target agent address and an
// optional offering name; the bot derives sample requirements from the
// declared schema, runs validation, and returns PASS/WARN/FAIL + latency
// + schema-match. v1 ships static analysis only (no actual paid hire);
// v0.2 wires the real hire through ACP_Website's docker-ops-sidecar.
//
// Offering name 17 chars — under marketplace 20-char cap.
export const agentSmokeCheck: Offering = {
  name: "agent_smoke_check",
  description:
    "Pre-hire validation against any V2 ACP agent. Given a target agent + optional offering name, derives sample requirements from the declared requirementSchema, validates structural fitness, and returns a PASS / WARN / FAIL verdict plus a confidence score, per-field findings, and the estimated price. v1 returns static-analysis verdict (no real paid hire); v0.2 wires the actual hire through ACP_Website's docker-ops-sidecar. Closes Suede's rewrite_acp_jobs gap from the BUYER side.",
  requirementSchema: {
    type: "object",
    properties: {
      targetAgent: {
        type: "string",
        pattern: "^0x[a-fA-F0-9]{40}$",
        description:
          "0x-prefixed wallet address of the agent to smoke-test. Lower- or mixed-case is fine.",
      },
      offeringName: {
        type: "string",
        description:
          "Optional. Name of the specific offering to test. If omitted, the bot selects the agent's CHEAPEST offering automatically (most cost-effective for buyer validation).",
      },
      sampleRequirement: {
        type: "object",
        description:
          "Optional. Buyer-supplied sample requirement payload. If omitted, the bot derives one from the offering's requirementSchema.",
      },
    },
    required: ["targetAgent"],
  },
  requirementExample: {
    targetAgent: "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c",
    offeringName: "today",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["verdict", "targetAgent", "offeringName", "findings", "confidence", "checkedAt"],
    properties: {
      verdict: {
        type: "string",
        enum: ["PASS", "WARN", "FAIL", "AGENT_NOT_FOUND", "OFFERING_NOT_FOUND"],
        description: "Overall smoke-test outcome.",
      },
      targetAgent: {
        type: "string",
        description: "Echo of the input agent address (lower-cased).",
      },
      offeringName: {
        type: "string",
        description: "The offering that was tested (auto-selected if not provided).",
      },
      offeringPriceUsdc: {
        type: "number",
        nullable: true,
        description: "Quoted price for the tested offering. null when offering not found.",
      },
      confidence: {
        type: "number",
        minimum: 0,
        maximum: 1,
        description: "0.0-1.0 confidence in the verdict. v1 static-analysis confidence rarely exceeds 0.85.",
      },
      findings: {
        type: "array",
        description: "Per-check findings. Each entry is severity + check + message.",
        items: {
          type: "object",
          required: ["severity", "check", "message"],
          properties: {
            severity: { type: "string", enum: ["info", "warn", "error"] },
            check: { type: "string", description: "Short ID of the structural check, e.g. 'schema_present', 'required_fields', 'sla_set'." },
            message: { type: "string", description: "Human-readable explanation." },
          },
        },
      },
      reputation: {
        type: "object",
        nullable: true,
        description: "Cached reputation summary for the target agent at check time. Null when not in cache.",
        properties: {
          score: { type: "integer", description: "0-100 agent score." },
          hires: { type: "integer", description: "Lifetime hires across the agent's offerings." },
          computedAt: { type: "string", format: "date-time" },
        },
      },
      checkedAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC timestamp of the smoke check.",
      },
      v1Note: {
        type: "string",
        description: "Static-analysis disclaimer until v0.2 ships real-hire mode.",
      },
    },
  },
  deliverableExample: {
    verdict: "PASS",
    targetAgent: "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c",
    offeringName: "today",
    offeringPriceUsdc: 0.02,
    confidence: 0.82,
    findings: [
      { severity: "info", check: "schema_present", message: "requirementSchema present and well-formed." },
      { severity: "info", check: "sla_set", message: "slaMinutes = 5 (meets ≥ 5 portfolio convention)." },
      { severity: "info", check: "deliverable_schema_present", message: "deliverableSchema present and well-formed." },
      { severity: "info", check: "price_floor", message: "priceUsdc = 0.02 (above $0.01 portfolio floor)." },
    ],
    reputation: {
      score: 0,
      hires: 0,
      computedAt: "2026-05-20T15:50:17Z",
    },
    checkedAt: "2026-05-20T16:00:00Z",
    v1Note: "v1 static analysis; v0.2 wires real hire via docker-ops-sidecar.",
  },

  validate(req) {
    if (!req.targetAgent || typeof req.targetAgent !== "string") {
      return { valid: false, reason: "targetAgent is required" };
    }
    if (!/^0x[a-fA-F0-9]{40}$/.test(req.targetAgent)) {
      return { valid: false, reason: "targetAgent must be a valid 0x-prefixed 40-hex address" };
    }
    if (req.offeringName !== undefined && req.offeringName !== null) {
      if (typeof req.offeringName !== "string" || req.offeringName.length === 0) {
        return { valid: false, reason: "offeringName must be a non-empty string" };
      }
      if (req.offeringName.length > 100) {
        return { valid: false, reason: "offeringName must be <= 100 chars" };
      }
    }
    if (req.sampleRequirement !== undefined && req.sampleRequirement !== null) {
      if (typeof req.sampleRequirement !== "object" || Array.isArray(req.sampleRequirement)) {
        return { valid: false, reason: "sampleRequirement must be a JSON object" };
      }
    }
    return { valid: true };
  },

  async execute(req, { client }) {
    return await client.agentSmokeCheck({
      targetAgent: (req.targetAgent as string).toLowerCase(),
      offeringName: typeof req.offeringName === "string" ? req.offeringName : undefined,
      sampleRequirement: typeof req.sampleRequirement === "object" && req.sampleRequirement !== null
        ? (req.sampleRequirement as Record<string, unknown>)
        : undefined,
    });
  },
};
