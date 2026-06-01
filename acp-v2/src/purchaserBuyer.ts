import type { AcpAgent, JobSession, JobRoomEntry } from "@virtuals-protocol/acp-node-v2";

export type HireStatus = "completed" | "rejected" | "expired" | "timeout" | "error";

export interface HireResult {
  jobId: string;
  status: HireStatus;
  deliverableParsed?: unknown;
  error?: string;
  durationMs: number;
}

interface PendingHire {
  jobId: string;
  funded: boolean;
  deliverable?: string;
  resolve: (r: HireResult) => void;
  startedAt: number;
  timer?: NodeJS.Timeout;
}

/**
 * Inner-hire engine for ACPPurchaser. Reuses the seller's single AcpAgent.
 * seller.ts routes any entry whose jobId isInnerJob() here. Inner hires are
 * serialized (one in-flight) — the SDK signs from one Privy wallet per process,
 * and serialization bounds the fronted-float exposure.
 */
export class PurchaserBuyer {
  private readonly pending = new Map<string, PendingHire>();
  private chain: Promise<void> = Promise.resolve();

  constructor(
    private readonly agent: AcpAgent,
    private readonly chainId: number
  ) {}

  isInnerJob(jobId: string): boolean {
    return this.pending.has(jobId);
  }

  /** Serialized: queue behind any in-flight inner hire. */
  async hireOnBehalf(
    targetAgent: string,
    targetOffering: string,
    requirement: Record<string, unknown>,
    timeoutMs: number
  ): Promise<HireResult> {
    const run = this.chain.then(() => this.doHire(targetAgent, targetOffering, requirement, timeoutMs));
    // keep the chain alive regardless of this hire's outcome
    this.chain = run.then(() => undefined, () => undefined);
    return run;
  }

  private async doHire(
    targetAgent: string,
    targetOffering: string,
    requirement: Record<string, unknown>,
    timeoutMs: number
  ): Promise<HireResult> {
    const detail = await this.agent.getAgentByWalletAddress(targetAgent);
    if (!detail) return this.fail("error", `target ${targetAgent} not found`);
    const offering = detail.offerings.find((o) => o.name === targetOffering);
    if (!offering) return this.fail("error", `offering ${targetOffering} not found on ${detail.name}`);

    const jobIdBig = await this.agent.createJobFromOffering(this.chainId, offering, targetAgent, requirement);
    const jobId = jobIdBig.toString();
    console.log(`[purchaser] inner job ${jobId} created -> ${detail.name}/${targetOffering}`);

    return await new Promise<HireResult>((resolve) => {
      const startedAt = Date.now();
      const p: PendingHire = { jobId, funded: false, resolve, startedAt };
      this.pending.set(jobId, p);
      p.timer = setTimeout(() => {
        if (this.pending.has(jobId)) {
          this.pending.delete(jobId);
          resolve({ jobId, status: "timeout", error: `no completion within ${timeoutMs}ms`, durationMs: Date.now() - startedAt });
        }
      }, timeoutMs);
    });
  }

  private fail(status: HireStatus, error: string): HireResult {
    return { jobId: "", status, error, durationMs: 0 };
  }

  /** Called by seller.ts for any entry whose session.jobId is an inner job. */
  async handleInnerEntry(session: JobSession, entry: JobRoomEntry): Promise<void> {
    const jobId = session.jobId;
    const p = this.pending.get(jobId);
    if (!p) return;
    if (entry.kind !== "system") return;
    const ev = entry.event;
    try {
      switch (ev.type) {
        case "budget.set":
          if (!p.funded) {
            p.funded = true;
            await session.fund(); // funds the inner downstream cost from our wallet
            console.log(`[purchaser] funded inner job ${jobId}`);
          }
          return;
        case "job.completed":
          if (!p.deliverable) await this.recoverDeliverable(jobId, p);
          this.settle(jobId, "completed");
          return;
        case "job.rejected":
          this.settle(jobId, "rejected", (ev as { reason?: string }).reason);
          return;
        case "job.expired":
          this.settle(jobId, "expired");
          return;
      }
    } catch (err) {
      this.settle(jobId, "error", err instanceof Error ? err.message : String(err));
    }
  }

  private async recoverDeliverable(jobId: string, p: PendingHire): Promise<void> {
    const delays = [0, 500, 1500];
    for (const delay of delays) {
      if (delay) await new Promise((r) => setTimeout(r, delay));
      try {
        const history = await this.agent.getTransport().getHistory(this.chainId, jobId);
        for (let i = history.length - 1; i >= 0; i--) {
          const e = history[i];
          if (e.kind === "system" && e.event.type === "job.submitted") {
            p.deliverable = (e.event as { deliverable?: string }).deliverable;
            return;
          }
        }
      } catch { /* indexing lag — retry */ }
    }
  }

  private settle(jobId: string, status: HireStatus, reasonOrError?: string): void {
    const p = this.pending.get(jobId);
    if (!p) return;
    this.pending.delete(jobId);
    if (p.timer) clearTimeout(p.timer);
    let parsed: unknown;
    if (p.deliverable) { try { parsed = JSON.parse(p.deliverable); } catch { /* non-JSON */ } }
    p.resolve({
      jobId, status, deliverableParsed: parsed,
      error: status === "completed" ? undefined : reasonOrError ?? `inner job ${status}`,
      durationMs: Date.now() - p.startedAt,
    });
  }
}
