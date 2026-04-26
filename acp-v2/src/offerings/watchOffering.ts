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
