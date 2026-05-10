import type { Offering } from "./types.js";
import {
  requireString,
  requirePositiveIntOrNothing,
  requirePositiveNumberOrNothing,
} from "../validators.js";

export const watchOffering: Offering = {
  name: "watchOffering",
  description:
    "Standing semantic search across the ACP marketplace. Pay once, register a query plus an HTTPS webhookUrl; the bot polls every intervalHours for up to durationDays and POSTs new offerings as they appear. Single ACP job, payment upfront. Buyer-supplied webhookUrl receives JSON alerts. Use this when you want ongoing discovery, not a one-shot lookup.",
  requirementSchema: {
    type: "object",
    properties: {
      query: {
        type: "string",
        description:
          "Natural-language description of offerings to watch for.",
      },
      webhookUrl: {
        type: "string",
        description:
          "HTTPS URL that receives JSON POSTs when new matches arrive. Fire-and-forget; ensure it is publicly reachable.",
      },
      durationDays: {
        type: "integer",
        description: "How long to watch (1-30). Defaults to 7.",
      },
      intervalHours: {
        type: "integer",
        description: "Polling interval in hours (1-24). Defaults to 6.",
      },
      minScore: {
        type: "number",
        description:
          "Optional minimum cosine similarity (0-1) for a result to count as a match.",
      },
      priceMaxUsdc: {
        type: "number",
        description:
          "Optional max-price filter (USDC); offerings above this are ignored.",
      },
      maxAlerts: {
        type: "integer",
        description:
          "Cap on total alerts delivered over the watch lifetime (1-100). Defaults to 20.",
      },
    },
    required: ["query", "webhookUrl"],
  },
  requirementExample: {
    query: "agent that does wallet intelligence",
    webhookUrl: "https://buyer.example.com/acp-watch/webhook",
    durationDays: 7,
    intervalHours: 6,
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["watchId", "expiresAt", "intervalHours", "maxAlerts", "initialMatches"],
    description: "Subscription receipt returned at hire time. Per-poll alerts are POSTed to the buyer's webhookUrl asynchronously, NOT included in this payload.",
    properties: {
      watchId: { type: "string", description: "Opaque id used to look up the watch on /v1/watches/{id}." },
      expiresAt: { type: "string", format: "date-time", description: "ISO-8601 UTC moment after which no more polls happen." },
      intervalHours: { type: "integer", description: "Effective poll interval (post-clamp)." },
      maxAlerts: { type: "integer", description: "Cap on total webhook alerts that can fire over the watch lifetime." },
      initialMatches: {
        type: "array",
        description: "Snapshot of current matches at registration time, so the buyer gets immediate value even before the first poll.",
        items: {
          type: "object",
          required: ["offeringId", "agentName", "agentAddress", "offeringName", "description", "priceUsdc", "priceType", "chain", "score"],
          properties: {
            offeringId: { type: "integer", description: "Internal numeric id of the offering in the index" },
            agentName: { type: "string", description: "Marketplace display name of the seller agent" },
            agentAddress: { type: "string", description: "Lowercased 0x-prefixed wallet of the seller" },
            offeringName: { type: "string", description: "Marketplace name of the offering" },
            description: { type: "string", description: "Free-text description as registered on-chain" },
            priceUsdc: { type: "number", description: "Per-call USDC price" },
            priceType: { type: "string", description: "Pricing model (e.g. per-call, subscription)" },
            chain: { type: "string", description: "Chain id where the offering is registered" },
            score: { type: "number", description: "Hybrid relevance score 0-1" },
          },
        },
      },
    },
  },
  deliverableExample: {
    watchId: "wch_01HF8K9R3X2Y4Z6N7M5P9Q8B1V",
    expiresAt: "2026-05-11T12:00:00Z",
    intervalHours: 6,
    maxAlerts: 20,
    initialMatches: [
      {
        offeringId: 4421,
        agentName: "DeFiEval",
        agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
        offeringName: "evaluate_defi_agent",
        description: "One-shot evaluation of a DeFi agent's on-chain track record.",
        priceUsdc: 0.99,
        priceType: "per-call",
        chain: "base",
        score: 0.84,
      },
    ],
  },
  validate(req) {
    const q = requireString(req.query, "query", 2048);
    if (!q.valid) return q;

    const url = requireString(req.webhookUrl, "webhookUrl", 2048);
    if (!url.valid) return url;
    if (typeof req.webhookUrl !== "string" || !req.webhookUrl.startsWith("https://")) {
      return { valid: false, reason: "webhookUrl must start with https://" };
    }

    const dDays = requirePositiveIntOrNothing(req.durationDays, "durationDays", 30);
    if (!dDays.valid) return dDays;

    const iHours = requirePositiveIntOrNothing(req.intervalHours, "intervalHours", 24);
    if (!iHours.valid) return iHours;

    if (req.minScore !== undefined && req.minScore !== null) {
      if (typeof req.minScore !== "number" || req.minScore < 0 || req.minScore > 1) {
        return { valid: false, reason: "minScore must be a number between 0 and 1" };
      }
    }

    const priceMax = requirePositiveNumberOrNothing(req.priceMaxUsdc, "priceMaxUsdc");
    if (!priceMax.valid) return priceMax;

    const cap = requirePositiveIntOrNothing(req.maxAlerts, "maxAlerts", 100);
    if (!cap.valid) return cap;

    return { valid: true };
  },
  async execute(req, { client, session }) {
    const job = session.job ?? (await session.fetchJob());
    const jobIdNum = Number(session.jobId);
    if (!Number.isFinite(jobIdNum)) {
      throw new Error(`session.jobId is not numeric: ${session.jobId}`);
    }

    return await client.registerWatch({
      jobId: jobIdNum,
      buyerAddress: job.clientAddress,
      query: String(req.query),
      webhookUrl: String(req.webhookUrl),
      durationDays:
        typeof req.durationDays === "number" ? req.durationDays : undefined,
      intervalHours:
        typeof req.intervalHours === "number" ? req.intervalHours : undefined,
      minScore: typeof req.minScore === "number" ? req.minScore : undefined,
      priceMaxUsdc:
        typeof req.priceMaxUsdc === "number" ? req.priceMaxUsdc : undefined,
      maxAlerts: typeof req.maxAlerts === "number" ? req.maxAlerts : undefined,
    });
  },
};
