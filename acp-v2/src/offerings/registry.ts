import type { Offering } from "./types.js";
import { today } from "./today.js";
import { composeStack } from "./composeStack.js";
import { watchOffering } from "./watchOffering.js";
import { agentReputation } from "./agentReputation.js";
import { arenaParticipants } from "./arenaParticipants.js";
import { buyerOrchestrate } from "./buyerOrchestrate.js";
import { preHireBudgetCheck } from "./preHireBudgetCheck.js";
import { sellerCoachingPack } from "./sellerCoachingPack.js";
import { v1Tov2Migration } from "./v1Tov2Migration.js";
// v1.8 Portfolio Risk Bot — 4 one-shot offerings + 1 subscription, all
// implemented as cross-bot orchestrators inside TheMetaBot. See
// docs/superpowers/specs for the design.
import { riskSnapshot } from "./riskSnapshot.js";
import { riskDeepDive } from "./riskDeepDive.js";
import { riskCompare } from "./riskCompare.js";
import { riskAttestation } from "./riskAttestation.js";
import { dailyRiskWatch } from "./dailyRiskWatch.js";

// v1.7.2: search / searchAgents / browseAgent moved from paid offerings to
// free Resources (see acp-v2/src/resources.ts). The $0.01 price floor was
// below the per-call hire-lifecycle overhead, so they were structurally
// uneconomic. As free Resources they now act as the discovery funnel into
// the paid offerings below.
export const OFFERINGS: Record<string, Offering> = {
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
  // v1.8 Portfolio Risk Bot — cross-bot orchestrator offerings
  risk_snapshot:    riskSnapshot,
  risk_deep_dive:   riskDeepDive,
  risk_compare:     riskCompare,
  risk_attestation: riskAttestation,
  daily_risk_watch: dailyRiskWatch,
};

export function getOffering(name: string): Offering | undefined {
  return OFFERINGS[name];
}

export function listOfferings(): string[] {
  return Object.keys(OFFERINGS);
}
