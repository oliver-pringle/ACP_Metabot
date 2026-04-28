import type { Offering } from "./types.js";
import { requireString } from "../validators.js";

export const agentReputation: Offering = {
  name: "agentReputation",
  description:
    "On-chain behavioural reputation for an ACP agent. Returns a 0-100 score derived from completion rate, dispute rate, recency, 30-day throughput, and average response time. Cached 24h per agent. Pass an optional offeringName to include a per-offering hire-count breakdown alongside the agent-level score.",
  requirementSchema: {
    type: "object",
    properties: {
      agentAddress: {
        type: "string",
        description:
          "Wallet address of the agent to look up. Lower- or mixed-case is fine; will be normalised.",
      },
      offeringName: {
        type: "string",
        description:
          "Optional. Name of a specific offering owned by the agent. When supplied, the response includes a single per-offering reputation block instead of an array of all offerings.",
      },
    },
    required: ["agentAddress"],
  },
  validate(req) {
    const addr = requireString(req.agentAddress, "agentAddress", 128);
    if (!addr.valid) return addr;
    if (req.offeringName !== undefined && req.offeringName !== null) {
      if (typeof req.offeringName !== "string" || req.offeringName.trim() === "") {
        return { valid: false, reason: "offeringName must be a non-empty string when supplied" };
      }
      if (req.offeringName.length > 256) {
        return { valid: false, reason: "offeringName must be at most 256 characters" };
      }
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.agentReputation({
      agentAddress: String(req.agentAddress),
      offeringName: typeof req.offeringName === "string" ? req.offeringName : undefined,
    });
  },
};
