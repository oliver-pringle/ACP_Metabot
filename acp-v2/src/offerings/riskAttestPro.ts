import type { Offering } from "./types.js";
import { requireString, requireOneOf } from "../validators.js";

// riskAttestPro ($10.00) — TheMetaBot's premium tier. Wraps the C# /v1/risk/
// attest-pro endpoint (Task 8) which orchestrates 7 cross-bot signal lanes at
// DEPTH (LiquidGuard HF + RevokeBot per-spender approvals + MEVProtect forensics
// + TheMetaBot 30d reputation trajectory + TheArenaBot history + TheWitnessBot
// manifest + 30-day risk trend from RiskTrajectoryStore). One deliverable
// returns the agent-consumable JSON envelope, a Haiku-narrated executive
// summary, a base64-encoded markdown compliance report, AND an on-chain EAS
// attestation pointer. Fail-soft floor: up to 3 of 7 sources may be
// unavailable; ≥4 unavailable surfaces 502 INSUFFICIENT_SIGNALS. 1h wallet
// cache keyed (walletAddress, chain).
export const riskAttestPro: Offering = {
  name: "riskAttestPro",
  description:
    "Premium $10 wallet risk briefing. 7 cross-bot signals at DEPTH (LiquidGuard HF + RevokeBot per-spender approvals + MEVProtect + reputation 30d trajectory + Arena history + WitnessBot manifest + 30-day risk trend). One deliverable serves agent JSON + LLM-narrated summary + compliance markdown + on-chain EAS attestation. 4-of-7 fail-soft floor (502 below); 1h wallet cache.",
  requirementSchema: {
    type: "object",
    properties: {
      walletAddress: {
        type: "string",
        pattern: "^0x[0-9a-fA-F]{40}$",
        description: "0x-prefixed wallet to assess. Case-insensitive; will be lowercased.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Optional. Chain to scope the assessment to. Defaults to 'base'.",
      },
      buyerSignature: {
        type: "string",
        description: "Optional EIP-712 signature over the request envelope. Surfaced forward for v1.1 strict-mode binding; accepted but not enforced in v1.0.",
      },
      fresh: {
        type: "boolean",
        description: "Optional. When true, bypasses the 1h wallet-response cache and forces a fresh 7-lane compute.",
      },
    },
    required: ["walletAddress"],
  },
  requirementExample: {
    walletAddress: "0x693a237221e760bC7ff4968B74e25dCA17234633",
    chain: "base",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: [
      "verdict",
      "scorePro",
      "grade",
      "components",
      "executiveSummary",
      "recommendations",
      "markdownReport",
      "attestation",
      "walletAddress",
      "chain",
      "generatedAt",
      "expiresAt",
      "sourcesQueried",
      "sourcesUnavailable",
      "componentsHash",
      "cacheHit",
    ],
    properties: {
      verdict: {
        type: "string",
        enum: ["STRONG_BUY", "OK", "CAUTION", "AVOID", "INSUFFICIENT_DATA"],
        description: "Composite verdict label. STRONG_BUY≥85 / OK 70-84 / CAUTION 55-69 / AVOID<55; INSUFFICIENT_DATA when too few sources answered.",
      },
      scorePro: {
        type: "integer",
        minimum: 0,
        maximum: 100,
        description: "0-100 composite (arithmetic mean of available component scores; renormalised when sources are unavailable).",
      },
      grade: {
        type: "string",
        enum: ["A", "B", "C", "D", "F"],
        description: "Letter grade derived from scorePro: A>=85 / B>=70 / C>=55 / D>=40 / F<40.",
      },
      components: {
        type: "object",
        description: "Per-signal deep data (healthFactor / approvals / mev / reputation / arena / witness / trajectory). Each component has score, source, status (fresh/stale/unavailable), and details.",
      },
      executiveSummary: {
        type: "string",
        description: "3-5 sentence LLM-narrated paragraph (Claude Haiku) explaining the verdict + dominant risk drivers in plain English.",
      },
      recommendations: {
        type: "array",
        description: "Ordered de-risking actions. revoke entries ship calldata for high-risk approvals; manual_review entries flag positions needing human attention.",
        items: {
          type: "object",
          properties: {
            priority: {
              type: "string",
              description: "Action priority: critical / high / medium / low.",
            },
            action: {
              type: "string",
              description: "Action type: revoke / raise_hf / verify_witness / manual_review.",
            },
            rationale: {
              type: "string",
              description: "Why this action is recommended (cites the underlying component finding).",
            },
          },
        },
      },
      markdownReport: {
        type: "string",
        description: "Base64-encoded full markdown audit report (decode for human-readable compliance trail; ~5-15kB). Pair with the JSON envelope for archival.",
      },
      attestation: {
        type: "object",
        description: "On-chain EAS attestation envelope (currently includes schema UID + componentsHash placeholder; live publish wiring lands v1.0.1).",
        properties: {
          uid: {
            type: "string",
            description: "EAS attestation UID on Base mainnet (null until v1.0.1 wires live publish).",
          },
          txHash: {
            type: "string",
            description: "Base mainnet tx hash of the publish (null until v1.0.1).",
          },
          blockNumber: {
            type: "integer",
            description: "Block the attestation was mined into (0 until v1.0.1).",
          },
          schemaUid: {
            type: "string",
            description: "riskAttestPro schema UID (registered once at boot via SchemaBootstrapWorker).",
          },
          basescanUrl: {
            type: "string",
            description: "BaseScan URL for direct inspection of the attestation (null until v1.0.1).",
          },
        },
      },
      walletAddress: {
        type: "string",
        description: "Lowercased 0x-prefixed wallet that was assessed.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Chain the assessment was scoped to.",
      },
      generatedAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC timestamp when the verdict was computed.",
      },
      expiresAt: {
        type: "string",
        format: "date-time",
        description: "ISO-8601 UTC timestamp 24h after generatedAt; treat verdicts after this as stale.",
      },
      sourcesQueried: {
        type: "array",
        description: "All 7 source names queried (LiquidGuard / RevokeBot / MEVProtect / TheMetaBot / TheArenaBot / TheWitnessBot / history).",
        items: {
          type: "string",
          description: "Name of a queried source.",
        },
      },
      sourcesUnavailable: {
        type: "array",
        description: "Subset of sourcesQueried that returned status=unavailable (excluded from score). At most 3; >=4 unavailable -> endpoint returns 502 INSUFFICIENT_SIGNALS.",
        items: {
          type: "string",
          description: "Name of an unavailable source.",
        },
      },
      componentsHash: {
        type: "string",
        description: "SHA256 hex hash of canonical sub-component JSON (LLM cache key; deterministic across identical payloads).",
      },
      cacheHit: {
        type: "boolean",
        description: "True if the response was served from the 1h wallet cache; false if computed fresh this hire.",
      },
    },
  },
  deliverableExample: {
    verdict: "OK",
    scorePro: 72,
    grade: "B",
    components: {
      healthFactor: {
        score: 85,
        source: "LiquidGuard",
        details: "Aave V3 HF 1.87 — comfortably above liquidation.",
        status: "fresh",
      },
      approvals: {
        score: 60,
        source: "RevokeBot",
        details: "1 high-risk approval (unlimited USDC to unverified router).",
        status: "fresh",
      },
      mev: {
        score: 90,
        source: "MEVProtect",
        details: "0 sandwich attempts over 30-day window.",
        status: "fresh",
      },
      reputation: {
        score: 55,
        source: "TheMetaBot",
        details: "Not a registered ACP agent; neutral baseline.",
        status: "fresh",
      },
      arena: {
        score: 70,
        source: "TheArenaBot",
        details: "Top-50 lifetime PnL bracket.",
        status: "fresh",
      },
      witness: {
        score: 65,
        source: "TheWitnessBot",
        details: "Signed manifest verified.",
        status: "fresh",
      },
      trajectory: {
        score: 75,
        source: "history",
        details: "30-day risk trend stable.",
        status: "fresh",
      },
    },
    executiveSummary:
      "Wallet shows balanced exposure across 7 signals. Lending health is strong (Aave HF 1.87) and MEV exposure is low. One unverified-router approval drags the approvals component; revoking it would move the verdict from OK to STRONG_BUY.",
    recommendations: [
      {
        priority: "high",
        action: "revoke",
        rationale: "Unlimited USDC approval to unverified router — revoke before next trade cycle.",
      },
    ],
    markdownReport: "IyByaXNrQXR0ZXN0UHJvIHJlcG9ydA==",
    attestation: {
      uid: "0x0000000000000000000000000000000000000000000000000000000000000000",
      txHash: "0x0000000000000000000000000000000000000000000000000000000000000000",
      blockNumber: 0,
      schemaUid: "0xdf208286c7c0b8a5d8f9e2a3b4c5d6e7f8901234567890abcdef0114f",
      basescanUrl: "https://basescan.org/attestation/view/0x0000000000000000000000000000000000000000000000000000000000000000",
    },
    walletAddress: "0x693a237221e760bc7ff4968b74e25dca17234633",
    chain: "base",
    generatedAt: "2026-05-30T18:00:00.0000000Z",
    expiresAt: "2026-05-31T18:00:00.0000000Z",
    sourcesQueried: ["LiquidGuard", "RevokeBot", "MEVProtect", "TheMetaBot", "TheArenaBot", "TheWitnessBot", "history"],
    sourcesUnavailable: [],
    componentsHash: "abc123def4567890abcdef1234567890abcdef1234567890abcdef1234567890",
    cacheHit: false,
  },
  validate(req) {
    const w = requireString(req.walletAddress, "walletAddress", 128);
    if (!w.valid) return w;
    if (typeof req.walletAddress !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.walletAddress)) {
      return { valid: false, reason: "walletAddress must be a 0x-prefixed 40-hex address" };
    }
    const c = requireOneOf(req.chain, "chain", ["base", "ethereum"] as const);
    if (!c.valid) return c;
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.riskAttestPro({
      walletAddress: String(req.walletAddress),
      chain: typeof req.chain === "string" ? (req.chain as "base" | "ethereum") : undefined,
      buyerSignature: typeof req.buyerSignature === "string" ? req.buyerSignature : undefined,
      fresh: typeof req.fresh === "boolean" ? req.fresh : undefined,
    });
  },
};
