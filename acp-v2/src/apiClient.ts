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
  composeStack(req: { useCase: string; budgetUsdc?: number; maxOfferings?: number }): Promise<ComposedStack>;
  registerWatch(req: RegisterWatchRequest): Promise<RegisterWatchResponse>;
  agentReputation(req: AgentReputationRequest): Promise<AgentReputationResponse>;
}

export function createApiClient(
  baseUrl: string,
  apiKey: string,
  timeoutMs = 60_000
): ApiClient {
  // Retry policy:
  //   - 5xx and network errors → retry up to 2 times (3 attempts total) with
  //     1s and 4s backoff. Matters most for /watches because the buyer has
  //     already been funded by the time it runs.
  //   - 4xx errors propagate immediately — they signal a deliberate rejection
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
            // sidecar traffic from cross-bot traffic. Free-text — peers
            // (DeFiEval, AgentEval) send their own value here.
            "X-Caller": "sidecar",
            ...(init?.headers ?? {}),
          },
        });
        if (res.ok) return (await res.json()) as T;

        const text = await res.text();
        const err = new Error(`acp-metabot-api ${res.status}: ${text}`);
        if (res.status >= 400 && res.status < 500) {
          // Client error — do not retry.
          throw err;
        }
        lastError = err;
      } catch (err) {
        // AbortError, network errors, JSON parse errors → retryable.
        // 4xx Errors thrown above are also caught here, so re-throw them.
        if (err instanceof Error && /^acp-metabot-api 4\d\d:/.test(err.message)) {
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
    composeStack: (req) =>
      request<ComposedStack>("/composeStack", { method: "POST", body: JSON.stringify(req) }),
    registerWatch: (req) =>
      request<RegisterWatchResponse>("/watches", { method: "POST", body: JSON.stringify(req) }),
    agentReputation: (req) =>
      request<AgentReputationResponse>("/agentReputation", {
        method: "POST",
        body: JSON.stringify(req),
      }),
  };
}
