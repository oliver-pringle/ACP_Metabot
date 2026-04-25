import type { AssetToken } from "@virtuals-protocol/acp-node-v2";

export interface Price {
  amount: number;
  token: "USDC";
}

const PRICE_USDC: Record<string, number> = {
  search: 0.05,
  composeStack: 0.20,
};

const DEFAULT_PRICE_USDC = 0.05;

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
