import type { Offering } from "./types.js";
import { requireString, requireOneOf } from "../validators.js";

// marketplacePulseSub ($4.00 / 30 days) — TheMetaBot's first recurring tier.
// On hire, Metabot generates a webhookSecret + subscription row, and from
// the next 12:00 UTC tick the MarketplacePulseWorker (default OFF) POSTs the
// daily /digest snapshot (HMAC-signed) to the buyer's HTTPS webhookUrl.
//
// Why a subscription on TheMetaBot: the marketplace pulse is most valuable
// when delivered passively — buyer agents that want to know "what shipped
// today" don't have to poll /v1/today every day. The webhook + signature
// shape is identical to RevokeBot's daily_scan_watch and v1.8 daily_risk_watch
// so a buyer verifier written once works across the whole portfolio.
//
// Offering name 19 chars — under the 20-char marketplace cap.
export const marketplacePulseSub: Offering = {
  name: "marketplacePulseSub",
  description:
    "Subscription. Daily marketplace digest (newOfferings, gainers, newAgents, churnRate, saturationMap) pushed to your HTTPS webhook for 30 days. HMAC-signed (X-Signature header, t=<ts>,v1=<hex>). Optional marketplace filter 'v1' | 'v2' | 'both'. At hire time you receive a subscriptionId + secret; first tick fires at the next 12:00 UTC.",
  requirementSchema: {
    type: "object",
    properties: {
      webhookUrl: {
        type: "string",
        description: "HTTPS URL that receives the signed daily push. Must be publicly reachable.",
      },
      marketplace: {
        type: "string",
        enum: ["v1", "v2", "both"],
        description: "Optional. Restrict the digest to a single ACP marketplace. Defaults to 'both'.",
      },
    },
    required: ["webhookUrl"],
  },
  requirementExample: {
    webhookUrl: "https://buyer.example.com/acp-metabot/pulse",
    marketplace: "both",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: [
      "subscriptionId",
      "webhookSecret",
      "cadence",
      "marketplace",
      "firstTickAt",
      "expiresAt",
      "ticksPurchased",
    ],
    description:
      "Subscription receipt returned at hire time. Daily push payloads are POSTed to the buyer's webhookUrl by MarketplacePulseWorker, NOT included in this response. Body shape: { subscriptionId, tickNumber, cadenceDays, computedAt, marketplace, digest }. Signature: X-Signature: t=<unix>,v1=<hex>. Verify with HMAC-SHA256(secret, `${t}.${body}`).",
    properties: {
      subscriptionId: {
        type: "string",
        description: "Opaque id (pls_<32hex>). Use for later lookups on /v1/marketplace/pulse/{id}.",
      },
      webhookSecret: {
        type: "string",
        description: "Per-subscription HMAC secret (whs_<64hex>). Returned ONCE. Store securely; not retrievable after hire.",
      },
      cadence: {
        type: "string",
        enum: ["daily"],
        description: "Tick cadence — always 'daily' in v1.",
      },
      marketplace: {
        type: "string",
        enum: ["v1", "v2", "both"],
        description: "Filter applied to every daily digest payload.",
      },
      firstTickAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC moment when the first webhook push is scheduled.",
      },
      expiresAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC moment when the subscription window ends (31 days after hire).",
      },
      ticksPurchased: {
        type: "integer",
        description: "Number of daily ticks included in the $4 bundle. Fixed at 30 in v1.",
      },
    },
  },
  deliverableExample: {
    subscriptionId: "pls_01HF8K9R3X2Y4Z6N7M5P9Q8B1VABCDEF",
    webhookSecret: "whs_a1b2c3d4e5f6789012345678901234567890abcdef1234567890abcdef123456",
    cadence: "daily",
    marketplace: "both",
    firstTickAt: "2026-05-19T12:00:00Z",
    expiresAt: "2026-06-18T12:00:00Z",
    ticksPurchased: 30,
  },
  validate(req) {
    const u = requireString(req.webhookUrl, "webhookUrl", 2048);
    if (!u.valid) return u;
    if (typeof req.webhookUrl !== "string" || !req.webhookUrl.startsWith("https://")) {
      return { valid: false, reason: "webhookUrl must start with https://" };
    }
    const m = requireOneOf(req.marketplace, "marketplace", ["v1", "v2", "both"] as const);
    if (!m.valid) return m;
    return { valid: true };
  },
  async execute(req, { client, session }) {
    const job = session.job ?? (await session.fetchJob());
    const jobIdNum = Number(session.jobId);
    if (!Number.isFinite(jobIdNum)) {
      throw new Error(`session.jobId is not numeric: ${session.jobId}`);
    }
    return await client.marketplacePulseSub({
      jobId: jobIdNum,
      buyerAddress: job.clientAddress,
      webhookUrl: String(req.webhookUrl),
      marketplace:
        req.marketplace === "v1" || req.marketplace === "v2" || req.marketplace === "both"
          ? (req.marketplace as "v1" | "v2" | "both")
          : undefined,
    });
  },
};
