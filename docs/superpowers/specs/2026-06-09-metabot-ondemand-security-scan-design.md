# Design: On-demand `acp_security_scan` (operator-triggered scan of any marketplace bot)

**Date:** 2026-06-09
**Status:** Approved (brainstorm) — pending implementation plan.
**Owner:** Oliver / Metabot.
**Builds on:** `2026-06-08-metabot-security-vetted-today-design.md` (the background `SecurityScanWorker`, `security_verdicts` cache, `security_scan_history` append log, `TheSecurityBotClient`).

## Goal

Let the operator run SecurityBot's scan against **any particular bot on the marketplace, on demand**, from the `acp-find-mcp` plugin — instead of waiting for the background worker to reach it. The result is persisted to the **same** tables the worker writes (`security_verdicts` latest-cache + `security_scan_history` append log) and returned to the caller with full per-finding detail.

This jumps the worker's traction-ordered queue for a specific agent, and doubles as a diagnostic (it directly answers "can SecurityBot score *this* bot?" — relevant to the 67/67 `not_auditable` first-pass observed 2026-06-08).

## Key decisions (settled in brainstorm)

1. **Operator-only.** The trigger uses SecurityBot's FREE internal scan path (`/v1/internal/scan` over `acp-shared`, no escrow). A public trigger would (a) let anyone get unlimited free scans that undercut SecurityBot's paid `$1 security_scan` offering, and (b) let any user make SecurityBot probe arbitrary external hosts via our infra. So the Metabot endpoint is **`X-API-Key` gated** (Metabot's `INTERNAL_API_KEY`); non-operators get 401.
2. **Full verdict + findings returned.** Because it's operator-only, the response includes the per-finding detail (`patternId`, `title`, `severity`, `verdict`, `evidence`, `fixRef`) — actionable for fixing a bot. The **public digest stays summary-only** (score/grade/status); P9/P10 is unchanged for public surfaces.
3. **Same persist path.** On-demand scan and the background worker both go through one shared method that upserts the cache and appends history — there is exactly one way a verdict is written.
4. **Free internal path** (reuse `TheSecurityBotClient`). $0 per scan. Not the paid ACP-hire path (the plugin is not a buyer wallet).

## Architecture

```
acp_security_scan(agentAddress)            [plugin tool, sends X-API-Key]
  --> POST api.acp-metabot.dev/admin/securityScan   [Metabot, X-API-Key gated]
      --> SecurityScanService.ScanAndPersistAsync(agentAddress)
          --> TheSecurityBotClient.ScanAsync  --(acp-shared, free)--> securitybot-api /v1/internal/scan
          --> SecurityVerdictRepository.UpsertAsync        (latest cache)
          --> SecurityScanHistoryRepository.AppendAsync    (append log)
      <-- full verdict + findings
  <-- verdict + findings surfaced to the operator
/v1/digest later reflects the freshly-cached verdict (security: {score,grade,status,scannedAt})
```

### Components

1. **`SecurityScanService` (new — `ACP_Metabot.Api/Services/SecurityScanService.cs`) — refactor + reuse**
   Extract the per-agent "scan → upsert cache → append history" currently inlined in
   `SecurityScanWorker.TickOnceAsync` into a single reusable method:
   `Task<ScanResult> ScanAndPersistAsync(string agentAddress, SecurityVerdictRepository repo, SecurityScanHistoryRepository historyRepo, CancellationToken ct)`.
   - Calls `_client.ScanAsync(agentAddress, ct)` → `ScanResult` (verdict + raw findings JSON + raw verdict).
   - `await repo.UpsertAsync(result.Verdict, ct)` (cache first — keeps the digest correct).
   - `await historyRepo.AppendAsync(verdict.AgentAddress, verdict.ScannedAt, verdict.Status, verdict.Score, verdict.Grade, verdict.ObservableCount, verdict.FindingCount, verdict.SeverityCountsJson, result.RawVerdict, verdict.CorpusVersion, result.RawFindingsJson, verdict.LastError, ct)`.
   - Returns the `ScanResult` so the caller can surface full detail.
   - Registered DI singleton; depends on `ITheSecurityBotClient` only (repos passed in by the caller, which already resolves them from its scope — the worker from its per-tick scope, the endpoint from the request scope). This keeps the service free of scope concerns and matches the existing repo-as-singleton wiring.
   - `SecurityScanWorker.TickOnceAsync` is updated to call `service.ScanAndPersistAsync(stale[i], repo, historyRepo, ct)` per agent instead of the inlined three calls — behaviour identical, just DRY. (The worker's batch/delay/stale-selection logic is unchanged.)

2. **Operator-gated endpoint — `POST /admin/securityScan` (Metabot `Program.cs`)**
   - **Gating confirmed:** Metabot's inline X-API-Key middleware (`Program.cs`) enforces on every path except `/health` and `/v1/*` (and re-gates `/v1/internal/*`). `/admin/*` is therefore gated — precedent: the operator-only `/admin/pulse/tick-now`. So `POST /admin/securityScan` requires `X-API-Key == INTERNAL_API_KEY`, else 401. (Place it alongside the existing `/admin/pulse/tick-now` operator route.)
   - Body: `{ "agentAddress": "0x…" }`. Validate against `^0x[0-9a-fA-F]{40}$` (lower-case before validate); 400 `{error:"invalid_address"}` otherwise.
   - Resolves `SecurityScanService` + both repos from DI, calls `ScanAndPersistAsync`, and returns 200 with the full projection:
     `{ agentAddress, status, score, grade, observableCount, findingCount, severityCounts (object), verdict, scannedAt, findings: [ {patternId,title,severity,verdict,evidence,fixRef}… ] }`.
     `findings` is parsed from the persisted `RawFindingsJson` (null/empty → `[]`). `not_auditable`/`error` return 200 with that status and null score/grade/empty findings (honest outcome, already persisted) — never a 500.
   - Modest rate-limit (operator tool; e.g. reuse/add a low-rate policy) to bound accidental loops.
   - Synchronous: a scan takes a few seconds (SecurityBot probes the live target); the request awaits it. Acceptable for a low-volume operator tool.

3. **Plugin tool `acp_security_scan` (`acp-find-plugin/mcp-server/server.js`)**
   - Args: `{ agentAddress: string (required) }`. Normalize via the existing `normalizeAddress`; reject non-hex client-side.
   - **Auth confirmed:** reuses the plugin's existing `ACP_API_KEY` env + `SEND_API_KEY`/`callGateway` mechanism, which already attaches `X-API-Key: ${ACP_API_KEY}` when set (server.js:507/550/etc.). The operator sets `ACP_API_KEY` = Metabot's `INTERNAL_API_KEY`. No new env var.
   - Calls `POST {gateway}/admin/securityScan` with `{ agentAddress }` (via `callGateway`, which sends the key), returns the verdict+findings JSON (add `marketplaceUrl` for the agent, consistent with the other tools).
   - If `ACP_API_KEY` is unset the request goes unauthenticated → Metabot 401; the tool surfaces a clear "operator key (ACP_API_KEY) required" message rather than a bare 401 passthrough.
   - **Local-only until the next `acp-find-mcp` npm republish** (docs-lockstep: README What's-new lead block + tool description), same as the `acp_today` security-field bump.

## Data flow / freshness

- **Always scans fresh.** "Run the scan on this bot" means scan now; every call performs a live scan and updates both tables (cache upsert + a new history row). No cache-first short-circuit — routine freshness is the worker's job; this is the explicit-override path.
- Accepts **any** `agentAddress` whether or not it is in Metabot's offerings index (SecurityBot resolves the target's public surface itself). This is what makes "any particular bot on the marketplace" work.

## Error handling

- Invalid/missing `agentAddress` → 400 (endpoint) / client-side reject (plugin).
- Missing operator key → 401 (endpoint) / explicit "operator key required" (plugin).
- SecurityBot transport / non-2xx / parse failure → `TheSecurityBotClient` already maps to a `status=error` verdict (never throws); the service persists it and the endpoint returns 200 with `status:"error"` + `lastError` is **not** surfaced raw (server-side only, per P30/P63 — the endpoint omits `lastError` from its response or returns a generic note). `NOT_AUDITABLE` → `status:"not_auditable"` + empty findings.
- The endpoint never leaks SecurityBot's raw upstream error body; it only returns the structured verdict + the findings array SecurityBot deliberately includes.

## Security posture

- Operator-gated (X-API-Key) — the single control preventing free-scan abuse + paid-offering cannibalisation.
- Reuses the P39-hardened `TheSecurityBotClient` (no-redirect + connect-time IP pin) for the cross-bot call.
- Full findings/evidence are returned ONLY on this operator-gated endpoint; the public `/v1/digest` projection is unchanged (score/grade/status). `security_scan_history.findings_json` remains server-side except via this gated tool.

## Testing

- **`SecurityScanService`:** `ScanAndPersistAsync` upserts the cache row, appends exactly one history row, returns the `ScanResult`; a re-run appends a second history row while the cache stays one row (mirrors the worker's existing rescan test, now exercised through the shared service). Uses a fake `ITheSecurityBotClient`.
- **`SecurityScanWorker`:** existing 6 tests must still pass after the refactor to call the service (no behaviour change).
- **Endpoint:** 401 without the key; 400 on a bad address; 200 returns the full verdict+findings AND both tables get a row; `error`/`not_auditable` return 200 with the right status (not 500); `lastError` not present in the response body.
- **Plugin tool:** sends `X-API-Key` from the operator env; maps the response; returns a clear error when the key env is unset (contract/snapshot test consistent with the plugin's existing tool tests, if any).

## Ops / wiring (for deploy — not this session)

- No new env on Metabot: the endpoint reuses Metabot's existing `INTERNAL_API_KEY` (already set) for gating, and `THESECURITYBOT_API_KEY` (already wired in the 2026-06-08 deploy) for the cross-bot call.
- Plugin: set `ACP_API_KEY` = Metabot's `INTERNAL_API_KEY` in the operator's MCP client config (the var the plugin already uses for X-API-Key); republish `acp-find-mcp` to ship the new tool to the operator's environment.
- The `/admin/securityScan` route is reached via the existing Caddy apex catch-all → `acp-metabot-api`; no new Caddy block needed.

## Out of scope (YAGNI)

- Public or paid (ACP-hire) access — operator-only, free internal path only.
- `baseUrl` override (agentAddress only; SecurityBot resolves the surface).
- Scheduling / batch on-demand (single agent per call; the worker handles bulk).
- The deferred `security_scan_history` **reader** endpoint/tool (`§G` of the 2026-06-08 handoff) — this is the scan **trigger**, not the history reader; they remain separate.
- Cache-first / "return cached if fresh" behaviour — this path always scans fresh by design.
