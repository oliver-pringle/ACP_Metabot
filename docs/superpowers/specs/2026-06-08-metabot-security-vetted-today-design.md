# Design: Security-vetted `acp_today` (Metabot-side free scanner)

**Date:** 2026-06-08
**Status:** Approved (brainstorm) — pending implementation plan.
**Owner:** Oliver / Metabot.

## Goal

Surface each marketplace agent's SecurityBot verdict (score + grade) inside the
`acp_today` marketplace digest, so "what's new / trending" comes with "how secure
is it". Achieved **for free** by having Metabot invoke SecurityBot's internal scan
endpoint over the `acp-shared` docker bridge — no ACP escrow, no buyer wallet, no
per-scan USDC.

## Key decision — why it's free

SecurityBot exposes `POST /v1/internal/scan` (X-API-Key gated; confirmed
`SecurityBot.Api/Program.cs:532`) and `securitybot-api` already joins `acp-shared`
(`docker-compose.yml:54`). Metabot is the hub on `acp-shared`. So Metabot calls the
internal scan endpoint directly with SecurityBot's `INTERNAL_API_KEY` — bypassing the
$1 paid `security_scan` ACP offering entirely. SecurityBot still passive-probes the
TARGET agent's public surface (free); only Metabot→SecurityBot rides `acp-shared`.

## Architecture

Scanning is fully decoupled from digest reads. A Metabot worker keeps a cached verdict
per agent; the gateway joins the cache into `/v1/digest`; `acp_today` reads it. A digest
request NEVER triggers a scan inline (stays a fast free GET).

```
SecurityScanWorker (C#, Metabot.Api)
   --(over acp-shared, X-API-Key)--> securitybot-api:5000/v1/internal/scan
   --> security_verdicts cache (Metabot SQLite)
/v1/digest handler  --join-->  security_verdicts  -->  acp_today (plugin) reads `security` field
```

### Components

1. **`security_verdicts` cache (Metabot SQLite, ADO.NET)**
   Columns: `agent_address` (PK, lowercased), `score` (int?), `grade` (text?),
   `status` (`scanned` | `not_auditable` | `error`), `observable_count` (int?),
   `finding_count` (int?), `severity_counts` (json text?), `scanned_at` (ISO text),
   `corpus_version` (text?), `last_error` (text?). WAL (portfolio convention).
   Repository: `SecurityVerdictRepository` (Upsert, GetByAgent, GetStaleAgents(ttl, limit),
   GetMany(addresses)).

1b. **`security_scan_history` store (Metabot SQLite, ADO.NET) — append-only**
   The `security_verdicts` table above is a *latest-verdict cache* (PK
   `agent_address`, upserted each scan — a new scan OVERWRITES the prior row). It
   drives the fast single-row-per-agent `/v1/digest` join and the stale/TTL
   selection. It deliberately keeps only the most recent result.

   To durably retain **the results of every scan on a bot** (not just the latest),
   the worker ALSO appends one immutable row per scan to `security_scan_history`.
   This mirrors Metabot's existing latest-cache + append-history split
   (`agent_reputation_cache` + `agent_reputation_history`, `risk_attest_pro_cache`
   + `risk_snapshot_history`) — Metabot persists history in its OWN database.

   **Why Metabot owns it (not read-through to SecurityBot):** SecurityBot already
   persists every scan to its own `scans` + `scan_findings` tables — but exposes
   NO read-by-agent endpoint (only a private `GetMostRecentByAgentAsync`), so that
   data is not queryable from outside SecurityBot. "The database" in the request =
   Metabot's DB, where the worker runs and owns the scan cadence. A read-through
   would couple Metabot to SecurityBot on a hot path and make it a critical
   dependency for Metabot's own audit features. (Decision confirmed 2026-06-08.)

   Columns: `id` (INTEGER PK AUTOINCREMENT — surrogate, truly append-only, NO
   natural-key uniqueness so re-scans append rather than overwrite),
   `agent_address` (text, lower-cased), `scanned_at` (ISO-8601 "O", normalized to
   UTC so the string `ORDER BY` is valid), `status` (`scanned` | `not_auditable` |
   `error`), `score` (int?), `grade` (text?), `observable_count` (int?),
   `finding_count` (int?), `severity_counts` (json text?), `verdict` (text? — the
   raw SecurityBot discriminator, e.g. `PASS`/`NOT_AUDITABLE`), `corpus_version`
   (text?), `findings_json` (text? — the FULL raw `findings[]` array verbatim),
   `last_error` (text? — server-side only). Indexed `(agent_address, scanned_at
   DESC)`. WAL.

   The summary columns (`score`/`grade`/`observable_count`/`finding_count`/
   `severity_counts`) are denormalized from `findings_json` so the deferred read
   surface can list scans without re-parsing the blob; they intentionally
   duplicate the cache's headline fields (same idiom as `risk_snapshot_history`).
   `corpus_version` is a forward-compat placeholder — the `/v1/internal/scan`
   response does not expose it today, so it is intentionally stored null (not a
   wiring bug); SecurityBot stamps it internally and may surface it later.

   **What each scan persists:** every `SecurityScanWorker` tick, for each agent,
   does TWO writes — (a) `UpsertAsync` the latest-verdict cache row (unchanged),
   then (b) `AppendAsync` one new history row carrying the full result incl. the
   raw `findings_json`. A `not_auditable` or `error` scan still appends a row (null
   score/findings + the reason) so the timeline is complete and gaps are explained.
   `pending` is a synthetic *digest-only* status for an unscanned agent and is
   NEVER written to history.

   **Write ordering / durability:** cache upsert first (so the digest stays
   correct), then the history append, on separate connections (no shared
   transaction — keeps the one-connection-per-repo-call idiom). A crash strictly
   between the two writes loses that one history row; the next scan re-captures the
   agent. This at-least-once behaviour is an accepted tradeoff for an audit log.

   **The digest reads the cache, never the history.** `/v1/digest` enrichment
   batch-joins `security_verdicts` only. The history's raw findings/evidence MUST
   NOT reach the public digest surface (P9/P10).

2. **`TheSecurityBotClient` (C#, cross-bot HTTP)**
   - BaseUrl `http://securitybot-api:5000` (env `THESECURITYBOT_BASE_URL`, default that).
   - `X-API-Key` from `THESECURITYBOT_API_KEY` (= SecurityBot's `INTERNAL_API_KEY`).
   - Registered via `AddHttpClient` with the portfolio hardening: `AllowAutoRedirect=false`
     + `ConnectCallback=InternalConnectCallbacks.PinResolvedIp` (P39) + bounded timeout
     (P57/P21; scans can take a few s — use ~30s).
   - P17: if `THESECURITYBOT_BASE_URL` set but key empty in non-Dev → fail-fast at boot,
     OR register a `NoopSecurityBotClient` (worker no-ops, verdicts stay `pending`) when
     unconfigured — graceful degrade.
   - `Task<ScanVerdict?> ScanAsync(string agentAddress, ct)` → `POST /v1/internal/scan`
     body `{ agentAddress }`; SecurityBot resolves the public URL itself. Maps the
     response `{ score, grade, observableCount, totalPatterns, findings[], summary }` to
     `ScanVerdict`; a `NOT_AUDITABLE` deliverable maps to `status=not_auditable`; a
     non-2xx / transport failure maps to `status=error` (never throws into the worker
     loop). Never logs the upstream body raw (P30/P63).

3. **`SecurityScanWorker` (BackgroundService, Metabot.Api)**
   - Tick interval: `SECURITY_SCAN_TICK_SECONDS` (default 60).
   - Each tick: `agents = SecurityVerdictRepository.GetStaleAgents(ttl=SECURITY_SCAN_TTL_DAYS, limit=SECURITY_SCAN_BATCH)` where "stale" = never scanned OR `scanned_at` older than the TTL for its status (see TTLs). Candidate universe = **all live marketplace agents** from Metabot's existing indexer/agent list (V1+V2). Priority order: portfolio + highest-traction first (sort by hire-count / recency), then the rest.
   - For each agent in the batch: `await client.ScanAsync(agent)`, upsert the verdict, then `await Task.Delay(SECURITY_SCAN_DELAY_SECONDS)` between agents.
   - **Rate (approved):** `SECURITY_SCAN_BATCH=10` agents per tick, `SECURITY_SCAN_DELAY_SECONDS=5` between scans. So ≤10 scans/min, deliberately gentle on SecurityBot + external targets.
   - Single-flight: the worker is single-instance (portfolio is single-replica); the batch query + per-tick processing is inherently serial, so no extra lock needed. Document the single-replica assumption.
   - CancellationToken threaded; `ThrowIfCancellationRequested` between agents.

4. **Gateway enrichment — `/v1/digest`**
   The digest handler, after building its agent/offering list, batch-loads
   `SecurityVerdictRepository.GetMany(distinct agent addresses)` and attaches to each
   agent/offering a `security` object:
   `{ score, grade, status, scannedAt }` — or `{ status: "pending" }` when no row exists.
   No raw findings/evidence on this public surface (P9/P10 — only score/grade/status).

5. **Plugin (`acp-find-mcp`) — `acp_today`**
   No handler logic change (the `security` field flows through `/v1/digest`
   automatically). Update the tool `description` to mention the per-agent `security`
   field. Optional `includeSecurity` boolean (default true) that the gateway honors to
   omit the join. **Local-only until the next plugin republish.**

## TTLs / freshness

- `scanned` verdict: re-scan after `SECURITY_SCAN_TTL_DAYS` (default **7**).
- `not_auditable`: long TTL `SECURITY_SCAN_NOTAUDITABLE_TTL_DAYS` (default 30) — don't
  keep re-probing agents with no HTTP surface.
- `error`: short TTL `SECURITY_SCAN_ERROR_TTL_HOURS` (default 6) — retry soon.
- Each verdict carries `scanned_at` + `corpus_version` so consumers can judge freshness;
  the digest surfaces `scannedAt`.
- `security_scan_history` is an immutable log — never re-scanned on, no TTL. The
  TTL/stale logic operates purely on the `security_verdicts` cache. **Growth note:**
  history is append-only with no pruning this iteration; the worst case is the
  6-hour `error` retry cadence (a persistently-failing agent ≈ 1.4k rows/yr) plus
  the size of `findings_json` for findings-heavy agents — bounded, but monitor the
  table size on the droplet. A `PruneOlderThanAsync` (mirror of the reputation
  history repo's) is the escape hatch if it ever warrants.

## Cost

**$0.** Internal `acp-shared` call (no escrow). Bounded by scan throughput
(≤10/min), not money. Full first-pass of N live agents ≈ `N / (10/min)` minutes of
worker time spread across ticks; steady-state re-scan load ≈ `N / 7 days`.

## Error handling

- Client failures (timeout, non-2xx, transport) → `status=error` + `last_error`
  (server-side only; never surfaced raw). Worker continues to the next agent.
- SecurityBot down / key unset → `NoopSecurityBotClient` or repeated `error` rows;
  digest shows `pending`/`error` — never breaks the digest.
- Gateway join failure → digest still returns (security omitted), logged.

## Ops / wiring (for deploy — not this session)

- Metabot `.env`: `THESECURITYBOT_API_KEY=<SecurityBot INTERNAL_API_KEY>` (cross-bot key
  sync convention); optional `SECURITY_SCAN_*` tunables.
- Both bots already on `acp-shared` (verified). No wallet/budget config.
- Register `SecurityVerdictRepository`, `TheSecurityBotClient`, `SecurityScanWorker` in
  Metabot `Program.cs`; add the table to `Db` schema init.

## Testing

- `SecurityVerdictRepository`: upsert/get/stale-selection (TTL per status), GetMany.
- `SecurityScanWorker`: batch=10 selection in priority order; 5s delay; status mapping
  (scanned/not_auditable/error); single-flight/serial; cancellation.
- `TheSecurityBotClient`: maps scan response → verdict; NOT_AUDITABLE → not_auditable;
  non-2xx → error (no throw); no raw-body leak; no-redirect handler wired.
- Gateway `/v1/digest`: join attaches `security`; `pending` when absent; no findings/evidence leaked.
- Plugin: `acp_today` passes the `security` field through unchanged (snapshot/contract test).

## Out of scope (YAGNI)

- Triggering the *paid* `security_scan` ACP offering (free internal path replaces it).
- Surfacing raw findings/evidence in the digest (score/grade/status only; P9/P10).
- A separate composite tool (enrichment is inline on the existing digest).
- Re-scanning on every digest request (decoupled cache only).
- Plugin republish / marketplace re-registration (separate, manual).
- Pruning/retention job for `security_scan_history` (append-only for now; prune
  method deferred until volume warrants).
- A read endpoint/plugin tool to query `security_scan_history` (the user asked only
  that results be SAVED; the read surface is specced + deferred — see below).

## Read surface (deferred — specced, not built)

Persistence (above) is in scope; the read surface is deferred. When built it
mirrors `agentReputationHistory` exactly:
- Gateway route `GET /v1/securityScanHistory?agent=0x..&limit=N` (limit clamped
  1–100, default 20), handler `HandleSecurityScanHistory(string agent, int? limit,
  SecurityScanHistoryRepository histRepo)` — lower-case the address BEFORE the
  `^0x[0-9a-f]{40}$` validation (the regex rejects uppercase), then
  `ListByAgentAsync(addr, limit)`, returning `{ agentAddress, count, history:
  [{ scannedAt, status, score, grade, verdict, findingCount, severityCounts }, …] }`.
  The public list returns SUMMARY fields only; **raw `findings_json` is never
  returned by the public endpoint — it stays server-side in this design** (P9/P10).
- Plugin tool `acp_agent_security_history` in `acp-find-mcp` (args: `agentAddress`
  required, `limit?` 1–100 default 20), mirroring `acp_agent_reputation_history`.
