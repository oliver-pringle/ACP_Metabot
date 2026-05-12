import type { Offering } from "./types.js";
import { search } from "./search.js";
import { searchAgents } from "./searchAgents.js";
import { browseAgent } from "./browseAgent.js";
import { today } from "./today.js";
import { composeStack } from "./composeStack.js";
import { watchOffering } from "./watchOffering.js";
import { agentReputation } from "./agentReputation.js";
import { arenaParticipants } from "./arenaParticipants.js";
import { buyerOrchestrate } from "./buyerOrchestrate.js";
import { preHireBudgetCheck } from "./preHireBudgetCheck.js";
import { sellerCoachingPack } from "./sellerCoachingPack.js";
import { v1Tov2Migration } from "./v1Tov2Migration.js";

export const OFFERINGS: Record<string, Offering> = {
  search,
  searchAgents,
  browseAgent,
  today,
  composeStack,
  watchOffering,
  agentReputation,
  // v1.7 (Bundle A): Arena marketplace integration. arenaDigestPro
  // (subscription, $4/mo) is deferred to v1.7.1 — clones the watchOffering
  // pattern (new table + repo + poller).
  arenaParticipants,
  // v1.7 (Bundle B): Buyer Agent Toolkit
  buyerOrchestrate,
  preHireBudgetCheck,
  // v1.7 (Bundle C): Seller-Success Coach + V1↔V2 portage
  sellerCoachingPack,
  v1Tov2Migration,
};

export function getOffering(name: string): Offering | undefined {
  return OFFERINGS[name];
}

export function listOfferings(): string[] {
  return Object.keys(OFFERINGS);
}
