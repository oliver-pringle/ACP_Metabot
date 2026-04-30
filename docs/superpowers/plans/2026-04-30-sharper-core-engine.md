# Sharper Core Engine — implementation plan

**Date:** 2026-04-30
**Status:** Implemented
**Spec:** [`../specs/2026-04-30-sharper-core-engine-design.md`](../specs/2026-04-30-sharper-core-engine-design.md)
**Source plan:** `~/.claude/plans/ultrathink-on-ways-themetabot-humble-fox.md`

## Phase 0 — Pre-flight

- `Db.InitializeSchemaAsync` runs a `pragma_compile_options()` probe; logs a
  warning to stderr if `ENABLE_FTS5` is missing. Microsoft.Data.Sqlite 9.0.*
  bundles FTS5 by default, so this is belt-and-braces.
- `ACP_Metabot.Api.Tests/Fixtures/search-eval-queries.json` stub seeded with
  5 placeholder queries spanning rare-keyword (hex), niche-ticker, multi-token
  brand+capability, common semantic, and self-reference patterns. Test csproj
  copies fixtures to output dir via `<None Include="Fixtures/*.json" />`.

## Phase 1 — Hybrid BM25 + dense fusion

Files touched:

- `ACP_Metabot.Api/Data/Db.cs` — `offerings_fts` virtual table + sync triggers
  (`offerings_ai`, `offerings_ad`, `offerings_au`) inside the existing
  `cmd.CommandText`; idempotent backfill block after `ExecuteNonQueryAsync`.
- `ACP_Metabot.Api/Data/OfferingRepository.cs` — `SearchBm25Async(query, limit)`
  + private `SanitizeFtsQuery` helper. Strips FTS5 operators, wraps multi-token
  queries in `"..."` for paste-safety with hex addresses.
- `ACP_Metabot.Api/Services/SearchService.cs` — `DensePoolSize`/`LexicalPoolSize`
  /`RrfK` constants; `ReciprocalRankFusion` static helper (internal, exposed
  to tests via `InternalsVisibleTo`); rewrites the cosine-only ordering loop
  to fuse with BM25 results and degrade cleanly on FTS exception (dense-only
  fallback).
- `ACP_Metabot.Api/ACP_Metabot.Api.csproj` — `<InternalsVisibleTo Include="ACP_Metabot.Api.Tests" />`.

Test coverage:

- `SearchFusionEvaluationTests.Fixture_Loads_AndHasMinimumQueries` — runs
  every CI build.
- `Rrf_TopOfBothRankers_Wins`, `Rrf_MissingFromOneRanker_Penalised`,
  `Rrf_KConstant_FlattensTail` — RRF unit tests, no DI required.
- `HybridBeatsCosineOn30Q` — skip-by-default; pending real fixture data.

## Phase 2 — Fielded filters

Files touched:

- `ACP_Metabot.Api/Program.cs` — `SearchRequest` record adds
  `[property: JsonPropertyName("chain")] string[]? Chains`, `int? MinReputation`,
  `int? Freshness`; `HandleSearch` validates and forwards.
- `ACP_Metabot.Api/Data/AgentReputationCacheRepository.cs` —
  `ListAllAgentScoresAsync(nowUtc)` returning a `(addr → agent_score)` snapshot
  for fresh (≤24h) cache rows.
- `ACP_Metabot.Api/Services/SearchService.cs` — DI gains
  `AgentReputationCacheRepository`; `_agentScoreLookup` snapshotted alongside
  the corpus in `RefreshCorpusAsync`; `SearchAsync` signature gains
  `HashSet<string>? chainFilter, int? minReputation`; filter logic added in
  the candidate-scoring loop. `freshness` flows through the existing
  `staleAfterDays` machinery from the handler.
- `ACP_Metabot.Api/Services/StackComposerService.cs` and `WatchService.cs`
  updated to pass the new params as `null`.
- `acp-v2/src/offerings/search.ts` — schema additions for `chain`,
  `minReputation`, `freshness`; matching `validate()` and `execute()` updates.
- `acp-v2/src/apiClient.ts` — `search` request type extended with the three
  new fields.
- `acp-find-plugin/mcp-server/server.js` — `acp_find` `inputSchema` extended;
  dispatcher forwards the new fields. `SERVER_VERSION` bumped to `0.3.0`.
- `acp-find-plugin/mcp-server/package.json` — `version` bumped to `0.3.0`,
  description updated to mention the new tool.

## Phase 3 — Reputation time-series + trajectory

Files touched:

- `ACP_Metabot.Api/Data/Db.cs` — `agent_reputation_history` table +
  `ix_rep_history_agent_date` index in the same `cmd.CommandText` block.
- `ACP_Metabot.Api/Data/AgentReputationHistoryRepository.cs` (NEW) —
  `UpsertAsync`, `GetTrajectoryAsync`, `PruneOlderThanAsync`.
- `ACP_Metabot.Api/Models/CachedReputation.cs` — `AgentReputationResultV2`
  gains `Trajectory?` field with `JsonIgnore(WhenWritingNull)`. New
  `HistoryPoint(date, agentScore, subScores?)` record.
- `ACP_Metabot.Api/Services/ReputationService.cs` — DI gains
  `AgentReputationHistoryRepository`; `ComputeAsync` writes a daily history row
  after the cache upsert; new `DeserializeWithTrajectoryAsync` and
  `GetCachedAsync` helpers attach the 30-day trajectory at the result-build
  boundary so cache hits and fresh computes both get it. `TrajectoryDays = 30`
  constant.
- `ACP_Metabot.Api/Services/ReputationWarmerService.cs` — DI gains
  `AgentReputationHistoryRepository`; post-pass DELETE prunes rows older than
  90 days (`HistoryRetentionDays`).
- `ACP_Metabot.Api/Program.cs` — `AgentReputationHistoryRepository` DI
  registered as singleton. `/v1/agentReputation` GET handler refactored to
  use `ReputationService.GetCachedAsync` so trajectory is attached
  automatically (also gives correct sub-score percentiles, fixing a
  pre-existing latent bug where percentiles were stale-from-JSON).
  New `app.MapGet("/v1/agentReputationHistory", ...)` reusing the
  `public-reputation` rate-limit policy + an internal mirror at
  `/agentReputationHistory`.
- `acp-v2/src/offerings/agentReputation.ts` — `deliverableSchema.properties`
  gains `trajectory` array; description updated.
- `acp-v2/src/apiClient.ts` — `ReputationHistoryPoint` interface;
  `AgentReputationResponse.trajectory?: ReputationHistoryPoint[]`.
- `acp-find-plugin/mcp-server/server.js` — new `acp_agent_reputation_history`
  tool + dispatcher; `acp_agent_reputation` description bumped to mention
  trajectory.

## Doc lockstep

- `ACP_Metabot/README.md` — offerings table updated; new endpoint mentioned;
  spec link added.
- `acp-find-plugin/README.md` — tool table now lists 8 tools (was 7); slash
  commands list updated; `/acp-find:reputation-history` mentioned.
- `acp-find-plugin/mcp-server/README.md` — npm-published; new filter args
  + new tool documented. (Republishing to npm requires Oliver running
  `npm publish` himself due to WebAuthn — see CLAUDE.md.)
- `acp-find-plugin/skills/acp-find/SKILL.md` — `acp_find` filter usage
  guidance; new `acp_agent_reputation_history` tool documented.
- `acp-find-plugin/commands/search.md` — filter usage hints.
- `acp-find-plugin/commands/reputation.md` — trajectory rendering instructions.
- `acp-find-plugin/commands/reputation-history.md` (NEW).
- `ACP_DeFiEval/README.md` — short note about the additive `trajectory` field
  in the embedded rep snapshot (no DeFiEval code change required).

## Verification commands

```bash
# C# tier
cd ACP_Metabot.Api && dotnet build           # zero warnings (vs. pre-existing Newtonsoft.Json transitive)
dotnet test                                  # 13 pass, 1 skipped (HybridBeatsCosineOn30Q)

# Sidecar
cd ../acp-v2 && npm run build                # tsc --noEmit clean
npm run print-offerings                      # search shows 3 new fields; agentReputation shows trajectory

# Local end-to-end (manual)
cd ../ACP_Metabot.Api && dotnet run
# Rare-keyword smoke:
curl -s -H "X-API-Key: $INTERNAL_API_KEY" -X POST http://localhost:5000/search \
  -H "Content-Type: application/json" \
  -d '{"query":"0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b","limit":3}' | jq

# Fielded filters:
curl -s -H "X-API-Key: $INTERNAL_API_KEY" -X POST http://localhost:5000/search \
  -H "Content-Type: application/json" \
  -d '{"query":"swap","chain":["base"],"minReputation":40,"freshness":30,"limit":5}' | jq

# Trajectory endpoint (will return empty history until the warmer or a paid hire seeds it):
curl -s "http://localhost:5000/v1/agentReputationHistory?agent=0xfc9f...&days=30" | jq

# npm republish — Oliver only, requires WebAuthn in his TTY:
cd ../../acp-find-plugin/mcp-server && npm publish
```

## Deferred to v1.3+

- B7 event-driven indexer freshness (off-chain feed already at 10-min cadence).
- Held-out eval fixture: 25 more queries to reach the planned 30; `expectedAgentAddress`
  / `expectedOfferingName` to fill in.
- Per-offering behavioural breakdown (rep v2 v1.0 deferred).
- HMAC attestations (rep v2 v1.2 deferred).
