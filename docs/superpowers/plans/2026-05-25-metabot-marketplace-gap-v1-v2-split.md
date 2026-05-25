# Plan — marketplaceGap V1/V2 split

**Spec:** `docs/superpowers/specs/2026-05-25-metabot-marketplace-gap-v1-v2-split-design.md`
**Version:** Metabot v1.10.1 OR v1.11 (Oliver's call at commit time; spec currently says v1.10.1).
**acp-find-mcp:** v0.12.0 → v0.12.1 (additive field; default-shift mirrored).

## Implementation order

C# foundation first (lowest risk, gates everything else), TS sidecar second (depends on C# shape), MCP + slash command third (depends on sidecar shape), docs in lockstep at the end before ship.

### Phase A — C# tier (~3-4 h)

A1. `SaturationCalculator.Refresh` — extend signature to `(id, category, marketplace, embedding)`. Maintain `_marketplaceById`. Compute `_rollupV1` / `_rollupV2` / `_rollupBoth` from the same flat `_nearDup` dict.
A2. `SaturationCalculator.PerCategory(marketplace = "v2")` — return precomputed rollup. Default `"v2"` per Q2.
A3. `SearchService.RefreshCorpusAsync` line 142 — pass `MarketplaceVersion ?? "v1"` through to `_saturation.Refresh`.
A4. `MarketplaceGapService.Analyze(category, limit, marketplace = "v2")` — accept the new param, pass to `PerCategory`. Add empty-slice note logic per Q4.
A5. `MarketplaceGapResponse` record — add `string Marketplace` field (non-nullable; echoes the resolved value).
A6. `MarketplaceGapRequest` record — add `string? Marketplace`.
A7. `Program.cs:1151-1162` — endpoint resolves null → `"v2"`, validates enum, returns 400 on invalid.
A8. Tests:
    - `SaturationCalculatorTests.MarketplaceFilter_*` — fixture with V1+V2 overlap; assert per-slice counts.
    - `MarketplaceGapServiceTests.Default_IsV2`.
    - `MarketplaceGapServiceTests.Marketplace_PassesThrough` — `v1`/`v2`/`both` happy path.
    - `MarketplaceGapServiceTests.EmptySlice_PopulatesNote` — Q4 assertion.
    - Update any existing test asserting `MarketplaceGapResponse` field set.
A9. `dotnet build` clean, `dotnet test` 100% pass.

### Phase B — TS sidecar (~1 h)

B1. `acp-v2/src/offerings/marketplaceGap.ts` — add `marketplace` to `requirementSchema.properties` + enum + default-`"v2"` description. Add to `validate()`. Forward in `execute()`. Add to `deliverableSchema.required` + `properties`. Update `deliverableExample.marketplace = "v2"`.
B2. `acp-v2/src/apiClient.ts` — `marketplaceGap(...)` forwards `marketplace` to `/v1/marketplace/gap`.
B3. `npm run build` clean. `npm run print-offerings` — verify schema renders, name still ≤20 chars (unchanged at 14), description still ≤500 chars.

### Phase C — MCP wrapper + slash command (~1 h)

C1. `acp-find-plugin/mcp-server/server.js` — add `marketplace` to `acp_marketplace_gap` input schema (enum, optional). Forward to gateway. Bump `package.json` to `0.12.1`.
C2. `acp-find-plugin/commands/marketplace-gap.md` — parser update per §4.7 of spec (positional keyword + named flag, flag wins). Update render to show sub-headline + 2026-05-25 BC banner for first month.
C3. `acp-find-plugin/plugin.json` — version bump to match.
C4. `acp-find-plugin/CHANGELOG.md` — new entry calling out the default-shift loudly.

### Phase D — docs lockstep (~1 h, per `feedback_acp_docs_in_lockstep`)

D1. `ACP_Metabot/ACP_Metabot/README.md` — marketplaceGap section: new arg + default-shift callout.
D2. `ACP_Metabot/ACP_Metabot/docs/user-guide.md` — same.
D3. `ACP_Metabot/ACP_Metabot/docs/technical-specifications.md` — endpoint signature.
D4. `acp-find-plugin/mcp-server/README.md` — new `## What's new in v0.12.1` lead block at TOP, ABOVE the v0.12.0 block (per `feedback_acp_find_readme_whats_new_lead.md` — twice-bitten hard rule). Pre-publish `head -30 README.md` check.
D5. `acp-find-plugin/README.md` — slash-command surface table refresh.
D6. `C:\code_crypto\acp\CLAUDE.md` — Metabot row update: marketplaceGap default-V2 + new arg.
D7. `acp-find-plugin/CHANGELOG.md` — included in C4.

### Phase E — ship (handed back to Oliver)

E1. `git diff` review.
E2. `git commit` with descriptive message naming the BC default-shift.
E3. `git push` (Oliver-authorised).
E4. SSH droplet, `docker compose stop acp-metabot-api acp-metabot-acp`, `docker compose up -d --build acp-metabot-api acp-metabot-acp`. Sequential per portfolio convention.
E5. Smoke per spec §5.3:
    - `curl -X POST .../v1/marketplace/gap -d '{}'` → assert `marketplace: "v2"` in response.
    - `... -d '{"marketplace":"v1"}'` → assert `marketplace: "v1"`.
    - `... -d '{"marketplace":"both"}'` → assert `marketplace: "both"` AND row count + first row matches today's pre-deploy snapshot (no regression on `"both"` numbers — Q3 guarantee).
    - `... -d '{"marketplace":"invalid"}'` → assert 400.
E6. npm publish acp-find-mcp@0.12.1 — Oliver from real Windows PowerShell, NOT from Claude Code (per WebAuthn-only quirk in memory). Pre-publish: `head -30 mcp-server/README.md` to confirm v0.12.1 lead block.
E7. Re-register `marketplaceGap` on app.virtuals.io (Oliver, manual — sidecar schema changed).
E8. Verify CLAUDE.md offering counts unchanged (19 paid Metabot offerings — `marketplaceGap` was already counted).

## Rollback

If V2-only default surprises a live buyer:
- Hotfix: change one line in `MarketplaceGapService.cs` default param `"v2"` → `"both"` + one line in `Program.cs` `?? "v2"` → `?? "both"` + one line in offering schema description. ~5-min fix, fully reverting the BC shift while keeping the new field functional.

## Files modified (final list)

C# (8):
- `ACP_Metabot.Api/Services/SaturationCalculator.cs`
- `ACP_Metabot.Api/Services/MarketplaceGapService.cs`
- `ACP_Metabot.Api/Services/SearchService.cs`
- `ACP_Metabot.Api/Program.cs`
- `ACP_Metabot.Api.Tests/SaturationCalculatorTests.cs` (new or extend)
- `ACP_Metabot.Api.Tests/MarketplaceGapServiceTests.cs` (new)

TS sidecar (2):
- `acp-v2/src/offerings/marketplaceGap.ts`
- `acp-v2/src/apiClient.ts`

MCP wrapper + plugin (4):
- `acp-find-plugin/mcp-server/server.js`
- `acp-find-plugin/mcp-server/package.json`
- `acp-find-plugin/plugin.json`
- `acp-find-plugin/commands/marketplace-gap.md`

Docs (6):
- `ACP_Metabot/README.md`
- `ACP_Metabot/docs/user-guide.md`
- `ACP_Metabot/docs/technical-specifications.md`
- `acp-find-plugin/mcp-server/README.md`
- `acp-find-plugin/README.md`
- `acp-find-plugin/CHANGELOG.md`
- `C:\code_crypto\acp\CLAUDE.md`

**Total: ~20 files touched.**
