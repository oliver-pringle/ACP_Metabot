import type { AssetToken } from "@virtuals-protocol/acp-node-v2";

export interface Price {
  amount: number;
  token: "USDC";
}

// v1.7.2: search / searchAgents / browseAgent demoted from $0.01 paid
// offerings to free Resources. See acp-v2/src/resources.ts.
const PRICE_USDC: Record<string, number> = {
  today: 0.05,
  composeStack: 0.05,   // R18 2026-06-07: $0.50 -> $0.05; dominated by buyerOrchestrate $0.10 (strict superset). Full fold to buyerOrchestrate is a v-next change (acp-find docs lockstep).
  watchOffering: 0.50,
  agentReputation: 0.05,
  // v1.7 paid offerings
  arenaParticipants: 0.05,
  buyerOrchestrate: 0.10,
  preHireBudgetCheck: 0.05,
  sellerCoachingPack: 1.00,
  v1Tov2Migration: 0.50,
  // v1.8 Portfolio Risk Bot
  risk_snapshot:    0.30,
  risk_deep_dive:   1.00,
  risk_compare:     0.20,
  risk_attestation: 0.50,
  // v1.9 marketplace gap finder.
  // raw lookup) and composeStack ($0.05 stack synthesis): structured ranking
  // + recommendation tags over already-computed saturation data.
  marketplaceGap: 0.30,
  // R12 Tier 1.3
  // because this returns a verdict, not just a price tally.
  agent_smoke_check: 0.10,
  // v1.10 Phase 3 T4  -  Claude-narrated top-5 summary. $0.05 = same floor as
  // agentReputation / arenaParticipants (single-call augmented read).
  searchNarrative: 0.05,
  // v1.10 Phase 3 T5  -  4-signal scam-risk score. $0.05 matches searchNarrative
  // (also a one-shot augmented read with a 6h cache window).
  agentRiskCheck: 0.05,
  // v1.0 riskAttestPro  -  TheMetaBot's premium tier. $10.00 is 10x risk_deep_dive
  // ($1.00) because riskAttestPro fans out to 7 cross-bot sources at DEPTH (vs
  // risk_deep_dive's 4 at headline) plus LLM narration plus base64 markdown
  // compliance report plus an on-chain EAS attestation pointer. Premium tier
  // priced for high-conviction wallet underwriting / institutional compliance
  // (per portfolio pricing principle: loss-prevention 1-5% of prevented loss).
  riskAttestPro: 10.00,
  // ACPPurchaser Path A (R16 #1 cold-start fix). purchase_execute's $0.10 is
  // the SERVICE fee only; the downstream cost rides the Require-Funds transfer.
  purchase_quote: 0.05,
  purchase_execute: 0.10,
};

const DEFAULT_PRICE_USDC = 0.01;

// Subscription pricing for recurring offerings.
// Each entry maps offering_name -> array of { priceUsdc, durationDays } tiers.
export interface SubscriptionPrice {
  priceUsdc: number;
  durationDays: number;
}

const SUBSCRIPTION_PRICES: Record<string, SubscriptionPrice[]> = {
  daily_risk_watch: [
    { priceUsdc: 1.00, durationDays: 30 },
    { priceUsdc: 1.50, durationDays: 7 },
    { priceUsdc: 5.00, durationDays: 30 },
    { priceUsdc: 13.00, durationDays: 90 },
  ],
  marketplacePulseSub: [
    { priceUsdc: 1.00, durationDays: 30 },
    { priceUsdc: 1.00, durationDays: 7 },
    { priceUsdc: 4.00, durationDays: 30 },
    { priceUsdc: 10.00, durationDays: 90 },
  ],
};

export function subscriptionPricesFor(
  offeringName: string
): SubscriptionPrice[] {
  return SUBSCRIPTION_PRICES[offeringName] ?? [];
}

export function priceFor(offeringName: string): Price {
  const amount = PRICE_USDC[offeringName] ?? DEFAULT_PRICE_USDC;
  return { amount, token: "USDC" };
}

export async function priceForAssetToken(
  offeringName: string,
  _requirement: Record<string, unknown>,
  chainId: number
): Promise<AssetToken> {
  const price = priceFor(offeringName);
  const { AssetToken } = await import("@virtuals-protocol/acp-node-v2");
  return AssetToken.usdc(price.amount, chainId);
}
