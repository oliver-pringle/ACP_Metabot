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
