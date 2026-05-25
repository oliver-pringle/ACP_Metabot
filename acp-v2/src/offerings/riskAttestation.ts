import type { Offering } from "./types.js";
import { requireString, requireOneOf } from "../validators.js";

// risk_attestation ($0.50)  -  risk_snapshot plus an EAS-style cryptographic
// attestation of the result at a specific block. Used by insurance / grant
// programs that want "borrower passed risk check at block X" as a portable
// claim. The attestation is signed by EASIssuer's Privy WaaS issuer wallet
// and (when EASIssuer publishes on-chain) returns the easAttestationUid
// + Basescan URL. When EASIssuer is unavailable, the snapshot still ships
// with a `signature: null` placeholder and `easAttestationUid: null`.
export const riskAttestation: Offering = {
  name: "risk_attestation",
  description:
    "EAS-attested risk snapshot for a wallet at a specific block. Same envelope as risk_snapshot plus a cryptographic attestation block (signature, schemaUid, easAttestationUid, easTxHash, blockNumber). Used by insurance / grant programs that want a portable 'borrower passed risk check at block X' claim. Issued via EASIssuer's two-key model (Privy WaaS signer + operator EOA pays gas). When EASIssuer is unreachable, the snapshot still ships with attestation fields nulled.",
  requirementSchema: {
    type: "object",
    properties: {
      wallet: {
        type: "string",
        pattern: "^0x[0-9a-fA-F]{40}$",
        description: "0x-prefixed wallet address to assess and attest.",
      },
      chain: {
        type: "string",
        enum: ["base", "ethereum"],
        description: "Optional. Chain to scope the assessment to. Defaults to 'base'.",
      },
    },
    required: ["wallet"],
  },
  requirementExample: {
    wallet: "0x693a237221e760bC7ff4968B74e25dCA17234633",
    chain: "base",
  },
  slaMinutes: 5,
  deliverableSchema: {
    type: "object",
    required: [
      "wallet",
      "chain",
      "generatedAt",
      "riskScore",
      "riskGrade",
      "summary",
      "components",
      "fallbacks",
      "attestation",
    ],
    description:
      "Same envelope as risk_snapshot plus an `attestation` block. Shared fields described on risk_snapshot.",
    properties: {
      wallet: { type: "string", description: "Lowercased 0x-prefixed wallet." },
      chain: { type: "string", enum: ["base", "ethereum"], description: "Chain in scope." },
      generatedAt: { type: "string", format: "date-time", description: "ISO-8601 UTC compute timestamp." },
      riskScore: { type: "integer", minimum: 0, maximum: 100, description: "Composite 0-100 risk score." },
      riskGrade: { type: "string", enum: ["A", "B", "C", "D", "F"], description: "Letter grade." },
      summary: { type: "string", description: "Verdict paragraph." },
      components: { type: "object", description: "Same shape as risk_snapshot.components." },
      fallbacks: { type: "array", items: { type: "string" }, description: "Components that were unavailable." },
      attestation: {
        type: "object",
        required: ["signature", "schemaUid", "blockNumber"],
        description: "EAS-compatible attestation envelope. easAttestationUid + easTxHash are populated only when EASIssuer successfully published on-chain; otherwise null.",
        properties: {
          signature:         { type: "string", description: "EIP-712 signature over the canonicalised snapshot, or null when EASIssuer was unreachable." },
          schemaUid:         { type: "string", description: "EAS schema UID for WalletRiskSnapshot v1 (registered at bootstrap)." },
          easAttestationUid: { type: "string", description: "On-chain EAS attestation UID. Null when not published on-chain." },
          easTxHash:         { type: "string", description: "Transaction hash for the attestation. Null when not published on-chain." },
          baseScanUrl:       { type: "string", description: "Basescan URL for the attestation. Null when not published on-chain." },
          blockNumber:       { type: "integer", description: "Block height the snapshot was anchored to." },
        },
      },
    },
  },
  deliverableExample: {
    wallet: "0x693a237221e760bc7ff4968b74e25dca17234633",
    chain: "base",
    generatedAt: "2026-05-13T12:34:56Z",
    riskScore: 78,
    riskGrade: "B",
    summary: "Wallet passes a B-grade risk check.",
    components: {
      healthFactor: { score: 90, source: "LiquidGuard", details: "Aave HF 2.1.", status: "fresh" },
      approvals: { score: 70, source: "RevokeBot", details: "0 high-risk approvals.", highRiskCount: 0, status: "fresh" },
      mevExposure: { score: 80, source: "MEVProtect", details: "Low exposure.", status: "fresh" },
      reputation: { score: 50, source: "TheMetaBot", details: "Not an ACP agent.", status: "fresh" },
    },
    fallbacks: [],
    attestation: {
      signature: "0xa1b2c3...ef00",
      schemaUid: "0xdf208286c7c0b8a5d8f9e2a3b4c5d6e7f8901234567890abcdef0114f",
      easAttestationUid: "0x9c8a76b5c4d3e2f1a09b8c7d6e5f4a3b2c1d0e9f8a7b6c5d4e3f2a1b0c9d8e",
      easTxHash: "0x33445566778899aabbccddeeff0011223344556677889900aabbccddeeff00",
      baseScanUrl: "https://basescan.org/tx/0x33445566778899aabbccddeeff0011223344556677889900aabbccddeeff00",
      blockNumber: 19234567,
    },
  },
  validate(req) {
    const w = requireString(req.wallet, "wallet", 128);
    if (!w.valid) return w;
    if (typeof req.wallet !== "string" || !/^0x[0-9a-fA-F]{40}$/.test(req.wallet)) {
      return { valid: false, reason: "wallet must be a 0x-prefixed 40-hex address" };
    }
    const c = requireOneOf(req.chain, "chain", ["base", "ethereum"] as const);
    if (!c.valid) return c;
    return { valid: true };
  },
  async execute(req, { client }) {
    return await client.riskAttestation({
      wallet: String(req.wallet),
      chain: typeof req.chain === "string" ? (req.chain as "base" | "ethereum") : undefined,
    });
  },
};
