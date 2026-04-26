export type ChainName = "base" | "baseSepolia";

export interface AcpEnv {
  walletAddress: string;
  walletId: string;
  signerPrivateKey: string;
  chain: ChainName;
  apiUrl: string;
  apiKey: string;
  builderCode?: string;
}

const REQUIRED = [
  "ACP_WALLET_ADDRESS",
  "ACP_WALLET_ID",
  "ACP_SIGNER_PRIVATE_KEY",
  "ACP_CHAIN",
  "ACP_METABOT_API_URL",
  "INTERNAL_API_KEY",
] as const;

export function loadEnv(source: NodeJS.ProcessEnv = process.env): AcpEnv {
  for (const name of REQUIRED) {
    const value = source[name];
    if (!value || value.trim() === "") {
      throw new Error(`Missing required env var: ${name}`);
    }
  }

  const chain = source.ACP_CHAIN;
  if (chain !== "base" && chain !== "baseSepolia") {
    throw new Error(`ACP_CHAIN must be "base" or "baseSepolia", got "${chain}"`);
  }

  const builderCodeRaw = source.ACP_BUILDER_CODE;
  const builderCode =
    builderCodeRaw && builderCodeRaw.trim() !== "" ? builderCodeRaw : undefined;

  return {
    walletAddress: source.ACP_WALLET_ADDRESS!,
    walletId: source.ACP_WALLET_ID!,
    signerPrivateKey: source.ACP_SIGNER_PRIVATE_KEY!,
    chain,
    apiUrl: source.ACP_METABOT_API_URL!,
    apiKey: source.INTERNAL_API_KEY!,
    builderCode,
  };
}
