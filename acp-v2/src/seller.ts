import { AcpAgent, AssetToken } from "@virtuals-protocol/acp-node-v2";
import type { JobSession, JobRoomEntry } from "@virtuals-protocol/acp-node-v2";
import { getChain } from "./chain.js";
import { PurchaserBuyer } from "./purchaserBuyer.js";
import { loadEnv } from "./env.js";
import { createProvider } from "./provider.js";
import { createApiClient } from "./apiClient.js";
import { route } from "./router.js";
import { priceForAssetToken } from "./pricing.js";
import { toDeliverable } from "./deliverable.js";
import { listOfferings, getOffering } from "./offerings/registry.js";
import { listResources } from "./resources.js";

type PendingJob =
  | { kind: "normal"; offeringName: string; requirement: Record<string, unknown> }
  | {
      kind: "execute";
      offeringName: "purchase_execute";
      requirement: Record<string, unknown>;
      targetAgent: string;
      targetOffering: string;
      innerRequirement: Record<string, unknown>;
      downstreamUsdc: number;
      buyerKey: string;
    };

async function main() {
  const env = loadEnv();
  const client = createApiClient(env.apiUrl, env.apiKey);

  console.log(`[seller] chain=${env.chain} wallet=${env.walletAddress}`);
  console.log(`[seller] api=${env.apiUrl}`);
  console.log(`[seller] offerings registered (in code): ${listOfferings().join(", ")}`);
  console.log(`[seller] resources registered (in code): ${listResources().join(", ") || "(none)"}`);

  const provider = await createProvider(env);
  const agent = await AcpAgent.create({ provider });

  // ACPPurchaser Path A: this single agent is BOTH seller and (for
  // purchase_execute) buyer. The PurchaserBuyer owns inner hires on the same
  // wallet; inner-job events are routed to it (see the entry handler).
  const chainId = getChain(env.chain).id;
  const purchaser = new PurchaserBuyer(agent, chainId);

  // Keyed by session.jobId so state survives across entries without mutating
  // the SDK session object. Cleared on terminal events.
  const pending = new Map<string, PendingJob>();

  agent.on("entry", async (session: JobSession, entry: JobRoomEntry) => {
    try {
      // Inner hires (ACPPurchaser purchase_execute) ride this same agent. Route
      // their events to the buyer engine and stop — they are NOT seller jobs.
      if (purchaser.isInnerJob(session.jobId)) {
        await purchaser.handleInnerEntry(session, entry);
        return;
      }

      if (entry.kind === "system") {
        switch (entry.event.type) {
          case "job.created":
            console.log(`[seller] job.created jobId=${session.jobId}`);
            return;
          case "job.funded":
            return await handleJobFunded(session);
          case "job.completed":
            console.log(`[seller] job.completed jobId=${session.jobId}`);
            pending.delete(session.jobId);
            return;
          case "job.rejected":
            console.log(`[seller] job.rejected jobId=${session.jobId}`);
            pending.delete(session.jobId);
            return;
          case "job.expired":
            pending.delete(session.jobId);
            return;
          default:
            return;
        }
      }

      if (entry.kind === "message" && entry.contentType === "requirement") {
        await handleRequirement(session, entry);
        return;
      }
    } catch (err) {
      console.error(`[seller] handler error for job ${session.jobId}:`, err);
    }
  });

  async function handleRequirement(session: JobSession, entry: JobRoomEntry) {
    if (entry.kind !== "message") return;

    let requirement: Record<string, unknown>;
    try {
      requirement = JSON.parse(entry.content);
    } catch {
      await session.sendMessage("invalid requirement payload");
      return;
    }

    // Offering name lives on the on-chain job description, not in the message body.
    // The SDK's createJobFromOffering sends the raw requirement payload as the "requirement"
    // message and stores offering.name in AcpJob.description.
    const job = session.job ?? (await session.fetchJob());
    const offeringName = job.description;

    // Refuse jobs with a non-zero evaluator. With a buyer-controlled evaluator,
    // the buyer can take our deliverable and then reject() to deny payment.
    // Insisting on the zero-address evaluator means submission auto-completes
    // on-chain.
    const ZERO_ADDRESS = "0x0000000000000000000000000000000000000000";
    if (job.evaluatorAddress.toLowerCase() !== ZERO_ADDRESS) {
      await session.sendMessage(
        `unsupported: this seller only accepts jobs with evaluatorAddress=${ZERO_ADDRESS}. Got: ${job.evaluatorAddress}`
      );
      return;
    }

    const offering = getOffering(offeringName);
    if (!offering) {
      await session.sendMessage(`unknown offering: ${offeringName}`);
      return;
    }
    const v = offering.validate(requirement);
    if (!v.valid) {
      await session.sendMessage(v.reason ?? "validation failed");
      return;
    }

    if (offeringName === "purchase_execute") {
      const targetAgent = String(requirement.targetAgent);
      const targetOffering = String(requirement.targetOffering);
      const maxFundsUsdc = Number(requirement.maxFundsUsdc);
      const innerRequirement = (requirement.requirement ?? {}) as Record<string, unknown>;

      // Resolve the downstream price live; reject non-fixed / not-found here.
      const detail = await agent.getAgentByWalletAddress(targetAgent);
      const off = detail?.offerings.find((o) => o.name === targetOffering);
      if (!off) {
        await session.sendMessage(`target offering not found: ${targetAgent}/${targetOffering}`);
        return;
      }
      const fixedPrice = (off.priceType || "").toLowerCase() === "fixed";
      if (!fixedPrice) {
        await session.sendMessage("purchase_execute v1 supports fixed-price targets only");
        return;
      }
      const downstreamUsdc = Number(off.priceValue);

      const buyerKey = job.clientAddress;
      const pre = await client.purchasePrecheck({
        outerJobId: session.jobId, buyerKey, targetAgent, targetOffering, downstreamUsdc, maxFundsUsdc,
      });
      if (!pre.ok) {
        await session.sendMessage(`precheck rejected: ${pre.reason}`);
        return;
      }

      // Service fee = our budget; downstream cost = the fund request to our own wallet.
      await session.setBudgetWithFundRequest(
        AssetToken.usdc(0.1, session.chainId),
        AssetToken.usdc(downstreamUsdc, session.chainId),
        env.walletAddress as `0x${string}`,
      );
      pending.set(session.jobId, {
        kind: "execute", offeringName: "purchase_execute", requirement,
        targetAgent, targetOffering, innerRequirement, downstreamUsdc, buyerKey,
      });
      return;
    }

    const price = await priceForAssetToken(offeringName, requirement, session.chainId);
    await session.setBudget(price);

    pending.set(session.jobId, { kind: "normal", offeringName, requirement });
  }

  async function handleJobFunded(session: JobSession) {
    const stash = pending.get(session.jobId);
    if (!stash) {
      console.warn(`[seller] job.funded without stashed requirement, jobId=${session.jobId}`);
      return;
    }
    if (stash.kind === "execute") {
      // P61: cap the inner fund at the quoted downstream price (precheck already
      // guarantees downstreamUsdc <= maxFundsUsdc). The buyer engine refuses to
      // fund any inner on-chain budget above this.
      const hire = await purchaser.hireOnBehalf(
        stash.targetAgent, stash.targetOffering, stash.innerRequirement, stash.downstreamUsdc, 240_000);
      if (hire.status === "completed" && hire.deliverableParsed !== undefined) {
        const deliverable = {
          status: "DELIVERED", targetAgent: stash.targetAgent, targetOffering: stash.targetOffering,
          innerJobId: hire.jobId, downstreamUsdc: stash.downstreamUsdc, serviceFeeUsdc: 0.1,
          deliverable: hire.deliverableParsed, reason: "",
        };
        // Require-Funds job: the submit MUST carry the transferAmount so the
        // FundTransferHook executes the fund transfer (downstream cost) to our
        // wallet — the destination set in setBudgetWithFundRequest. A plain
        // submit() reverts (0x3224cff4) because the fund request is unsettled.
        await session.submit(
          await toDeliverable(session.jobId, deliverable),
          AssetToken.usdc(stash.downstreamUsdc, session.chainId),
        );
        await client.purchaseSettle({ outerJobId: session.jobId, buyerKey: stash.buyerKey, state: "DELIVERED",
          innerJobId: hire.jobId, reason: null, downstreamUsdc: stash.downstreamUsdc });
        console.log(`[seller] purchase_execute DELIVERED jobId=${session.jobId} inner=${hire.jobId}`);
      } else {
        const reason = `downstream_failed:${hire.status}${hire.error ? `:${hire.error}` : ""}`;
        await session.reject(reason);
        await client.purchaseSettle({ outerJobId: session.jobId, buyerKey: stash.buyerKey, state: "REJECTED",
          innerJobId: hire.jobId || null, reason, downstreamUsdc: stash.downstreamUsdc });
        console.log(`[seller] purchase_execute REJECTED jobId=${session.jobId} reason=${reason}`);
      }
      return;
    }

    const outcome = await route(stash.offeringName, stash.requirement, { client, session, agent });
    if (!outcome.ok) {
      await session.sendMessage(`execution failed: ${outcome.reason}`);
      return;
    }
    const payload = await toDeliverable(session.jobId, outcome.result);
    await session.submit(payload);
    console.log(`[seller] submitted jobId=${session.jobId} offering=${stash.offeringName}`);
  }

  await agent.start();

  const shutdown = async (signal: string) => {
    console.log(`[seller] ${signal} received, stopping agent`);
    try {
      await agent.stop();
    } finally {
      process.exit(0);
    }
  };
  process.on("SIGINT", () => void shutdown("SIGINT"));
  process.on("SIGTERM", () => void shutdown("SIGTERM"));

  console.log("[seller] running  -  waiting for jobs");
}

main().catch((err) => {
  console.error("[seller] fatal:", err);
  process.exit(1);
});
