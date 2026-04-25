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

export interface SearchResponse {
  query: string;
  count: number;
  results: OfferingMatch[];
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

export interface ApiClient {
  health(): Promise<HealthResponse>;
  search(req: { query: string; limit?: number; minScore?: number }): Promise<SearchResponse>;
  composeStack(req: { useCase: string; budgetUsdc?: number; maxOfferings?: number }): Promise<ComposedStack>;
}

export function createApiClient(baseUrl: string, timeoutMs = 60_000): ApiClient {
  async function request<T>(
    path: string,
    init?: RequestInit & { method?: string }
  ): Promise<T> {
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), timeoutMs);
    try {
      const res = await fetch(`${baseUrl}${path}`, {
        ...init,
        signal: ctl.signal,
        headers: {
          "Content-Type": "application/json",
          ...(init?.headers ?? {}),
        },
      });
      if (!res.ok) {
        const text = await res.text();
        throw new Error(`acp-metabot-api ${res.status}: ${text}`);
      }
      return (await res.json()) as T;
    } finally {
      clearTimeout(timer);
    }
  }

  return {
    health: () => request<HealthResponse>("/health"),
    search: (req) =>
      request<SearchResponse>("/search", { method: "POST", body: JSON.stringify(req) }),
    composeStack: (req) =>
      request<ComposedStack>("/composeStack", { method: "POST", body: JSON.stringify(req) }),
  };
}
