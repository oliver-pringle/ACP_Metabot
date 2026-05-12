using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

// Five v1.7 paid offerings extracted out of Program.cs to keep the latter
// readable. Each method maps 1:1 to a POST endpoint mapped in Program.cs:
//
//   arenaParticipants        → POST /v1/arena/participants-bulk
//   buyerStackOrchestration  → POST /v1/buyer/orchestrate
//   preHireBudgetCheck       → POST /v1/buyer/budget-check
//   sellerCoachingPack       → POST /v1/seller/coaching
//   v1Tov2Migration          → POST /v1/seller/migration
//
// arenaDigestPro (subscription) is intentionally deferred — it needs the
// watchOffering subscription pattern cloned (new table + repository +
// background poller). Punt to v1.7.1.
public class V17PaidOfferingsService
{
    private readonly AgentArenaParticipationRepository _arena;
    private readonly OfferingRepository                _offerings;
    private readonly AgentResourcesRepository          _resources;
    private readonly StackComposerService              _stackComposer;
    private readonly ReputationService                 _reputation;
    private readonly ILogger<V17PaidOfferingsService>  _logger;

    public V17PaidOfferingsService(
        AgentArenaParticipationRepository arena,
        OfferingRepository offerings,
        AgentResourcesRepository resources,
        StackComposerService stackComposer,
        ReputationService reputation,
        ILogger<V17PaidOfferingsService> logger)
    {
        _arena         = arena;
        _offerings     = offerings;
        _resources     = resources;
        _stackComposer = stackComposer;
        _reputation    = reputation;
        _logger        = logger;
    }

    // ── arenaParticipants ($0.05) ─────────────────────────────────────────────
    //
    // Bulk lookup of 1-25 agent addresses against the agent_arena_participation
    // index. Returns isParticipant + ranks + PnL per address. Buyer agents call
    // this to filter a candidate cohort before paying for arena_agent_report on
    // ArenaBot proper.
    public async Task<object> ArenaParticipantsAsync(IReadOnlyList<string> addresses)
    {
        var seen = new HashSet<string>();
        var rows = new List<object>();
        foreach (var raw in addresses)
        {
            var addr = (raw ?? "").Trim().ToLowerInvariant();
            if (!IsValidAddress(addr)) continue;
            if (!seen.Add(addr)) continue;

            var p = await _arena.GetByAddressAsync(addr);
            rows.Add(new
            {
                agentAddress  = addr,
                isParticipant = p is not null && p.IsParticipant,
                rankLifetime  = p?.RankLifetime,
                rank30d       = p?.Rank30d,
                pnlLifetimeUsd = p?.PnlLifetimeUsd,
                pnl30dUsd     = p?.Pnl30dUsd,
                lastWeekPick  = p?.LastWeekPick,
                lastObservedAt = p?.LastObservedAt.ToString("O")
            });
        }
        return new
        {
            requested = addresses.Count,
            indexed   = rows.Count(r => (bool)r.GetType().GetProperty("isParticipant")!.GetValue(r)!),
            agents    = rows
        };
    }

    // ── buyerStackOrchestration ($0.10) ───────────────────────────────────────
    //
    // composeStack + reputation badges. Computes a recommended stack for a use
    // case, then for each stack entry looks up the seller's cached reputation
    // (and Arena participation) so the buyer can rank by trust signal.
    public async Task<object> BuyerStackOrchestrationAsync(
        string useCase, double? budgetUsdc, int? maxOfferings, CancellationToken ct)
    {
        var max = Math.Clamp(maxOfferings ?? 5, 1, 10);
        var stack = await _stackComposer.ComposeAsync(useCase, budgetUsdc, max, null, null, ct);

        // For each entry, pull cached reputation + Arena participation. Cached
        // hits only — we never trigger a live reputation compute from here
        // (that would gas the per-call price and exceed slaMinutes).
        var enriched = new List<object>();
        foreach (var entry in stack.Stack)
        {
            var addr = (entry.AgentAddress ?? "").ToLowerInvariant();
            object? repBadge = null;
            object? arenaBadge = null;
            try
            {
                if (_reputation.IsReady)
                {
                    // Use the offerings list to power a search-summary score;
                    // this stays cheap and doesn't trigger chain scans.
                    var offerings = await _offerings.ListByAgentAsync(addr);
                    if (offerings.Count > 0)
                    {
                        var sum = _reputation.BuildSearchSummary(offerings[0]);
                        repBadge = sum;
                    }
                }
                var arena = await _arena.GetByAddressAsync(addr);
                if (arena is not null && arena.IsParticipant)
                    arenaBadge = new
                    {
                        rankLifetime = arena.RankLifetime,
                        rank30d      = arena.Rank30d,
                        lastWeekPick = arena.LastWeekPick
                    };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[v17] orchestrate enrichment failed for {Addr}", addr);
            }
            enriched.Add(new
            {
                offeringName  = entry.OfferingName,
                agentName     = entry.AgentName,
                agentAddress  = entry.AgentAddress,
                priceUsdc     = entry.PriceUsdc,
                role          = entry.Role,
                reputation    = repBadge,
                arenaParticipation = arenaBadge
            });
        }

        return new
        {
            useCase,
            rationale       = stack.Rationale,
            totalPriceUsdc  = stack.TotalPriceUsdc,
            stack           = enriched,
            note            = "Reputation badges come from Metabot's cached reputation index; arenaParticipation comes from the v1.7 arena-source poll. Neither triggers a fresh on-chain compute — call /v1/agentReputation directly for live evidence."
        };
    }

    // ── preHireBudgetCheck ($0.02) ────────────────────────────────────────────
    //
    // Given a list of offering IDs, return per-offering price + total + any
    // missing ids. Tiny compute; the value is the buyer can call it BEFORE
    // setBudget()-ing each individual ACP job. We DO NOT validate that the
    // buyer's wallet actually holds the USDC — that's the buyer's job.
    public async Task<object> PreHireBudgetCheckAsync(IReadOnlyList<long> offeringIds)
    {
        var lines = new List<object>();
        double total = 0;
        var missing = new List<long>();
        foreach (var id in offeringIds.Distinct().Take(25))
        {
            var off = await _offerings.GetByIdAsync(id);
            if (off is null) { missing.Add(id); continue; }
            total += off.PriceUsdc;
            lines.Add(new
            {
                offeringId          = off.Id,
                offeringName        = off.OfferingName,
                agentName           = off.AgentName,
                agentAddress        = off.AgentAddress,
                priceUsdc           = off.PriceUsdc,
                priceType           = off.PriceType,
                marketplaceVersion  = off.MarketplaceVersion ?? "v1"
            });
        }
        return new
        {
            requested      = offeringIds.Count,
            resolved       = lines.Count,
            totalUsdc      = Math.Round(total, 4),
            lines,
            missingIds     = missing,
            note           = "Per-call prices only; subscription tiers (when present) and gas costs are NOT included. Use AcpClient.setBudget(...) at hire-time for the wire-level escrow amount."
        };
    }

    // ── sellerCoachingPack ($1.00) ────────────────────────────────────────────
    //
    // Extended sellerDiagnose with per-offering scoring and prioritised
    // remediation suggestions. Identifies gaps a seller can fix to make their
    // offerings more discoverable / more frequently hired.
    public async Task<object> SellerCoachingPackAsync(string agent, CancellationToken ct)
    {
        var addr = agent.Trim().ToLowerInvariant();
        var offerings = await _offerings.ListByAgentAsync(addr);
        if (offerings.Count == 0)
        {
            return new
            {
                agentAddress = addr,
                verdict      = "NOT_INDEXED",
                priority     = new[]
                {
                    "Provision the agent on app.virtuals.io.",
                    "Run `npm run print-offerings` in the bot's acp-v2 dir and paste each block into the Offerings tab.",
                    "Run `npm run print-resources` and register Resources as a parallel pre-hire surface."
                }
            };
        }

        var resources = await _resources.ListByAgentAsync(addr, ct);
        var perOffering = new List<object>();
        var actions = new List<string>();
        int totalScore = 0;

        foreach (var o in offerings)
        {
            int score = 0;
            var notes = new List<string>();
            if (!string.IsNullOrEmpty(o.RequirementSchemaJson)) score += 25; else notes.Add("Missing requirementSchema — buyer agents can't pre-validate.");
            if (!string.IsNullOrEmpty(o.Description) && o.Description.Length >= 100) score += 25; else notes.Add("Description < 100 chars — too short for marketplace search to surface reliably.");
            if (o.OfferingName.Length <= 20) score += 15; else notes.Add($"Offering name `{o.OfferingName}` exceeds 20-char marketplace cap.");
            if (o.PriceUsdc >= 0.01) score += 10; else notes.Add($"Price ${o.PriceUsdc:F2} is below the marketplace minimum of $0.01.");
            if (o.UsageCount > 0) score += 25; else notes.Add("No paid hires yet — consider promoting through a Resource-tab teaser + lower introductory price.");

            totalScore += score;
            perOffering.Add(new
            {
                offeringName = o.OfferingName,
                healthScore  = score,
                hires        = o.UsageCount,
                notes
            });
        }
        var avgScore = offerings.Count == 0 ? 0 : totalScore / offerings.Count;

        if (resources.Count == 0)
            actions.Add("URGENT: Register at least a `capabilities` Resource so buyer agents can introspect you without paying. Resources are the primary V2 demand-side primitive.");
        if (offerings.Any(o => string.IsNullOrEmpty(o.RequirementSchemaJson)))
            actions.Add("Fill in requirementSchema for every offering — orchestrator agents (Butler etc.) filter out offerings without schemas.");
        if (offerings.Count(o => o.UsageCount == 0) > offerings.Count / 2)
            actions.Add("More than half of your offerings have zero hires. Consider: (a) repricing the cheapest as a $0.01 teaser, (b) registering Resources that map onto each offering, (c) adding semantic keywords (`hire me when X`) to descriptions.");
        if (offerings.All(o => (o.MarketplaceVersion ?? "v1") == "v1"))
            actions.Add("All offerings are V1. V2 is the new generation — see the `v1Tov2Migration` offering for an automated migration plan.");

        return new
        {
            agentAddress     = addr,
            agentName        = offerings[0].AgentName,
            overallVerdict   = avgScore >= 80 ? "STRONG" : (avgScore >= 50 ? "OK_WITH_GAPS" : "WEAK"),
            avgHealthScore   = avgScore,
            offeringCount    = offerings.Count,
            resourceCount    = resources.Count,
            totalHires       = offerings.Sum(o => o.UsageCount),
            perOffering,
            priority         = actions,
            cachedAt         = DateTime.UtcNow.ToString("O")
        };
    }

    // ── v1Tov2Migration ($0.50) ───────────────────────────────────────────────
    //
    // Given an agent address, return the V1 vs V2 split and a per-offering
    // migration plan with priority ordering (by hire count). Reads the corpus
    // only — no LLM call, deterministic output.
    public async Task<object> V1Tov2MigrationAsync(string agent)
    {
        var addr = agent.Trim().ToLowerInvariant();
        var offerings = await _offerings.ListByAgentAsync(addr);
        if (offerings.Count == 0)
            return new
            {
                agentAddress = addr,
                verdict      = "NOT_INDEXED",
                note         = "Agent has no indexed offerings on either V1 or V2. Provision on app.virtuals.io first."
            };

        var byMarket = offerings.GroupBy(o => o.MarketplaceVersion ?? "v1")
                                .ToDictionary(g => g.Key, g => g.ToList());
        var v1 = byMarket.TryGetValue("v1", out var v1List) ? v1List : new List<Offering>();
        var v2 = byMarket.TryGetValue("v2", out var v2List) ? v2List : new List<Offering>();

        string verdict;
        string topNote;
        if (v1.Count > 0 && v2.Count == 0)
        {
            verdict = "MIGRATE_RECOMMENDED";
            topNote = $"You have {v1.Count} V1 offering(s) and 0 on V2. Migrating unlocks Resources, subscription tiers, and the new ACP escrow flow.";
        }
        else if (v2.Count > 0 && v1.Count == 0)
        {
            verdict = "ALREADY_V2";
            topNote = "Agent is V2-only. No migration needed.";
        }
        else if (v1.Count > 0 && v2.Count > 0)
        {
            verdict = "PARTIAL_MIGRATION";
            topNote = $"Agent is on BOTH marketplaces ({v1.Count} V1 + {v2.Count} V2). Consider sunsetting the V1 entries — buyers default to V2 in TheMetaBot's search.";
        }
        else
        {
            verdict = "UNKNOWN";
            topNote = "Could not determine marketplace state from the index.";
        }

        // Migration steps ordered by hire count (touch the busiest offerings first)
        var steps = v1.OrderByDescending(o => o.UsageCount)
                      .Select((o, i) => new
                      {
                          step       = i + 1,
                          offeringName = o.OfferingName,
                          priorHires = o.UsageCount,
                          actions = new[]
                          {
                              "Confirm the offering's requirementSchema is JSON-Schema-valid (V2 enforces schema validation at the marketplace layer).",
                              "Add slaMinutes (min 5) — V2 mandates this since 2026-05-10.",
                              "Add deliverableSchema + deliverableExample if not present — V2 marketplace registration form requires both.",
                              "If this offering has a `watch` / `alert` / subscription flavor, prefer cloning ACP_BasicSubscriptionBot and registering as a V2 subscription tier rather than a one-shot."
                          }
                      }).ToArray();

        return new
        {
            agentAddress      = addr,
            agentName         = offerings[0].AgentName,
            verdict,
            topNote,
            v1OfferingCount   = v1.Count,
            v2OfferingCount   = v2.Count,
            v1TotalHires      = v1.Sum(o => o.UsageCount),
            v2TotalHires      = v2.Sum(o => o.UsageCount),
            migrationSteps    = steps,
            cachedAt          = DateTime.UtcNow.ToString("O")
        };
    }

    private static bool IsValidAddress(string a)
        => !string.IsNullOrWhiteSpace(a)
           && System.Text.RegularExpressions.Regex.IsMatch(a, "^0x[0-9a-f]{40}$");
}
