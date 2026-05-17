import type { Offering } from "./types.js";
import { requireString, requireOneOf } from "../validators.js";

// daily_risk_watch ($5.00 / 30 days) — subscription tier. After hire, the
// RiskWatchWorker BackgroundService runs risk_snapshot daily and POSTs an
// HMAC-signed webhook with the new snapshot, a diff vs. yesterday, and an
// `alert` flag set when the score drops by more than 10 points. Buyer
// supplies an HTTPS webhookUrl; Metabot generates the secret + subscription
// row at hire time. Worker is OFF by default (RiskWatch:Worker:Enabled=true
// flag) so this offering is callable but inactive until the operator flips
// the flag on.
export const dailyRiskWatch: Offering = {
  name: "daily_risk_watch",
  description:
    "Subscription. Daily risk_snapshot pushed to your HTTPS webhook for 30 days. Each push includes the new snapshot, a diff vs. the prior day, and an alert flag set when the score drops by >10 points. HMAC-signed (X-Subscription-Signature header). At hire time you receive a subscriptionId + secret; the first tick fires within 24h. Webhook idempotent on subscriptionId+tickNumber.",
  requirementSchema: {
    type: "object",
    properties: {
      wallet: {
        type: "string",
        pattern: "^0x[0-9a-fA-F]{40}$",
        description: "0x-prefixed wallet address to monitor daily.",
      },
      webhookUrl: {
        type: "string",
        description: "HTTPS URL that receives the signed daily push. Must be publicly reachable.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Optional. Chain scope for the daily snapshot. Defaults to 'base'.",
      },
    },
    required: ["wallet", "webhookUrl"],
  },
  requirementExample: {
    wallet: "0x693a237221e760bC7ff4968B74e25dCA17234633",
    webhookUrl: "https://buyer.example.com/acp-risk-watch/webhook",
    chain: "base",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: ["subscriptionId", "walletAddress", "cadence", "chain", "firstTickAt"],
    description:
      "Subscription receipt returned at hire time. Daily push payloads are POSTed to the buyer's webhookUrl by the RiskWatchWorker, NOT included in this payload. The webhook body includes { tick, snapshot, diff, alert } and is signed with HMAC-SHA256 using the secret returned alongside this receipt out-of-band.",
    properties: {
      subscriptionId: {
        type: "string",
        description: "Opaque id used to look up the subscription on /v1/risk-watch/{id}.",
      },
      walletAddress: {
        type: "string",
        description: "Lowercased 0x-prefixed wallet being monitored.",
      },
      cadence: {
        type: "string",
        enum: ["daily"],
        description: "Tick cadence — always 'daily' in v1.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Chain scope for every daily snapshot.",
      },
      firstTickAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC moment when the first webhook push is scheduled.",
      },
    },
  },
  deliverableExample: {
    subscriptionId: "riskwatch_01HF8K9R3X2Y4Z6N7M5P9Q8B1V",
    walletAddress: "0x693a237221e760bc7ff4968b74e25dca17234633",
    cadence: "daily",
    chain: "base",
    firstTickAt: "2026-05-14T02:00:00Z",
  },
  validate(req) {
    const w = requireString(req.wallet, "wallet", 128);
    if (!w.valid) return w;
    if (typeof req.wallet !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.wallet)) {
      return { valid: false, reason: "wallet must be a 0x-prefixed 40-hex address" };
    }
    const u = requireString(req.webhookUrl, "webhookUrl", 2048);
    if (!u.valid) return u;
    if (typeof req.webhookUrl !== "string" || !req.webhookUrl.startsWith("https://")) {
      return { valid: false, reason: "webhookUrl must start with https://" };
    }
    const c = requireOneOf(req.chain, "chain", ["base", "ethereum"] as const);
    if (!c.valid) return c;
    return { valid: true };
  },
  async execute(req, { client, session }) {
    const job = session.job ?? (await session.fetchJob());
    const jobIdNum = Number(session.jobId);
    if (!Number.isFinite(jobIdNum)) {
      throw new Error(`session.jobId is not numeric: ${session.jobId}`);
    }
    return await client.dailyRiskWatch({
      jobId: jobIdNum,
      buyerAddress: job.clientAddress,
      wallet: String(req.wallet),
      webhookUrl: String(req.webhookUrl),
      chain: typeof req.chain === "string" ? (req.chain as "base" | "ethereum") : undefined,
    });
  },
};
