import type { Offering } from "./types.js";
import { search } from "./search.js";
import { searchAgents } from "./searchAgents.js";
import { browseAgent } from "./browseAgent.js";
import { today } from "./today.js";
import { composeStack } from "./composeStack.js";
import { watchOffering } from "./watchOffering.js";
import { agentReputation } from "./agentReputation.js";

export const OFFERINGS: Record<string, Offering> = {
  search,
  searchAgents,
  browseAgent,
  today,
  composeStack,
  watchOffering,
  agentReputation,
};

export function getOffering(name: string): Offering | undefined {
  return OFFERINGS[name];
}

export function listOfferings(): string[] {
  return Object.keys(OFFERINGS);
}
