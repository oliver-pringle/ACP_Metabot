import { PrivyAlchemyEvmProviderAdapter } from "@virtuals-protocol/acp-node-v2";
import { getChain } from "./chain.js";
import type { AcpEnv } from "./env.js";

export async function createProvider(env: AcpEnv) {
  return await PrivyAlchemyEvmProviderAdapter.create({
    walletAddress: env.walletAddress as `0x${string}`,
    walletId: env.walletId,
    signerPrivateKey: env.signerPrivateKey,
    chains: [getChain(env.chain)],
    ...(env.builderCode ? { builderCode: env.builderCode } : {}),
  });
}
