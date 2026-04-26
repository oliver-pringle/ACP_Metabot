import type { Offering } from "./types.js";
import { requireString } from "../validators.js";

export const agentReputation: Offering = {
  name: "agentReputation",
  description:
    "Reputation lookup for an ACP agent. Given an agent's wallet address, returns a 0-100 score and percentile derived from lifetime job counts across the marketplace, plus a per-offering breakdown. Pass an optional offeringName to focus the response on a single offering. Useful before hiring an unfamiliar agent or for ranking candidates returned by the search offering.",
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
