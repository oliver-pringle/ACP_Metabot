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
// v1.9 marketplace gap finder — "where should I build?". Repackages the
// saturationMap that /digest already exposes for free into an opportunity-
// ranked, recommendation-tagged response.
import { marketplaceGap } from "./marketplaceGap.js";
// v1.9 TheMetaBot's first recurring tier — daily marketplace digest pushed
// via HMAC webhook on the BasicSubscriptionBot pattern.
import { marketplacePulseSub } from "./marketplacePulseSub.js";
// R12 Tier 1.3 — productises ACP_Tester test-hire primitive as $0.10 offering.
import { agentSmokeCheck } from "./agentSmokeCheck.js";
// v1.10 Phase 3 T4 + T5 — Smart Search Claude-narrated summary + defensive
// agent risk score. Two $0.05 paid offerings; see specs in
// docs/superpowers/specs/2026-05-18-metabot-v1.10-smart-search-design.md
// and the Phase 3 plan.
import { searchNarrative } from "./searchNarrative.js";
import { agentRiskCheck } from "./agentRiskCheck.js";

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
  // v1.9 marketplace gap finder ($0.30 one-shot)
  marketplaceGap,
  // v1.9 marketplace pulse subscription ($4.00 / 30 days daily digest)
  marketplacePulseSub,
  // R12 Tier 1.3 — agent_smoke_check ($0.10) — static-analysis smoke test
  // for any V2 agent's offering. v0.2 wires real-hire via docker-ops-sidecar.
  agent_smoke_check: agentSmokeCheck,
  // v1.10 Phase 3 — Smart Search narrative + defensive risk score. Both
  // priced at $0.05 (matching the $0.05 floor on similar one-shot reads
  // like agentReputation / arenaParticipants).
  searchNarrative,
  agentRiskCheck,
};

export function getOffering(name: string): Offering | undefined {
  return OFFERINGS[name];
}

export function listOfferings(): string[] {
  return Object.keys(OFFERINGS);
}
