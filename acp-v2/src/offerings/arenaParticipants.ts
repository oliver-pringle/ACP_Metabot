import type { Offering } from "./types.js";

const ADDRESS_RX = /^0x[a-fA-F0-9]{40}$/;

export const arenaParticipants: Offering = {
  name: "arenaParticipants",
  description:
    "Bulk pre-hire gate. Given 1-25 ACP agent addresses, returns per-address Arena participation: " +
    "indexed yes/no, current Lifetime + 30-day rank, lifetime + 30-day PnL, and last-week Council pick flag. " +
    "Use this to filter a candidate cohort before paying for ArenaBot's deeper arena_agent_report. " +
    "Cheap, deterministic, cached. No live RPC.",
  requirementSchema: {
    type: "object",
    properties: {
      addresses: {
        type: "array",
        items: { type: "string", pattern: "^0x[a-fA-F0-9]{40}$" },
        minItems: 1,
        maxItems: 25,
        description: "1-25 EVM addresses to look up.",
      },
    },
    required: ["addresses"],
  },
  requirementExample: {
    addresses: [
      "0x6f28f51743b912197caeadbc3113c955bb80e738",
      "0x4e471856e18434216D608D12745283c94CCc6C0b",
    ],
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["requested", "indexed", "agents"],
    properties: {
      requested: { type: "integer", description: "Count of addresses in the request." },
      indexed:   { type: "integer", description: "Count of addresses found in the Arena participation index." },
      agents: {
        type: "array",
        description: "Per-address participation row.",
        items: {
          type: "object",
          required: ["agentAddress", "isParticipant"],
          properties: {
            agentAddress:   { type: "string" },
            isParticipant:  { type: "boolean" },
            rankLifetime:   { type: ["integer", "null"] },
            rank30d:        { type: ["integer", "null"] },
            pnlLifetimeUsd: { type: ["number", "null"] },
            pnl30dUsd:      { type: ["number", "null"] },
            lastWeekPick:   { type: ["boolean", "null"] },
            lastObservedAt: { type: ["string", "null"] },
          },
        },
      },
    },
  },
  deliverableExample: {
    requested: 2,
    indexed: 1,
    agents: [
      {
        agentAddress: "0x6f28f51743b912197caeadbc3113c955bb80e738",
        isParticipant: true,
        rankLifetime: 17,
        rank30d: 8,
        pnlLifetimeUsd: 23410.55,
        pnl30dUsd: 4221.12,
        lastWeekPick: false,
        lastObservedAt: "2026-05-12T08:30:00Z",
      },
      {
        agentAddress: "0x4e471856e18434216d608d12745283c94ccc6c0b",
        isParticipant: false,
        rankLifetime: null,
        rank30d: null,
        pnlLifetimeUsd: null,
        pnl30dUsd: null,
        lastWeekPick: null,
        lastObservedAt: null,
      },
    ],
  },
  validate(req) {
    if (!Array.isArray(req.addresses))
      return { valid: false, reason: "addresses must be an array" };
    if (req.addresses.length < 1 || req.addresses.length > 25)
      return { valid: false, reason: "addresses must contain 1-25 entries" };
    for (const a of req.addresses) {
      if (typeof a !== "string" || !ADDRESS_RX.test(a))
        return { valid: false, reason: `invalid address: ${String(a)}` };
    }
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.arenaParticipants({ addresses: req.addresses as string[] });
  },
};
