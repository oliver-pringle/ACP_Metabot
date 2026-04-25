import { base, baseSepolia, type Chain } from "viem/chains";
import type { ChainName } from "./env.js";

export function getChain(name: ChainName): Chain {
  if (name === "base") return base;
  return baseSepolia;
}
