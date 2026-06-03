export interface SaturationDto {
  nearDuplicateCount: number;
  categorySize: number;
}

export interface PricePercentileDto {
  value: number | null;
  peerN: number;
  lowN: boolean;
}

export interface OfferingMatch {
  offeringId: number;
  agentName: string;
  agentAddress: string;
  offeringName: string;
  description: string;
  priceUsdc: number;
  priceType: string;
  chain: string;
  score: number;
  saturation?: SaturationDto;
  pricePercentile?: PricePercentileDto;
}

export interface BestMatch {
  agentAddress: string;
  offeringName: string;
  score: number;
}

export interface SearchResponse {
  query: string;
  count: number;
  results: OfferingMatch[];
  bestMatch: BestMatch | null;
}

export interface AgentSearchHitOffering {
  offeringName: string;
  priceUsdc: number;
  marketplaceVersion: "v1" | "v2";
}

export interface AgentSearchHit {
  agentAddress: string;
  agentName: string;
  score: number;
  totalOfferings: number;
  topOfferings: AgentSearchHitOffering[];
  totalJobs: number;
  topOfferingNames: string[];
  marketplaces: ("v1" | "v2")[];
  dominantMarketplace: "v1" | "v2" | "tied" | "none";
  agentScore?: number;
}

export interface AgentSearchResponse {
  query: string;
  count: number;
  agents: AgentSearchHit[];
}

export interface CrossPresenceMarketplace {
  offeringCount: number;
  firstSeenAt: string;
  lastSeenAt: string;
}

export interface CrossPresence {
  v1: CrossPresenceMarketplace | null;
  v2: CrossPresenceMarketplace | null;
  inBoth: boolean;
  dominant: "v1" | "v2" | "tied" | "none";
}

export interface StackEntry {
  offeringName: string;
  agentName: string;
  agentAddress: string;
  priceUsdc: number;
  role: string;
}

export interface ComposedStack {
  rationale: string;
  stack: StackEntry[];
  totalPriceUsdc: number;
}

export interface HealthResponse {
  status: string;
  time: string;
}

export interface RegisterWatchRequest {
  jobId: number;
  buyerAddress: string;
  query: string;
  webhookUrl: string;
  durationDays?: number;
  intervalHours?: number;
  minScore?: number;
  priceMaxUsdc?: number;
  maxAlerts?: number;
}

export interface RegisterWatchResponse {
  watchId: string;
  expiresAt: string;
  intervalHours: number;
  maxAlerts: number;
  initialMatches: OfferingMatch[];
}

export interface AgentReputationRequest {
  agentAddress: string;
}

export interface SubScore {
  value: number;
  score: number;
  percentile: number;
  evidence: string;
  insufficientData: boolean;
}

export interface SubScoreSet {
  completion: SubScore;
  dispute: SubScore;
  recency: SubScore;
  volume30d: SubScore;
  responseTime: SubScore;
}

export interface ReputationRawCounts {
  totalJobs: number;
  completed: number;
  rejected: number;
  expired: number;
  completedLast30d: number;
  lastActiveAt?: string;
}

export interface ReputationFlags {
  isColdStart: boolean;
  insufficientData: boolean;
  warmCacheHit: boolean;
}

export interface ReputationHistoryPoint {
  date: string;            // YYYY-MM-DD UTC
  agentScore: number;
  subScores?: SubScoreSet;
}

export interface AgentReputationResponse {
  agentAddress: string;
  agentName: string;
  agentScore: number;
  computedAt: string;
  windowDays: number;
  subScores: SubScoreSet;
  rawCounts: ReputationRawCounts;
  flags: ReputationFlags;
  trajectory?: ReputationHistoryPoint[];
}

// ── BrowseAgent ───────────────────────────────────────────────────────────────

export interface BrowseAgentOffering {
  offeringId: number;
  offeringName: string;
  description: string;
  priceUsdc: number;
  priceType: string;
  chain: string;
  isPrivate: boolean;
  requirementSchema?: unknown;
  firstSeenAt: string;
  lastSeenAt: string;
  marketplaceVersion: "v1" | "v2";
  pricePercentile?: PricePercentileDto;
}

export interface BrowseAgentResponse {
  agentAddress: string;
  agentName: string;
  reputation: unknown;
  offerings: BrowseAgentOffering[];
  crossPresence?: CrossPresence;
}

// ── Digest ────────────────────────────────────────────────────────────────────

export interface NewAgentRow {
  address: string;
  name: string;
  marketplace: "v1" | "v2";
  firstSeenAt: string;
  offeringCount: number;
}

export interface NewAgentsBlock {
  count: number;
  agents: NewAgentRow[];
}

export interface ChurnRate {
  rate: number;
  churnedCount: number;
  baselineCount: number;
}

export interface CohortSurvivalRow {
  cohortWeek: string;
  cohortStart: string;
  cohortSize: number;
  surviving: number;
  survivalRate: number;
}

export interface SaturationMapRow {
  category: string;
  total: number;
  saturatedCount: number;
  saturationPct: number;
}

export interface DigestResponse {
  windowDays: number;
  windowStart: string;
  snapshotComparison: string;
  partial: boolean;
  newOfferings: unknown[];
  gainers: unknown[];
  newAgents: NewAgentsBlock;
  churnRate: ChurnRate;
  cohortSurvival: CohortSurvivalRow[] | null;
  saturationMap: SaturationMapRow[];
  computedAt: string;
}

export interface ApiClient {
  health(): Promise<HealthResponse>;
  search(req: {
    query: string;
    limit?: number;
    minScore?: number;
    priceMaxUsdc?: number;
    staleAfterDays?: number;
    chain?: string[];
    minReputation?: number;
    freshness?: number;
  }): Promise<SearchResponse>;
  searchAgents(req: {
    query: string;
    limit?: number;
    marketplace?: "v1" | "v2";
  }): Promise<AgentSearchResponse>;
  browseAgent(address: string): Promise<BrowseAgentResponse>;
  digest(req: {
    days?: number;
    marketplace?: string;
  }): Promise<DigestResponse>;
  composeStack(req: { useCase: string; budgetUsdc?: number; maxOfferings?: number }): Promise<ComposedStack>;
  registerWatch(req: RegisterWatchRequest): Promise<RegisterWatchResponse>;
  agentReputation(req: AgentReputationRequest): Promise<AgentReputationResponse>;

  // v1.7 paid offerings
  arenaParticipants(req: { addresses: string[] }): Promise<unknown>;
  buyerOrchestrate(req: { useCase: string; budgetUsdc?: number; maxOfferings?: number }): Promise<unknown>;
  budgetCheck(req: { offeringIds: number[] }): Promise<unknown>;
  // ACPPurchaser Path A (R16 #1 cold-start fix).
  purchaseQuote(req: { targetAgent: string; downstreamUsdc: number; fixedPrice: boolean }): Promise<unknown>;
  purchasePrecheck(req: { outerJobId: string; buyerKey: string; targetAgent: string; targetOffering: string; downstreamUsdc: number; maxFundsUsdc: number }): Promise<{ ok: boolean; reason?: string; downstreamUsdc: number }>;
  purchaseSettle(req: { outerJobId: string; buyerKey: string; state: string; innerJobId?: string | null; reason?: string | null; downstreamUsdc: number }): Promise<unknown>;
  // Stack Purchase Router (T11). stackQuote delegates to C# /v1/buyer/stack/quote;
  // stackPrecheck/stackSettle are internal-only (X-API-Key gated).
  stackQuote(req: { subject: string; intent: string; maxFundsUsdc: number; maxSteps?: number }): Promise<unknown>;
  stackPrecheck(req: { outerJobId: string; buyerKey: string; quoteId: string; subject: string }): Promise<{
    ok: boolean; reason?: string;
    steps: Array<{ targetAgent: string; targetOffering: string; role: string; quotedPriceUsdc: number; riskTier: string; innerRequirement: Record<string, unknown> }>;
    totalDownstreamUsdc: number;
  }>;
  stackSettle(req: { outerJobId: string; buyerKey: string; state: string; innerJobIds?: string | null; reason?: string | null; totalDownstreamUsd: number }): Promise<unknown>;
  sellerCoaching(req: { agent: string }): Promise<unknown>;
  v1Tov2Migration(req: { agent: string }): Promise<unknown>;

  // v1.8 Portfolio Risk Bot  -  cross-bot orchestrator offerings
  riskSnapshot(req: { wallet: string; chain?: "base" | "ethereum" }): Promise<unknown>;
  riskDeepDive(req: { wallet: string; chain?: "base" | "ethereum" }): Promise<unknown>;
  riskCompare(req: { wallets: string[]; chain?: "base" | "ethereum" }): Promise<unknown>;
  riskAttestation(req: { wallet: string; chain?: "base" | "ethereum" }): Promise<unknown>;
  dailyRiskWatch(req: {
    jobId: number;
    buyerAddress: string;
    wallet: string;
    webhookUrl: string;
    chain?: "base" | "ethereum";
  }): Promise<unknown>;

  // v1.9 marketplaceGap  -  "where should I build?"
  // v1.10.1: marketplace ∈ {v1, v2, both}. Default "v2" at the C# endpoint
  // (BC-shift from pre-v1.10.1 "both"); the sidecar passes whatever the
  // buyer set, undefined elides the field on the wire.
  marketplaceGap(req: { category?: string; limit?: number; marketplace?: "v1" | "v2" | "both" }): Promise<unknown>;

  // v1.9 marketplacePulseSub  -  daily digest subscription create
  marketplacePulseSub(req: {
    jobId: number;
    buyerAddress: string;
    webhookUrl: string;
    marketplace?: "v1" | "v2" | "both";
  }): Promise<unknown>;

  // R12 Tier 1.3  -  agent_smoke_check: static-analysis smoke test for any
  // V2 ACP agent's offering. v0.2 wires real-hire via docker-ops-sidecar.
  agentSmokeCheck(req: {
    targetAgent: string;
    offeringName?: string;
    sampleRequirement?: Record<string, unknown>;
  }): Promise<unknown>;

  // v1.10 Phase 3 T4  -  searchNarrative: Claude-Haiku narrated wrap of the
  // top-5 search hits for a buyer query. Forces top-5 + no
  // resources/expand/risk on the underlying search, 1h cache.
  searchNarrative(req: {
    query: string;
    previousQueries?: string[];
    marketplace?: "v1" | "v2";
  }): Promise<unknown>;

  // v1.10 Phase 3 T5  -  agentRiskCheck: 4-signal scam-risk score for one agent
  // on one chain (1 = Ethereum, 8453 = Base; default 8453). 6h cache.
  agentRiskCheck(req: {
    agentAddress: string;
    chainId?: number;
  }): Promise<unknown>;

  // v1.0 riskAttestPro  -  premium $10 7-lane cross-bot risk briefing.
  // Endpoint: POST /v1/risk/attest-pro. walletAddress is required + must match
  // ^0x[0-9a-f]{40}$ (case-insensitive; the endpoint lowercases). chain
  // defaults to 'base' when unset; only 'base'/'ethereum' accepted. fresh:true
  // bypasses the 1h wallet-response cache. buyerSignature is surfaced forward
  // for v1.1 strict-mode binding  -  accepted on v1.0 without enforcement.
  riskAttestPro(req: {
    walletAddress: string;
    chain?: "base" | "ethereum";
    buyerSignature?: string;
    fresh?: boolean;
  }): Promise<unknown>;
}

export function createApiClient(
  baseUrl: string,
  apiKey: string,
  timeoutMs = 60_000
): ApiClient {
  // Retry policy:
  //   - 5xx and network errors -> retry up to 2 times (3 attempts total) with
  //     1s and 4s backoff. Matters most for /watches because the buyer has
  //     already been funded by the time it runs.
  //   - 4xx errors propagate immediately  -  they signal a deliberate rejection
  //     (e.g. SSRF guard) and retrying would only burn time.
  const retryDelaysMs = [1000, 4000];

  async function request<T>(
    path: string,
    init?: RequestInit & { method?: string }
  ): Promise<T> {
    let lastError: unknown;
    for (let attempt = 0; attempt <= retryDelaysMs.length; attempt++) {
      if (attempt > 0) {
        await new Promise((r) => setTimeout(r, retryDelaysMs[attempt - 1]));
      }
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), timeoutMs);
      try {
        const res = await fetch(`${baseUrl}${path}`, {
          ...init,
          signal: ctl.signal,
          headers: {
            "Content-Type": "application/json",
            "X-API-Key": apiKey,
            // Tag internal calls so the C# tier's request_log can split
            // sidecar traffic from cross-bot traffic. Free-text  -  peers
            // (DeFiEval, AgentEval) send their own value here.
            "X-Caller": "sidecar",
            ...(init?.headers ?? {}),
          },
        });
        if (res.ok) return (await res.json()) as T;

        // P63: never embed the upstream response body in the thrown Error — it
        // can carry RPC API keys (P9) and internal route detail, and router.ts /
        // seller.ts relay err.message to the paying buyer. Log the body
        // server-side; throw an opaque, status-tagged message.
        console.error(`[apiClient] ${init?.method ?? "GET"} ${path} -> ${res.status}: ${await res.text()}`);
        const err = new Error(`upstream error (status ${res.status}) [${init?.method ?? "GET"} ${path}]`);
        if (res.status >= 400 && res.status < 500) {
          // Client error  -  do not retry.
          throw err;
        }
        lastError = err;
      } catch (err) {
        // AbortError, network errors, JSON parse errors -> retryable.
        // 4xx Errors thrown above are also caught here, so re-throw them.
        // (Matches the opaque message minted above — body is never embedded.)
        if (err instanceof Error && /^upstream error \(status 4\d\d\)/.test(err.message)) {
          throw err;
        }
        lastError = err;
      } finally {
        clearTimeout(timer);
      }
    }
    throw lastError ?? new Error("acp-metabot-api: request failed after retries");
  }

  return {
    health: () => request<HealthResponse>("/health"),
    search: (req) =>
      request<SearchResponse>("/search", { method: "POST", body: JSON.stringify(req) }),
    searchAgents: (req) =>
      request<AgentSearchResponse>("/searchAgents", { method: "POST", body: JSON.stringify(req) }),
    browseAgent: (address) =>
      request<BrowseAgentResponse>(`/agent/${encodeURIComponent(address)}`),
    digest: (req) => {
      const params = new URLSearchParams();
      if (req.days !== undefined) params.set("days", String(req.days));
      if (req.marketplace) params.set("marketplace", req.marketplace);
      const qs = params.toString();
      return request<DigestResponse>(qs ? `/digest?${qs}` : "/digest");
    },
    composeStack: (req) =>
      request<ComposedStack>("/composeStack", { method: "POST", body: JSON.stringify(req) }),
    registerWatch: (req) =>
      request<RegisterWatchResponse>("/watches", { method: "POST", body: JSON.stringify(req) }),
    agentReputation: (req) =>
      request<AgentReputationResponse>("/agentReputation", {
        method: "POST",
        body: JSON.stringify(req),
      }),

    // v1.7 paid offerings
    arenaParticipants: (req) =>
      request<unknown>("/v1/arena/participants-bulk", { method: "POST", body: JSON.stringify(req) }),
    buyerOrchestrate: (req) =>
      request<unknown>("/v1/buyer/orchestrate", { method: "POST", body: JSON.stringify(req) }),
    budgetCheck: (req) =>
      request<unknown>("/v1/buyer/budget-check", { method: "POST", body: JSON.stringify(req) }),
    purchaseQuote: (req) =>
      request<unknown>("/v1/buyer/purchase/quote", { method: "POST", body: JSON.stringify(req) }),
    purchasePrecheck: (req) =>
      request<{ ok: boolean; reason?: string; downstreamUsdc: number }>(
        "/v1/internal/buyer/purchase/precheck", { method: "POST", body: JSON.stringify(req) }),
    purchaseSettle: (req) =>
      request<unknown>("/v1/internal/buyer/purchase/settle", { method: "POST", body: JSON.stringify(req) }),
    // Stack Purchase Router (T11)
    stackQuote: (req) =>
      request<unknown>("/v1/buyer/stack/quote", { method: "POST", body: JSON.stringify(req) }),
    stackPrecheck: (req) =>
      request("/v1/internal/buyer/stack/precheck", { method: "POST", body: JSON.stringify(req) }),
    stackSettle: (req) =>
      request<unknown>("/v1/internal/buyer/stack/settle", { method: "POST", body: JSON.stringify(req) }),
    sellerCoaching: (req) =>
      request<unknown>("/v1/seller/coaching", { method: "POST", body: JSON.stringify(req) }),
    v1Tov2Migration: (req) =>
      request<unknown>("/v1/seller/migration", { method: "POST", body: JSON.stringify(req) }),

    // v1.8 Portfolio Risk Bot  -  every endpoint is /v1/risk/*.
    riskSnapshot: (req) =>
      request<unknown>("/v1/risk/snapshot", { method: "POST", body: JSON.stringify(req) }),
    riskDeepDive: (req) =>
      request<unknown>("/v1/risk/deep-dive", { method: "POST", body: JSON.stringify(req) }),
    riskCompare: (req) =>
      request<unknown>("/v1/risk/compare", { method: "POST", body: JSON.stringify(req) }),
    riskAttestation: (req) =>
      request<unknown>("/v1/risk/attestation", { method: "POST", body: JSON.stringify(req) }),
    // /v1/internal/*  -  INTERNAL_API_KEY gated. The sidecar already sends
    // X-API-Key on every call (see request() default headers). Moved out of
    // the public /v1/* surface to refuse subscription creation from anyone
    // who can't prove they paid escrow.
    dailyRiskWatch: (req) =>
      request<unknown>("/v1/internal/risk/watch", { method: "POST", body: JSON.stringify(req) }),

    // v1.9 marketplaceGap
    marketplaceGap: (req) =>
      request<unknown>("/v1/marketplace/gap", { method: "POST", body: JSON.stringify(req) }),

    // v1.9 marketplacePulseSub  -  /v1/internal/* (escrow-gated, see above).
    marketplacePulseSub: (req) =>
      request<unknown>("/v1/internal/marketplace/pulse/subscribe", {
        method: "POST",
        body: JSON.stringify(req),
      }),

    // R12 Tier 1.3  -  agentSmokeCheck
    agentSmokeCheck: (req) =>
      request<unknown>("/v1/smoke/check", {
        method: "POST",
        body: JSON.stringify(req),
      }),

    // v1.10 Phase 3 T4  -  searchNarrative. The C# endpoint takes a wrapped
    // SearchNarrativeRequest({Search:SearchRequest, PreviousQueries:string[]?})
    // because the underlying narrator forces top-5 + no resources/expand/risk
    // on the search side. The sidecar passes only the buyer-facing fields
    // (query + previousQueries + optional marketplace filter); all the
    // limit/offset/rerank knobs live behind the endpoint.
    searchNarrative: (req) =>
      request<unknown>("/v1/searchNarrative", {
        method: "POST",
        body: JSON.stringify({
          search: {
            query: req.query,
            marketplace: req.marketplace,
          },
          previousQueries: req.previousQueries,
        }),
      }),

    // v1.10 Phase 3 T5  -  agentRiskCheck. ChainId defaults to 8453 (Base) at
    // the endpoint when unset; only 1 and 8453 are accepted.
    agentRiskCheck: (req) =>
      request<unknown>("/v1/agentRiskCheck", {
        method: "POST",
        body: JSON.stringify(req),
      }),

    // v1.0 riskAttestPro  -  premium 7-lane cross-bot orchestrator. The C#
    // endpoint validates walletAddress + lowercases, validates chain default
    // 'base', honours fresh:true to bypass the 1h wallet cache, and decorates
    // the response with cacheHit:true/false. buyerSignature is accepted but
    // unused in v1.0 (v1.1 strict-mode binding hook).
    riskAttestPro: (req) =>
      request<unknown>("/v1/risk/attest-pro", {
        method: "POST",
        body: JSON.stringify(req),
      }),
  };
}
