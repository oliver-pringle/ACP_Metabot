using System.Text.Json;
using System.Text.Json.Serialization;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services.MarketplaceSource;

/// <summary>
/// ACP V2 marketplace source. Reads from <c>https://api.acp.virtuals.io</c>,
/// the V2 backend hardcoded in the SDK
/// (<c>@virtuals-protocol/acp-node-v2/dist/core/constants.js</c>).
///
/// V2 has no public "list all agents" endpoint (probed 2026-04-30:
/// <c>GET /agents</c> returns 401 even with a valid agent Bearer). Three
/// enumeration paths feed a deduped wallet set:
///
/// <list type="bullet">
/// <item>Source A — distinct sellers from on-chain JobCreated events on
///       the V2 ACP contract. Provided by
///       <see cref="V2KnownSellersRepository"/>; populated incrementally
///       by ChainEventScanner.</item>
/// <item>Source B — keyword sweep against
///       <c>/agents/search?query=X&amp;chainIds=8453&amp;topK=49</c>.
///       Curated keyword list catches cold-start agents that haven't been
///       hired yet.</item>
/// <item>Source C — hardcoded <c>Indexer:V2:KnownAgents</c> config list
///       (defaults to TheMetaBot's own wallet so a fresh deploy is always
///       findable in its own index).</item>
/// </list>
///
/// The deduped wallet set is hydrated via per-wallet
/// <c>GET /agents/wallet/{addr}</c>. Both endpoints work unauthenticated
/// (probed 2026-04-30); auth is wired up only if rate limits surface 429s.
/// </summary>
public class AcpV2MarketplaceSource : IMarketplaceSource
{
    public string MarketplaceVersion => "v2";

    private readonly HttpClient _http;
    private readonly ILogger<AcpV2MarketplaceSource> _logger;
    private readonly V2KnownSellersRepository? _sellersRepo;
    private readonly AgentResourcesRepository? _resourcesRepo;
    private readonly AgentReputationCacheRepository? _reputationCacheRepo;
    private readonly int _chainId;
    private readonly string[] _knownAgents;
    private readonly bool _keywordSweepEnabled;
    private readonly int _keywordSweepTopK;
    private readonly string[] _keywordSweepKeywords;
    private readonly int _requestDelayMs;
    private readonly int _maxConcurrentFetches;

    /// <summary>
    /// Default keyword set for Source B. Spans:
    /// <list type="bullet">
    /// <item>English alphabet (a-z) — matches anything that mentions a single letter prominently.</item>
    /// <item>High-frequency English stop-words — surprisingly effective at catching "tail" agents.</item>
    /// <item>Crypto / DeFi domain terms — matches the most likely V2 agent niches.</item>
    /// <item>Canonical ACP categories — pulls in agents that self-declare those categories.</item>
    /// </list>
    /// </summary>
    private static readonly string[] DefaultKeywords = new[]
    {
        // alphabet
        "a","b","c","d","e","f","g","h","i","j","k","l","m",
        "n","o","p","q","r","s","t","u","v","w","x","y","z",
        // common english
        "the","and","for","with","from","that","this","into","over","new",
        // crypto / defi domain
        "token","swap","yield","stake","liquidity","vault","bridge","oracle","lending",
        "borrowing","perp","options","margin","arbitrage","mev","rugpull","honeypot",
        "wallet","portfolio","trade","trading","alpha","signal","market","price","chart",
        "memecoin","airdrop","staking","governance","dao","nft","defi","cefi",
        // agent / acp domain
        "agent","bot","analytics","research","intelligence","monitor","alert","watch",
        "score","reputation","evaluation","content","summary","translate","caption",
        // chains / ecosystems
        "base","ethereum","solana","arbitrum","optimism","polygon","bsc","virtuals",
    };

    /// <summary>Default known agents — Oliver's V2 portfolio. Override via config.</summary>
    private static readonly string[] DefaultKnownAgents = new[]
    {
        "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c", // TheMetaBot
        "0x997163304142c3a3ff660ad03069b7d78485ca95", // ACP_DeFiEval
        "0xb97552998e7ee94ef2a260fdc25529ed93e4902b", // ACP_AgentEval
        "0x18362cdc11247ee9e37dea29a1cf21f378ec619f", // ACP_LiquidGuard
        "0x827b2c1de0922314f62bc19554044fd649291ca3", // ACP_MEVProtect
        "0x6f28f51743b912197caeadbc3113c955bb80e738", // TheChainlinkBot
        "0xa524de81819e213e8bb181fa0b3747a4a6c3a7e3", // TheArenaBot
        "0xbd9527bdbd61640f544bddd513ed9fcaf9387df8", // TheRevokeBot
        "0xe9b0f88f8f27a7033f4f9679e93ebcfe1a78f7fd", // TheEASIssuerBot
        "0xa42b7122126245858c3cb0dcd0e4c151f3ea48d5", // TheSecurityBot
    };

    public AcpV2MarketplaceSource(
        IConfiguration config,
        IHttpClientFactory httpFactory,
        ILogger<AcpV2MarketplaceSource> logger,
        V2KnownSellersRepository? sellersRepo = null,
        AgentResourcesRepository? resourcesRepo = null,
        AgentReputationCacheRepository? reputationCacheRepo = null)
    {
        _logger = logger;
        _sellersRepo = sellersRepo;
        _resourcesRepo = resourcesRepo;
        _reputationCacheRepo = reputationCacheRepo;
        var baseUrl = config["Indexer:V2:ApiBaseUrl"] ?? "https://api.acp.virtuals.io";
        if (baseUrl.EndsWith("/")) baseUrl = baseUrl.TrimEnd('/');
        _chainId = config.GetValue<int?>("Indexer:V2:ChainId") ?? 8453;
        _keywordSweepEnabled = config.GetValue<bool?>("Indexer:V2:KeywordSweepEnabled") ?? true;
        _keywordSweepTopK = Math.Clamp(
            config.GetValue<int?>("Indexer:V2:KeywordSweepTopK") ?? 49, 1, 49);
        _requestDelayMs = Math.Max(0, config.GetValue<int?>("Indexer:V2:ApiRequestDelayMs") ?? 50);
        _maxConcurrentFetches = Math.Clamp(
            config.GetValue<int?>("Indexer:V2:MaxConcurrentFetches") ?? 4, 1, 16);

        var configKnown = config.GetSection("Indexer:V2:KnownAgents").Get<string[]>();
        _knownAgents = (configKnown is { Length: > 0 } ? configKnown : DefaultKnownAgents)
            .Select(a => a.Trim().ToLowerInvariant())
            .Where(a => a.Length == 42 && a.StartsWith("0x"))
            .Distinct()
            .ToArray();

        var configKeywords = config.GetSection("Indexer:V2:KeywordSweepKeywords").Get<string[]>();
        _keywordSweepKeywords = (configKeywords is { Length: > 0 } ? configKeywords : DefaultKeywords)
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _http = httpFactory.CreateClient(nameof(AcpV2MarketplaceSource));
        _http.BaseAddress ??= new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ACP_Metabot/1.0 (+https://app.virtuals.io)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<IReadOnlyList<MarketplaceOfferingDto>> FetchAsync(CancellationToken ct)
    {
        // 1. Build deduped wallet set from sources A + B + C.
        var wallets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Source C — always include configured / default agents.
        foreach (var w in _knownAgents) wallets.Add(w);
        var sourceCAdded = wallets.Count;

        // Source A — distinct sellers from on-chain JobCreated events.
        if (_sellersRepo is not null)
        {
            try
            {
                var chainSellers = await _sellersRepo.ListAllAsync();
                foreach (var w in chainSellers) wallets.Add(w.ToLowerInvariant());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[v2-source] chain-events seller fetch failed; continuing with B+C");
            }
        }
        var sourceAAdded = wallets.Count - sourceCAdded;

        // Source B — keyword sweep against /agents/search.
        var sourceBAdded = 0;
        if (_keywordSweepEnabled)
        {
            sourceBAdded = await KeywordSweepAsync(wallets, ct);
        }

        _logger.LogInformation(
            "[v2-source] wallet set: total={Total} (C={C}, A={A}, B={B})",
            wallets.Count, sourceCAdded, sourceAAdded, sourceBAdded);

        if (wallets.Count == 0)
        {
            return Array.Empty<MarketplaceOfferingDto>();
        }

        // 2. Per-wallet fan-out to /agents/wallet/{addr}, bounded concurrency.
        var collected = new List<MarketplaceOfferingDto>(capacity: wallets.Count * 3);
        var semaphore = new SemaphoreSlim(_maxConcurrentFetches);
        var tasks = wallets.Select(async wallet =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                if (_requestDelayMs > 0) await Task.Delay(_requestDelayMs, ct);
                return await FetchAgentAsync(wallet, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        foreach (var dtos in results)
        {
            if (dtos is not null) collected.AddRange(dtos);
        }

        _logger.LogInformation(
            "[v2-source] fetched {Offerings} offerings across {Wallets} wallets",
            collected.Count, wallets.Count);

        return collected;
    }

    private async Task<int> KeywordSweepAsync(HashSet<string> wallets, CancellationToken ct)
    {
        var before = wallets.Count;
        foreach (var keyword in _keywordSweepKeywords)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"/agents/search?query={Uri.EscapeDataString(keyword)}" +
                       $"&chainIds={_chainId}&topK={_keywordSweepTopK}";
            try
            {
                var resp = await _http.GetFromJsonAsync<AgentsSearchResponse>(path, ct);
                if (resp?.Data is null) continue;
                foreach (var a in resp.Data)
                {
                    if (!string.IsNullOrEmpty(a.WalletAddress))
                    {
                        wallets.Add(a.WalletAddress.Trim().ToLowerInvariant());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "[v2-source] keyword sweep '{Keyword}' failed; continuing", keyword);
            }
            if (_requestDelayMs > 0)
            {
                try { await Task.Delay(_requestDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        return wallets.Count - before;
    }

    private async Task<List<MarketplaceOfferingDto>?> FetchAgentAsync(string wallet, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<AgentByWalletResponse>(
                $"/agents/wallet/{wallet}", ct);
            if (resp?.Data is null) return null;

            var agent = resp.Data;
            // Filter on the configured chain. Each agent record has a
            // chains[] array; we only index entries for our chain that are
            // currently active.
            var chainBinding = agent.Chains?.FirstOrDefault(c =>
                c.ChainId == _chainId && c.Active);
            if (chainBinding is null) return null;

            // R7-IDEA-C: persist this agent's Resources as a side-effect of
            // the per-wallet fetch. AcpAgentDetail.resources is what every
            // portfolio bot writes via R7-IDEA-A; here we mirror them into
            // SQLite so /v1/agent/{address}/resources and
            // /v1/marketplace/resources/search can expose them. Best-effort —
            // a write failure here must not break offering ingestion, so
            // we swallow + log and continue.
            if (_resourcesRepo is not null && agent.Resources is { Count: > 0 } incomingResources)
            {
                var payload = new List<(string Name, string Url, string? ParamsJson, string Description)>(incomingResources.Count);
                foreach (var r in incomingResources)
                {
                    if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Url)) continue;
                    string? paramsJson = null;
                    if (r.Params.ValueKind != JsonValueKind.Undefined &&
                        r.Params.ValueKind != JsonValueKind.Null)
                    {
                        paramsJson = r.Params.GetRawText();
                    }
                    payload.Add((r.Name, r.Url, paramsJson, r.Description ?? ""));
                }
                if (payload.Count > 0)
                {
                    try
                    {
                        await _resourcesRepo.UpsertManyForAgentAsync(
                            wallet, agent.Name ?? "", MarketplaceVersion, payload, ct);
                    }
                    catch (Exception rex)
                    {
                        _logger.LogWarning(rex,
                            "[v2-source] resources upsert failed for {Wallet}; continuing", wallet);
                    }
                }
            }

            // v1.7.5: agent-level hire count from the reputation cache.
            // The V2 marketplace API doesn't expose hire counts on offerings;
            // ChainEventScanner is the authoritative source but it writes
            // results only to agent_reputation_cache, never back to
            // offerings.agent_job_count. Look up the cached TotalJobs once
            // per agent and apply it to every DTO so the column finally
            // carries a real number for V2-only agents.
            //
            // Falls back to 0 when the agent has never been warmed (same as
            // pre-v1.7.5 behaviour). No TTL — a 7-day-stale count of 50 is
            // strictly better than 0 for ranking.
            long agentJobCount = 0;
            if (_reputationCacheRepo is not null)
            {
                try
                {
                    var cached = await _reputationCacheRepo.GetCachedTotalJobsAsync(wallet);
                    if (cached is long c && c >= 0) agentJobCount = c;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "[v2-source] reputation cache lookup failed for {Wallet}; defaulting to 0", wallet);
                }
            }

            var dtos = new List<MarketplaceOfferingDto>(agent.Offerings?.Count ?? 0);
            foreach (var o in agent.Offerings ?? Enumerable.Empty<V2Offering>())
            {
                if (o.IsHidden) continue;
                if (string.IsNullOrEmpty(o.Name)) continue;

                // V2 stores `requirements` two ways across the marketplace's
                // history: legacy registrations have it as a JSON-as-string,
                // newer ones (LiquidGuard re-register 2026-05-12 onward) have
                // it as a structured JSON object. Handle both ValueKinds so
                // either shape lands as the same JsonElement? downstream.
                var schema = NormalizeSchemaJson(o.Requirements);

                // v1.10 Phase 2 T3a: deliverable schema follows the same
                // dual shape as requirements (AcpAgentOffering.deliverable
                // is Record<string, unknown> | string per the V2 SDK
                // types.d.ts). Same parser path.
                var deliverable = NormalizeSchemaJson(o.Deliverable);

                dtos.Add(new MarketplaceOfferingDto
                {
                    AgentAddress = wallet,
                    AgentName = agent.Name ?? "",
                    OfferingName = o.Name,
                    Description = o.Description ?? "",
                    RequirementSchema = schema,
                    DeliverableSchema = deliverable,
                    PriceUsdc = o.PriceValue ?? 0.0,
                    PriceType = NormalizePriceType(o.PriceType),
                    IsPrivate = false, // V2 isHidden==true is filtered above
                    Chain = "base",
                    // V2 has no usageCount on offerings; per-offering counts
                    // require contract reads (deferred to a future revision).
                    UsageCount = 0,
                    // v1.7.5: agent-level total from the reputation cache
                    // (warmer-populated, ChainEventScanner-sourced). The
                    // per-agent value is repeated on every dto for this
                    // agent — OfferingRepository writes it onto each row
                    // and ReputationService.BuildSearchSummary reads it as
                    // the agent's lifetime total.
                    AgentJobCount = agentJobCount,
                });
            }
            return dtos;
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 404 = wallet isn't a V2 agent. Expected for some sweep results.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "[v2-source] /agents/wallet/{Wallet} failed; skipping", wallet);
            return null;
        }
    }

    private static string NormalizePriceType(string? raw) => (raw ?? "fixed").ToLowerInvariant() switch
    {
        "fixed" => "fixed",
        "percentage" or "percent" => "percent",
        var other => other ?? "fixed"
    };

    /// <summary>
    /// Normalises one of V2's dual-shape schema fields (requirements OR
    /// deliverable). Both arrive as <c>Record&lt;string, unknown&gt; | string</c>
    /// per the SDK's <c>AcpAgentOffering</c> contract — legacy registrations
    /// use the JSON-as-string shape, newer registrations use the structured
    /// object. Returns null when the field is missing / null / unparseable
    /// rather than throwing — schema parsing failures must NOT drop the
    /// whole offering (or its Resources side-effect).
    /// </summary>
    private static JsonElement? NormalizeSchemaJson(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                return el.Clone();
            case JsonValueKind.String:
                var raw = el.GetString();
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var trimmed = raw.Trim().Trim('"');
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    return doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Malformed schema string; surface upstream as null.
                    return null;
                }
            default:
                // null / undefined / number / boolean → null
                return null;
        }
    }

    // ---- response shapes ----

    private class AgentsSearchResponse
    {
        [JsonPropertyName("data")] public List<V2Agent>? Data { get; set; }
    }

    private class AgentByWalletResponse
    {
        [JsonPropertyName("data")] public V2Agent? Data { get; set; }
    }

    private class V2Agent
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("walletAddress")] public string? WalletAddress { get; set; }
        [JsonPropertyName("chains")] public List<V2Chain>? Chains { get; set; }
        [JsonPropertyName("offerings")] public List<V2Offering>? Offerings { get; set; }
        // R7-IDEA-C: AcpAgentDetail.resources from acp-node-v2 ^0.0.6
        // dist/events/types.d.ts:168. Mirrored into SQLite by the
        // _resourcesRepo side-effect inside FetchAgentAsync.
        [JsonPropertyName("resources")] public List<V2Resource>? Resources { get; set; }
    }

    private class V2Chain
    {
        [JsonPropertyName("chainId")] public int ChainId { get; set; }
        [JsonPropertyName("active")] public bool Active { get; set; }
    }

    private class V2Offering
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        // Upstream returns `requirements` as EITHER a JSON-as-string (older
        // marketplace state) OR a structured JSON object (newer registrations
        // — e.g. LiquidGuard after a 2026-05-12 re-register). Declared as
        // JsonElement so System.Text.Json accepts either ValueKind without
        // throwing the entire FetchAgentAsync into the catch (which silently
        // dropped the whole agent — including its Resources side-effect —
        // pre-fix).
        [JsonPropertyName("requirements")] public JsonElement Requirements { get; set; }
        // v1.10 Phase 2 T3a. Same dual-shape contract as Requirements
        // (Record<string, unknown> | string per V2 SDK types.d.ts).
        // Declared JsonElement, not JsonElement?, so missing fields are
        // ValueKind=Undefined — NormalizeSchemaJson returns null for those.
        [JsonPropertyName("deliverable")] public JsonElement Deliverable { get; set; }
        [JsonPropertyName("priceType")] public string? PriceType { get; set; }
        [JsonPropertyName("priceValue")] public double? PriceValue { get; set; }
        [JsonPropertyName("isHidden")] public bool IsHidden { get; set; }
    }

    // R7-IDEA-C: matches AcpAgentResource in
    // @virtuals-protocol/acp-node-v2/dist/events/types.d.ts:168
    // (name, url, params, description). The `params` field is left as
    // JsonElement so it round-trips through SQLite as a raw JSON string.
    private class V2Resource
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("params")] public JsonElement Params { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = "";
    }
}
