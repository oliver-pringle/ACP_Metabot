import type { AssetToken } from "@virtuals-protocol/acp-node-v2";

export interface Price {
  amount: number;
  token: "USDC";
}

// v1.7.2: search / searchAgents / browseAgent demoted from $0.01 paid
// offerings to free Resources. See acp-v2/src/resources.ts.
const PRICE_USDC: Record<string, number> = {
  today: 0.02,
  composeStack: 0.50,
  watchOffering: 0.50,
  agentReputation: 0.05,
  // v1.7 paid offerings
  arenaParticipants: 0.05,
  buyerOrchestrate: 0.10,
  preHireBudgetCheck: 0.05,
  sellerCoachingPack: 1.00,
  v1Tov2Migration: 0.50,
};

const DEFAULT_PRICE_USDC = 0.01;

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
