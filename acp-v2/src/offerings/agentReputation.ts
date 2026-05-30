import type { Offering } from "./types.js";
import { requireString } from "../validators.js";

export const agentReputation: Offering = {
  name: "agentReputation",
  description:
    "On-chain behavioural reputation for an ACP agent. Returns a 0-100 agentScore plus five sub-scores (completion, dispute, recency, volume30d, responseTime), each with raw evidence and corpus percentile, derived from JobCreated/JobFunded/JobSubmitted/JobCompleted/JobRejected/JobExpired events on Base. Includes raw counts, reliability flags, and a 30-day daily trajectory. Cached 24h per agent. Pass agentAddress only.",
  requirementSchema: {
    type: "object",
    properties: {
      agentAddress: {
        type: "string",
        description:
          "Wallet address of the agent to look up. Lower- or mixed-case is fine; will be normalised.",
      },
    },
    required: ["agentAddress"],
  },
  requirementExample: {
    agentAddress: "0x693a237221e760bC7ff4968B74e25dCA17234633",
  },
  // Cold-compute path runs ChainEventScanner.ScanAgentAsync  -  1-4 min
  // on free RPCs even after the v1.7.2 RpcRetry helper. 10-min SLA
  // gives the retry path real headroom; 5-min was structurally tight.
  slaMinutes: 10,
  deliverableSchema: {
    type: "object",
    required: [
      "agentAddress",
      "agentName",
      "agentScore",
      "computedAt",
      "windowDays",
      "subScores",
      "rawCounts",
      "flags",
    ],
    properties: {
      agentAddress: { type: "string", description: "Lowercased 0x-prefixed wallet." },
      agentName:    { type: "string", description: "On-marketplace display name; empty string if not yet indexed." },
      agentScore:   { type: "integer", minimum: 0, maximum: 100, description: "Composite 0-100 reputation score." },
      computedAt:   { type: "string", format: "date-time", description: "ISO-8601 UTC timestamp of compute." },
      windowDays:   { type: "integer", description: "Number of recent days the score considers (currently 90)." },
      subScores: {
        type: "object",
        description: "Per-dimension reputation breakdown. Each entry has the same shape: value (raw metric), score (0-100), percentile (corpus position), evidence (human-readable derivation), insufficientData (flag).",
        required: ["completion", "dispute", "recency", "volume30d", "responseTime"],
        properties: {
          completion:   { $ref: "#/definitions/subScore", description: "Completion sub-score. Fraction of in-window jobs that reached JobCompleted, with corpus percentile." },
          dispute:      { $ref: "#/definitions/subScore", description: "Dispute sub-score. Inverse of JobRejected rate within the scoring window." },
          recency:      { $ref: "#/definitions/subScore", description: "Recency sub-score. Inverse of time since the agent's last on-chain JobSubmitted." },
          volume30d:    { $ref: "#/definitions/subScore", description: "30-day volume sub-score. Number of completed jobs in the trailing 30 days." },
          responseTime: { $ref: "#/definitions/subScore", description: "Response-time sub-score. Inverse of median offer-to-submit duration across sampled jobs." },
        },
      },
      rawCounts: {
        type: "object",
        description: "Raw on-chain event counts the score derives from (JobCreated/Completed/Rejected/Expired) within the scoring window.",
        required: ["totalJobs", "completed", "rejected", "expired", "completedLast30d"],
        properties: {
          totalJobs:        { type: "integer", description: "Total on-chain jobs observed for the agent within the scoring window" },
          completed:        { type: "integer", description: "Number of jobs that reached JobCompleted on-chain" },
          rejected:         { type: "integer", description: "Number of jobs ended via JobRejected" },
          expired:          { type: "integer", description: "Number of jobs that timed out via JobExpired" },
          completedLast30d: { type: "integer", description: "Subset of completed jobs that fall within the trailing 30 days" },
          lastActiveAt:     { type: "string", format: "date-time", description: "Optional. ISO-8601 UTC of last on-chain JobSubmitted, or off-chain lastActiveAt if available." },
        },
      },
      flags: {
        type: "object",
        description: "Reliability flags. Surface cold-start agents, sub-scores with insufficient data, and warmer-served vs live-compute responses.",
        required: ["isColdStart", "insufficientData", "warmCacheHit"],
        properties: {
          isColdStart:      { type: "boolean", description: "True when the agent has fewer events than the score requires for full confidence." },
          insufficientData: { type: "boolean", description: "True when any sub-score lacks enough data to compute meaningfully." },
          warmCacheHit:     { type: "boolean", description: "True when the response came from the daily warmer cache rather than a live compute." },
        },
      },
      trajectory: {
        type: "array",
        description: "Daily agentScore history for up to the last 30 days (oldest -> newest). Empty when the agent has no warmer or paid-hire history yet. Use to spot improving or declining agents.",
        items: {
          type: "object",
          required: ["date", "agentScore"],
          properties: {
            date:       { type: "string", description: "YYYY-MM-DD UTC." },
            agentScore: { type: "integer", minimum: 0, maximum: 100, description: "Composite reputation score for this day" },
            subScores:  { description: "Per-day sub-score breakdown when available." },
          },
        },
      },
    },
    definitions: {
      subScore: {
        type: "object",
        required: ["value", "score", "percentile", "evidence", "insufficientData"],
        properties: {
          value:            { type: "number", description: "Raw metric value (e.g. completion ratio, days since last activity)." },
          score:            { type: "integer", minimum: 0, maximum: 100, description: "0-100 sub-score." },
          percentile:       { type: "number", description: "Position within the corpus, 0-100, 1dp." },
          evidence:         { type: "string", description: "Human-readable derivation of the score." },
          insufficientData: { type: "boolean", description: "True when this sub-score lacks enough on-chain evidence to be meaningful" },
        },
      },
    },
  },
  deliverableExample: {
    agentAddress: "0x9a1bf7c91b2e2d4d6f0a0b3a4c1e2d3f4a5b6c7d",
    agentName: "DeFiEval",
    agentScore: 78,
    computedAt: "2026-05-04T12:34:56Z",
    windowDays: 90,
    subScores: {
      completion: {
        value: 0.94,
        score: 88,
        percentile: 82.4,
        evidence: "118 of 126 jobs in window completed (94%).",
        insufficientData: false,
      },
      dispute: {
        value: 0.02,
        score: 92,
        percentile: 76.1,
        evidence: "2 rejected of 126 jobs (1.6% dispute rate).",
        insufficientData: false,
      },
      recency: {
        value: 0.5,
        score: 95,
        percentile: 90.2,
        evidence: "Last on-chain activity 12h ago.",
        insufficientData: false,
      },
      volume30d: {
        value: 47,
        score: 71,
        percentile: 64.0,
        evidence: "47 completed jobs in last 30 days.",
        insufficientData: false,
      },
      responseTime: {
        value: 142.6,
        score: 60,
        percentile: 55.5,
        evidence: "Avg response 142.6s across 47 samples.",
        insufficientData: false,
      },
    },
    rawCounts: {
      totalJobs: 126,
      completed: 118,
      rejected: 2,
      expired: 6,
      completedLast30d: 47,
      lastActiveAt: "2026-05-04T00:34:56Z",
    },
    flags: {
      isColdStart: false,
      insufficientData: false,
      warmCacheHit: true,
    },
    trajectory: [
      { date: "2026-05-03", agentScore: 77 },
      { date: "2026-05-04", agentScore: 78 },
    ],
  },
  validate(req) {
    const addr = requireString(req.agentAddress, "agentAddress", 128);
    if (!addr.valid) return addr;
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.agentReputation({
      agentAddress: String(req.agentAddress),
    });
  },
};
