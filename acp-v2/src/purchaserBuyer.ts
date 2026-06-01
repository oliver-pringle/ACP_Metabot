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
  // P61 drain guard: the max USDC we will fund the inner job for. The TARGET
  // seller sets the inner on-chain budget (attacker-controlled, NOT bound to the
  // listed price we quoted) — we MUST refuse to fund above this cap.
  maxInnerUsdc: number;
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

  /**
   * Serialized: queue behind any in-flight inner hire.
   * @param maxInnerUsdc P61 cap — never fund the inner job above this (the quoted
   *   fixed price). The target's self-set on-chain budget is not trusted.
   */
  async hireOnBehalf(
    targetAgent: string,
    targetOffering: string,
    requirement: Record<string, unknown>,
    maxInnerUsdc: number,
    timeoutMs: number
  ): Promise<HireResult> {
    const run = this.chain.then(() => this.doHire(targetAgent, targetOffering, requirement, maxInnerUsdc, timeoutMs));
    // keep the chain alive regardless of this hire's outcome
    this.chain = run.then(() => undefined, () => undefined);
    return run;
  }

  private async doHire(
    targetAgent: string,
    targetOffering: string,
    requirement: Record<string, unknown>,
    maxInnerUsdc: number,
    timeoutMs: number
  ): Promise<HireResult> {
    const detail = await this.agent.getAgentByWalletAddress(targetAgent);
    if (!detail) return this.fail("error", `target ${targetAgent} not found`);
    const offering = detail.offerings.find((o) => o.name === targetOffering);
    if (!offering) return this.fail("error", `offering ${targetOffering} not found on ${detail.name}`);

    const jobIdBig = await this.agent.createJobFromOffering(this.chainId, offering, targetAgent, requirement);
    const jobId = jobIdBig.toString();
    console.log(`[purchaser] inner job ${jobId} created -> ${detail.name}/${targetOffering} (cap ${maxInnerUsdc} USDC)`);

    return await new Promise<HireResult>((resolve) => {
      const startedAt = Date.now();
      const p: PendingHire = { jobId, funded: false, maxInnerUsdc, resolve, startedAt };
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
        case "budget.set": {
          if (p.funded) return;
          // P61 DRAIN GUARD. The target seller sets the inner on-chain budget; it
          // is attacker-controlled and NOT bound to the listed price we quoted. An
          // attacker lists at $0.01, then setBudget(500) on the inner job → a no-arg
          // session.fund() would escrow 500 from Metabot's float. So: read the inner
          // budget, REFUSE to fund anything over the quoted cap (leave it unfunded —
          // it expires on-chain, no USDC leaves our wallet), refuse any unexpected
          // inner fund-request, and fund the EXACT verified budget (never no-arg).
          const job = session.job;
          if (!job) { this.settle(jobId, "error", "inner_job_not_loaded"); return; }
          const innerBudgetUsdc = job.budget.amount;
          if (!(innerBudgetUsdc <= p.maxInnerUsdc + 1e-9)) {
            console.warn(`[purchaser] REFUSING inner job ${jobId}: budget ${innerBudgetUsdc} > cap ${p.maxInnerUsdc} USDC (P61)`);
            this.settle(jobId, "rejected", `inner_budget_${innerBudgetUsdc}_exceeds_cap_${p.maxInnerUsdc}`);
            return;
          }
          if (job.getFundRequestIntent()) {
            console.warn(`[purchaser] REFUSING inner job ${jobId}: unexpected fund-request intent (P61)`);
            this.settle(jobId, "rejected", "inner_requires_funds_unsupported");
            return;
          }
          p.funded = true;
          await session.fund(job.budget); // explicit, verified <= cap (never no-arg)
          console.log(`[purchaser] funded inner job ${jobId} (${innerBudgetUsdc} USDC, cap ${p.maxInnerUsdc})`);
          return;
        }
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
