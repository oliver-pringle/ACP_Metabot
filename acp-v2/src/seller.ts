import { AcpAgent } from "@virtuals-protocol/acp-node-v2";
import type { JobSession, JobRoomEntry } from "@virtuals-protocol/acp-node-v2";
import { loadEnv } from "./env.js";
import { createProvider } from "./provider.js";
import { createApiClient } from "./apiClient.js";
import { route } from "./router.js";
import { priceForAssetToken } from "./pricing.js";
import { toDeliverable } from "./deliverable.js";
import { listOfferings, getOffering } from "./offerings/registry.js";

type PendingJob = {
  offeringName: string;
  requirement: Record<string, unknown>;
};

async function main() {
  const env = loadEnv();
  const client = createApiClient(env.apiUrl);

  console.log(`[seller] chain=${env.chain} wallet=${env.walletAddress}`);
  console.log(`[seller] api=${env.apiUrl}`);
  console.log(`[seller] offerings registered (in code): ${listOfferings().join(", ")}`);

  const provider = await createProvider(env);
  const agent = await AcpAgent.create({ provider });

  // Keyed by session.jobId so state survives across entries without mutating
  // the SDK session object. Cleared on terminal events.
  const pending = new Map<string, PendingJob>();

  agent.on("entry", async (session: JobSession, entry: JobRoomEntry) => {
    try {
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

    const price = await priceForAssetToken(offeringName, requirement, session.chainId);
    await session.setBudget(price);

    pending.set(session.jobId, { offeringName, requirement });
  }

  async function handleJobFunded(session: JobSession) {
    const stash = pending.get(session.jobId);
    if (!stash) {
      console.warn(`[seller] job.funded without stashed requirement, jobId=${session.jobId}`);
      return;
    }
    const outcome = await route(stash.offeringName, stash.requirement, { client });
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

  console.log("[seller] running — waiting for jobs");
}

main().catch((err) => {
  console.error("[seller] fatal:", err);
  process.exit(1);
});
