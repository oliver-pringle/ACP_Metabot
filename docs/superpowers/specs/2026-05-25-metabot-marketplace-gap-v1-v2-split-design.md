# marketplaceGap V1/V2 split — design

**Status:** APPROVED — open questions resolved 2026-05-25 (see §6). Ready to plan + implement.
**Bumps:** Metabot v1.10.1 → adds `marketplace` arg to `marketplaceGap` AND flips the default to `"v2"`. acp-find-mcp v0.12.0 → v0.12.1 (additive). acp-find-plugin slash command parser update.
**Author:** Oliver / Claude session 2026-05-25
**Touches:** 1 paid offering, 1 free MCP tool, 1 slash command, ~5 C# files.

## 0. Behaviour change — read me first

**The default of `marketplaceGap` flips from "combined V1+V2 corpus" to "V2-only".** Every existing caller that does not pass `marketplace` explicitly will start receiving a V2-only ranking after deploy. This is a deliberate semantic shift (resolved Q2): V2 is where new bots actually deploy, so V2-only is the relevant denominator for the offering's primary use case ("where should I build a new ACP bot?").

Callers that want the pre-2026-05-25 behaviour pass `marketplace: "both"` explicitly.

**Near-duplicate edges remain cross-marketplace (resolved Q3).** A V1 offering near-duplicated to a V2 offering bumps both their saturation counts. This preserves the "both" numbers exactly as today — only the new "v1" and "v2" slices change anything.

## 1. Motivation

`acp_marketplace_gap` currently computes saturation/opportunity over the **combined V1+V2 corpus**. For someone deciding where to build a new ACP **v2** bot, this is the wrong denominator — V1 offerings (~17k as of 2026-05-25) drown the V2 density signal (~few hundred per category), and "saturated" V1 categories may have wide-open V2 windows.

User-asked Q on 2026-05-25: "show me the gap analysis for V2 only." The current schema doesn't allow that — it accepts only `category` and `limit`.

## 2. Goals

- Accept `marketplace: "v1" | "v2" | "both"` on the `marketplaceGap` paid offering, on its `/v1/marketplace/gap` HTTP endpoint, on the `acp_marketplace_gap` MCP tool, and on the `/acp-find:marketplace-gap` slash command.
- Compute saturation *within the chosen marketplace's pool*. Near-duplicates are only counted against same-marketplace peers.
- Preserve current behaviour when the field is omitted (`"both"` is the default — fully backwards-compatible).
- Echo the chosen marketplace value in the response so buyers can verify.

## 3. Non-goals

- Re-tuning the `recommendationTag` thresholds per marketplace. See §6 Q1 — punted to a follow-up after we have a few days of V2-only data.
- Changing the opportunity-score formula. `total * (1 - sat)²` stays.
- Cross-marketplace overlap analytics ("which V1 categories have no V2 coverage?"). Separate offering candidate; not in scope.
- Marketplace filter on `today` / `digest` / `composeStack` (those already take `marketplace` via the v1.3 unification work).

## 4. Design

### 4.1 Data model — `SaturationCalculator` becomes marketplace-aware

Today:

```csharp
public void Refresh(IEnumerable<(long id, string category, float[] embedding)> corpus)
```

Computes one rollup `List<CategorySaturation>` shared across callers.

After:

```csharp
public void Refresh(IEnumerable<(long id, string category, string marketplace, float[] embedding)> corpus)

public IReadOnlyList<CategorySaturation> PerCategory(string marketplace = "v2")
```

Internals (resolved Q3 — near-dup edges cross marketplace boundaries):

- `_byCategory` stays as today — bucketed by category only. The inner O(N²) cosine loop runs once over each category, **not** per-marketplace.
- `_nearDup` stays a flat `Dictionary<long, int>` — an offering's near-dup count is computed against ALL offerings in its category, V1 + V2 alike. This preserves today's `"both"` numbers exactly.
- A new auxiliary `_marketplaceById: Dictionary<long, string>` lets the rollup step pick which offerings belong in each marketplace slice without recomputing edges.
- Three rollups precomputed at `Refresh` time: `_rollupV1`, `_rollupV2`, `_rollupBoth`. Each rollup is `_byCategory` filtered to the chosen marketplace (or unfiltered for `"both"`), with `Total = filtered count` and `SaturatedCount = count of filtered where _nearDup[id] > 0`. The same `_nearDup` dict feeds all three — only the inclusion mask changes.
- `PerCategory(marketplace)` returns the precomputed rollup. Default `"v2"` (resolved Q2). `SearchService`'s per-hit enrichment at line 142 stays correct because it doesn't use `PerCategory()` — it calls `NearDuplicateCount(offeringId, category)`, which is unaffected.

**Cost:** inner cosine loop is the same size as today. Three rollups instead of one is a few extra dictionary scans (~O(N) total, dwarfed by the O(N²) cosine pass). Memory grows by one `_marketplaceById` dict + two extra rollup lists (~negligible).

**Backwards-compat for `SearchService` per-hit calls:** `NearDuplicateCount(offeringId, category)` still works unchanged — same flat `_nearDup` lookup.

### 4.2 Corpus tag plumbing — `SearchService.RefreshCorpusAsync`

Current line 142:

```csharp
_saturation.Refresh(tagged.Select(c => (c.Item1.Id, c.Item3 ?? string.Empty, c.Item2)));
```

After:

```csharp
_saturation.Refresh(tagged.Select(c => (
    c.Item1.Id,
    c.Item3 ?? string.Empty,
    string.IsNullOrEmpty(c.Item1.MarketplaceVersion) ? "v1" : c.Item1.MarketplaceVersion,
    c.Item2)));
```

`PricePercentileCalculator.Refresh` already takes the marketplace tag — same shape.

### 4.3 Service surface — `MarketplaceGapService.Analyze`

Current:

```csharp
public MarketplaceGapResponse Analyze(string? category, int limit)
```

After:

```csharp
public MarketplaceGapResponse Analyze(string? category, int limit, string marketplace = "v2")
```

`marketplace` is normalised (lower-case, trim) and validated against `{ "v1", "v2", "both" }`; invalid values return `400 invalid_marketplace` from the endpoint and a structured `validate()` failure in the sidecar offering.

`Analyze` calls `_saturation.PerCategory(marketplace)` instead of `PerCategory()`. The response gains a `marketplace` field (the resolved, canonicalised value):

```csharp
public record MarketplaceGapResponse(
    IReadOnlyList<MarketplaceGapRow> Opportunities,
    string? Filter,
    string Marketplace,        // NEW: "v1" | "v2" | "both"
    string? Note,
    DateTime ComputedAt);
```

(`Marketplace` is non-nullable. Default-omitted requests resolve to `"v2"` and the field echoes `"v2"` so buyers always know the denominator. Resolved Q4: when the requested slice is empty (e.g. `marketplace="v2"` but corpus has no V2 offerings yet), `Note` carries `"no <marketplace> offerings in current corpus snapshot"` — same shape as today's cold-boot note, parameterised per slice.)

### 4.4 HTTP endpoint — `POST /v1/marketplace/gap`

```csharp
public record MarketplaceGapRequest(string? Category, int? Limit, string? Marketplace);

app.MapPost("/v1/marketplace/gap",
    async (MarketplaceGapRequest req, MarketplaceGapService svc) =>
{
    var limit = req?.Limit ?? 5;
    var marketplace = (req?.Marketplace ?? "v2").Trim().ToLowerInvariant();
    if (marketplace is not ("v1" or "v2" or "both"))
        return Results.BadRequest(new { error = "invalid_marketplace",
            allowed = new[] { "v1", "v2", "both" } });
    return Results.Ok(svc.Analyze(req?.Category, limit, marketplace));
}).RequireRateLimiting("public-compose");
```

### 4.5 Paid offering — `acp-v2/src/offerings/marketplaceGap.ts`

Add `marketplace` to `requirementSchema.properties`:

```typescript
marketplace: {
  type: "string",
  enum: ["v1", "v2", "both"],
  description:
    "Optional. Which marketplace pool to compute saturation against. 'v1' = acpx.virtuals.io legacy pool. 'v2' (default) = api.acp.virtuals.io modern pool — the relevant denominator for new ACP-v2 bot decisions. 'both' = combined pool, matches the pre-2026-05-25 unfiltered response.",
},
```

`validate()` adds a string + enum check. `execute()` forwards `marketplace` to `client.marketplaceGap`.

Add to `deliverableSchema.required`: `"marketplace"`.

Add to `deliverableSchema.properties.marketplace`: the same enum.

Update `deliverableExample` to include `marketplace: "both"`.

### 4.6 acp-find-mcp wrapper — `acp_marketplace_gap` tool

`server.js`: add `marketplace` to the tool's input schema (same enum, same default-`"both"` semantics). Pass through to the HTTP call. No new field validation needed beyond enum matching — the upstream endpoint is the source of truth.

Bump `acp-find-mcp` to `v0.12.1`. Tools count stays 39 (no new tools, just one new field).

### 4.7 Slash command — `/acp-find:marketplace-gap`

`acp-find-plugin/commands/marketplace-gap.md` parser update. Today accepts `<category> [limit:N]`. Extend to accept BOTH a positional `v1`/`v2`/`both` keyword AND a `marketplace:<value>` named flag (resolved Q5 — support both forms). Parser scans positional first, then flags; if both appear, the flag wins (explicit > implicit).

- `/acp-find:marketplace-gap` → `{}` (defaults to `v2` per Q2; renders sub-headline `Marketplace: v2 (V2-only saturation — new default)`)
- `/acp-find:marketplace-gap v2` → `{ marketplace: "v2" }`
- `/acp-find:marketplace-gap both` → `{ marketplace: "both" }` (pre-2026-05-25 default reachable via explicit keyword)
- `/acp-find:marketplace-gap marketplace:v1` → `{ marketplace: "v1" }`
- `/acp-find:marketplace-gap "Trading Bots" v2 limit:10` → `{ category: "Trading Bots", marketplace: "v2", limit: 10 }`
- `/acp-find:marketplace-gap "Trading Bots" both marketplace:v2 limit:10` → `{ category: "Trading Bots", marketplace: "v2", limit: 10 }` (flag overrides keyword)

Parser disambiguation: the keyword tokens `v1`/`v2`/`both` are reserved — they never get parsed as part of a category name. (Categories never contain those tokens; canonical names like "Trading Bots" / "Wallet Intelligence" are multi-word and start with capital letters.)

Render adds a sub-headline line under the table: `Marketplace: v2 (V2-only saturation)` or `Marketplace: both (combined V1+V2 saturation)`. Default ("v2") sub-headline appends ` — new default 2026-05-25` for the first month post-deploy so existing users notice the BC shift.

### 4.8 Tests

Net-new:

- `SaturationCalculatorTests.MarketplaceFilter_*` — fixture with deliberately overlapping V1 + V2 corpus; assert each rollup's near-dup counts match within-marketplace truth and that `"both"` ≥ each individual.
- `MarketplaceGapServiceTests.Marketplace_PassesThrough` — happy path for `"v1"`, `"v2"`, `"both"`.
- `MarketplaceGapServiceTests.Marketplace_InvalidValue` — invalid value path (service-level — endpoint guard is tested separately at integration level).

Existing test files to touch: any that asserts `MarketplaceGapResponse` doesn't include `marketplace` field. Grep `MarketplaceGapResponse` in `*Tests.cs`. (Per CLAUDE.md: this batch ships with tests, unlike v1.7-1.9 batches.)

### 4.9 Docs in lockstep (per `feedback_acp_docs_in_lockstep`)

Every commit MUST touch:

1. `ACP_Metabot/ACP_Metabot/README.md` — marketplaceGap section adds `marketplace` arg.
2. `ACP_Metabot/ACP_Metabot/docs/user-guide.md` — same.
3. `ACP_Metabot/ACP_Metabot/docs/technical-specifications.md` — endpoint signature.
4. `acp-find-plugin/mcp-server/README.md` — new release "What's new in v0.12.1" lead block at TOP of file (per `feedback_acp_find_readme_whats_new_lead.md` — twice-bitten rule).
5. `acp-find-plugin/CHANGELOG.md` — v0.12.1 entry.
6. `acp-find-plugin/commands/marketplace-gap.md` — argument parser doc.
7. `acp-find-plugin/README.md` — slash-command surface table (if it lists args).
8. `C:\code_crypto\acp\CLAUDE.md` — Metabot row update reflecting the new marketplaceGap arg.

## 5. Rollout

1. Code + tests on `main`.
2. Deploy Metabot (`acp-metabot-api` + `acp-metabot-acp` rebuild — both touch since the sidecar's `marketplaceGap.ts` schema changes).
3. Smoke: call `/v1/marketplace/gap` with each of `{omitted, "v1", "v2", "both"}`. Assert response `marketplace` field matches input (or `"both"` when omitted).
4. Publish `acp-find-mcp@0.12.1` to npm (real-PowerShell ritual per `feedback_acp_find_plugin_v0_8_shipped`).
5. Re-register the updated `marketplaceGap` offering on app.virtuals.io if the marketplace UI doesn't auto-sync schema changes (it doesn't — Oliver registers manually).
6. Marketplace listing: 19 paid offerings, no count change. CLAUDE.md count remains correct.

## 6. Open questions — RESOLVED 2026-05-25

**[Q-1] Threshold tuning when V2-only is selected → SHIP AS-IS.** V2-only mode will be dominated by `niche_underserved` + `balanced`; that's fine for v1.10.1. Tune later if a real buyer complains. No threshold-profile changes; same `Tag()` switch as today.

**[Q-2] Default value → `"v2"`.** Behaviour change documented in §0 and called out in CHANGELOG. The marketplace 90% of new-bot decisions target IS V2; making callers pass `marketplace: "both"` explicitly to get the old default is the cleanest way to signal "you're looking at the combined pool on purpose."

**[Q-3] `"both"` near-dup symmetry → CROSS-MARKETPLACE.** A V1 offering near-duped to a V2 offering bumps both their saturation counts. This preserves today's `"both"` numbers exactly; only the new `"v1"` and `"v2"` slices are new signal. Implementation simplification (see §4.1) — `_nearDup` stays one flat dict.

**[Q-4] Empty-slice handling → PARAMETERISED NOTE.** When the requested slice has zero offerings (e.g. cold-boot V2 corpus), `Note` carries `"no <marketplace> offerings in current corpus snapshot"`. Same shape as today's cold-boot note.

**[Q-5] Slash-command syntax → BOTH FORMS.** Positional keyword (`v1`/`v2`/`both`) AND named flag (`marketplace:<value>`). Flag wins on conflict.

## 7. Estimated effort

- C# tier (SaturationCalculator + service + endpoint + tests): ~3-4 hours.
- TS sidecar (offering schema + sidecar test if any): ~1 hour.
- MCP wrapper + plugin slash command: ~1 hour.
- Docs in lockstep across 8 files: ~1 hour.
- Smoke + deploy + marketplace re-register: ~30 min.

**Total: ~half a day** of focused work once the 5 open questions are resolved.

## 8. References

- Current `MarketplaceGapService`: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/MarketplaceGapService.cs`
- Current `SaturationCalculator`: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/SaturationCalculator.cs:1-73`
- Corpus refresh: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/SearchService.cs:132-159`
- Endpoint: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Program.cs:1151-1162`
- v1.9 shipping memory: `memory/project_acp_metabot_v1_8_shipped.md` (sibling) — establishes the pattern.
- Docs-lockstep rule: `memory/feedback_acp_docs_in_lockstep.md`
- README-lead rule: `memory/feedback_acp_find_readme_whats_new_lead.md`
