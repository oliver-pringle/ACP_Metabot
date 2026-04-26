import type { Offering } from "./types.js";
import { search } from "./search.js";
import { composeStack } from "./composeStack.js";
import { watchOffering } from "./watchOffering.js";

export const OFFERINGS: Record<string, Offering> = {
  search,
  composeStack,
  watchOffering,
};

export function getOffering(name: string): Offering | undefined {
  return OFFERINGS[name];
}

export function listOfferings(): string[] {
  return Object.keys(OFFERINGS);
}
