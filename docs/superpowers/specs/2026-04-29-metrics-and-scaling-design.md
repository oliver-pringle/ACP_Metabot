# TheMetaBot Operator Metrics + Scaling Runbook

**Date:** 2026-04-29
**Status:** Approved (design), not yet implemented
**Owner:** Oliver Pringle
**Bot:** ACP_Metabot

## Summary

Add an operator-only telemetry surface to TheMetaBot so we can answer, on demand:

- How many requests per day, split by traffic source (`acp-find-mcp` plugin via `User-Agent: acp-find-plugin/<ver>` vs other public `/v1/*` callers vs internal X-API-Key callers).
- Top search queries (`acp_find`) and top agents looked up (`acp_browse_agent`, `acp_agent_reputation`).
- Per-endpoint error rates and Voyage / Claude failure rates.

Five `GET /metrics/*` endpoints, gated by the existing `INTERNAL_API_KEY` middleware. Backed by a new `request_log` table (raw, 14-day retention) plus hourly + daily rollups.

A separate scaling **runbook** documents seven levers with metric-keyed signals, ordered by likely trigger order. No scaling code ships in this work — only the metrics surface plus the runbook (`docs/runbook-scaling.md`).

## Why this change

The bot has been live since 2026-04-26 with `acp-find-mcp` published to npm and `acp-find-plugin` shipping to Claude Code. Today, the only telemetry is Caddy access logs (rolling 100 MB) and ASP.NET default `ILogger` output — both lost on container restart. We don't know how much traffic the plugin is actually driving, what users are searching for, or where the latency / failure modes are. The gap is most acute for the plugin question, since the plugin is now the primary discovery channel and we have no visibility into adoption.

Scaling work is deferred until a metric signals real pain. The runbook captures the levers in advance so future-Oliver doesn't have to redo the analysis under stress.

## Scope and non-goals

**In scope (v1):**
- New SQLite tables: `request_log`, `request_rollup_hourly`, `request_rollup_daily`.
- ASP.NET middleware that records every request (including 401s and 429s) into a bounded channel; background writer drains the channel and batches inserts every 250 ms.
- Source classification: `mcp_plugin`, `public_other`, `internal` — with optional `caller_id` from an `X-Caller` header for forward-compatibility.
- Body capture (`query` text, `agentAddress`) for `/search`, `/composeStack`, `/agentReputation`, `/agent/{address}` and their `/v1/` counterparts. 2 KB cap.
- Provider error tagging: typed `VoyageApiException` / `ClaudeApiException` carry status code; middleware records them as `provider_error = "voyage_429"` etc.
- Hourly + daily rollups via a background service that runs at xx:05 every hour, plus a daily prune at 03:00 UTC (offset from the existing 02:00 lifetime snapshot).
- Five operator endpoints: `/metrics/summary`, `/metrics/timeseries`, `/metrics/endpoints`, `/metrics/top`, `/metrics/errors`.
- Sidecar adds `X-Caller: sidecar` to its outbound calls into the C# tier.
- Documentation: this spec, `docs/runbook-scaling.md`, README pointer.

**Out of scope:**
- No Prometheus/Grafana/Datadog integration. Operator-curl JSON only.
- No HTML dashboard.
- No alerting on threshold crossing.
- No PII redaction. Raw queries / wallet addresses stored as-is per requirements; Oliver controls the DB.
- No scaling code shipped. All seven levers documented for future activation only.

## Architecture

```
Caddy ──► acp-metabot-api  (no published port; private bridge)
           │
           ├── UseForwardedHeaders          (existing)
           ├── UseRateLimiter               (existing — must precede metrics so 429s recorded)
           ├── RequestMetricsMiddleware     NEW — fire-and-forget Channel<T> write
           ├── X-API-Key middleware         (existing — gates /metrics/* automatically)
           ├── /search /composeStack ...    (existing handlers)
           └── /metrics/{summary|timeseries|endpoints|top|errors}   NEW

                       ┌──────────────────────────────┐
                       │  MetricsChannel (bounded     │
                       │  Channel<RequestMetricEvent>,│
                       │  4096 cap, DropOldest)       │
                       └──────────────────────────────┘
                                       │
                       ┌───────────────▼──────────────────┐
                       │  MetricsWriterService             │
                       │  (BackgroundService)              │
                       │   ─ drain task: 250 ms / 100 row  │
                       │     batched INSERTs               │
                       │   ─ rollover task: xx:05 hourly,  │
                       │     03:00 daily prune             │
                       └───────────────┬──────────────────┘
                                       │
                       ┌───────────────▼──────────────────┐
                       │  SQLite (acp_metabot.db)         │
                       │   request_log                    │
                       │   request_rollup_hourly          │
                       │   request_rollup_daily           │
                       └──────────────────────────────────┘
```

## Schema

Append to `Data\Db.cs#InitializeSchemaAsync` DDL block:

```sql
CREATE TABLE IF NOT EXISTS request_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ts              TEXT    NOT NULL,
    endpoint        TEXT    NOT NULL,
    method          TEXT    NOT NULL,
    status_code     INTEGER NOT NULL,
    duration_ms     INTEGER NOT NULL,
    source          TEXT    NOT NULL,
    user_agent      TEXT,
    caller_id       TEXT,
    remote_ip       TEXT,
    query_text      TEXT,
    agent_address   TEXT,
    provider_error  TEXT
);

CREATE INDEX IF NOT EXISTS ix_request_log_ts          ON request_log(ts);
CREATE INDEX IF NOT EXISTS ix_request_log_endpoint_ts ON request_log(endpoint, ts);
CREATE INDEX IF NOT EXISTS ix_request_log_source_ts   ON request_log(source, ts);

CREATE TABLE IF NOT EXISTS request_rollup_hourly (
    bucket_hour     TEXT    NOT NULL,            -- 'YYYY-MM-DD HH'
    endpoint        TEXT    NOT NULL,
    source          TEXT    NOT NULL,
    status_class    TEXT    NOT NULL,            -- '2xx','3xx','4xx','5xx','429'
    count           INTEGER NOT NULL,
    sum_duration_ms INTEGER NOT NULL,
    voyage_errors   INTEGER NOT NULL DEFAULT 0,
    claude_errors   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (bucket_hour, endpoint, source, status_class)
);

CREATE TABLE IF NOT EXISTS request_rollup_daily (
    bucket_date     TEXT    NOT NULL,
    endpoint        TEXT    NOT NULL,
    source          TEXT    NOT NULL,
    status_class    TEXT    NOT NULL,
    count           INTEGER NOT NULL,
    sum_duration_ms INTEGER NOT NULL,
    voyage_errors   INTEGER NOT NULL DEFAULT 0,
    claude_errors   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (bucket_date, endpoint, source, status_class)
);

CREATE INDEX IF NOT EXISTS ix_rollup_daily_endpoint  ON request_rollup_daily(endpoint, bucket_date);
CREATE INDEX IF NOT EXISTS ix_rollup_hourly_endpoint ON request_rollup_hourly(endpoint, bucket_hour);
```

## Source classification

Pure static helper, evaluated once per request:

```
Classify(HttpContext) -> (string source, string? callerId)
  if path startsWith "/v1/"
    if user-agent startsWith "acp-find-plugin/" -> ("mcp_plugin", null)
    else -> ("public_other", null)
  else -> ("internal", header["X-Caller"] or null)
```

`StringComparison.Ordinal` for the UA prefix; `OrdinalIgnoreCase` for path. `X-Caller` is free-text — sidecar sends `sidecar`, future cross-bot callers (DeFiEval, AgentEval) can send their own values. Schema-stable: any new caller is captured via the existing column.

## Provider error tagging

Replace `throw new InvalidOperationException(...)` in `VoyageEmbeddingProvider`, `VoyageRerankProvider`, and `ClaudeApiClient` with two new typed exceptions:

```csharp
public sealed class VoyageApiException : Exception {
    public int StatusCode { get; }
    public string? UpstreamBody { get; }
    public VoyageApiException(int statusCode, string? body, string message) : base(message) { ... }
}
public sealed class ClaudeApiException : Exception { ... }
```

Status code `0` indicates a network/transport failure (DNS, TLS, timeout, connection reset). Each `PostAsJsonAsync` call site wraps `HttpRequestException`, `TaskCanceledException`, and `OperationCanceledException` (when the caller's token isn't cancelled), rethrowing as `XxxApiException(0, ...)`. The caller's cancellation passes through unchanged.

The middleware catches both types and tags `provider_error = "voyage_<status>"` or `claude_<status>`, then rethrows so ASP.NET maps to 500 unchanged.

## Async write path

Bounded `Channel<RequestMetricEvent>(4096)` with `BoundedChannelFullMode.DropOldest`. The middleware calls `TryWrite`, which is non-blocking and never throws. On drop, an `Interlocked` counter on `MetricsChannel` advances; the operator sees this on `/metrics/summary` as `metricsDropped: N`.

A single `BackgroundService` (`MetricsWriterService`) runs two loops:

1. **Drain loop** — `await Task.WhenAny(channelRead, Task.Delay(250 ms))`. Buffer up to 100 events, flush on either trigger via `RequestMetricsRepository.InsertManyAsync` (single transaction).
2. **Rollover loop** — every hour at minute 5, aggregate the hour-just-past from `request_log` into `request_rollup_hourly`. Daily at 03:00 UTC, write yesterday's row to `request_rollup_daily` and `DELETE FROM request_log WHERE ts < $cutoff` for `cutoff = now - 14 days`.

The rollover offset (xx:05, 03:00) avoids overlapping `LifetimeSnapshotService`'s 02:00 run. Mirror its `try/catch per iteration, log-and-continue` error policy.

## Operator endpoints

All five sit outside `/v1/*` and outside `/health`, so the existing X-API-Key middleware (`Program.cs:152-154`) gates them with no new auth code:

| Method + Path | Query | Returns |
|---|---|---|
| `GET /metrics/summary` | `days` (default 7) | totals + per-source counts + error rate over the window, plus `metricsDropped` |
| `GET /metrics/timeseries` | `days` (1-90), `granularity=hour\|day` | bucketed counts + error rates |
| `GET /metrics/endpoints` | `days` (1-90) | per-endpoint table: count, p50/p95 latency, 4xx/5xx/429 counts, per-source split |
| `GET /metrics/top` | `dim=query\|agent`, `days`, `limit` (default 20, max 200) | top-N by hire/lookup count (raw-table-bound, max 14 d window) |
| `GET /metrics/errors` | `days` (default 1), `limit` (default 100, max 1000) | recent raw rows where `status_code >= 400 OR provider_error IS NOT NULL` |

For `EndpointsAsync`, when `days <= 14`, read raw rows for true p50/p95. For longer windows, fall back to `sum_duration_ms / count` from the rollup (approximate average).

## Retention

- `request_log`: **14 days**. Pruned daily at 03:00 UTC.
- `request_rollup_hourly`: **90 days**. Pruned daily.
- `request_rollup_daily`: **forever**. ~50 bytes × 14 endpoints × 3 sources × 5 status classes × 365 days ≈ 13 MB/year.

## Risks and tradeoffs

- **Body buffering on `/search` and `/composeStack`** adds a buffered `MemoryStream` to those endpoints' hot path. 2 KB cap; Voyage round-trip dominates request latency by ~100×. Quantify post-deploy via `/metrics/endpoints`.
- **Channel drop policy = `DropOldest`**: under burst, lose metrics, never block requests. Operator sees the drop count via `/metrics/summary` and can adjust capacity if it ever goes non-zero.
- **SQLite contention** between the metrics batch writer (every 250 ms) and the hourly rollover at xx:05. `Cache=Shared` plus WAL handles this comfortably at expected volume. The xx:05 offset deliberately avoids the 02:00 lifetime snapshot.
- **No PII redaction**: raw queries and wallet addresses are stored as-is per requirements. If that ever changes, add a config flag that nulls `query_text` / `agent_address` on insert.
- **Top-N query history bounded by raw retention (14 d)**: longer windows aren't supported in v1. If needed later, add a `query_top_rollup_daily` table.

## Verification (manual, after build)

Boot via `cd ACP_Metabot.Api && dotnet run`, then:

1. **Schema check:** `sqlite3 data/acp_metabot.db ".schema request_log"` shows the new table.
2. **Source split:**
   ```
   curl http://localhost:5000/v1/health
   curl -H "User-Agent: acp-find-plugin/1.2.3" http://localhost:5000/v1/health
   sleep 2
   sqlite3 data/acp_metabot.db "SELECT source, count(*) FROM request_log GROUP BY source"
   ```
   Expect `mcp_plugin=1, public_other=1`.
3. **Body capture:** `curl -X POST -d '{"query":"perp closer"}' -H "Content-Type: application/json" http://localhost:5000/v1/search`. Expect a row with `query_text='perp closer'`, `duration_ms` populated.
4. **Provider error capture:** restart with `VOYAGE_API_KEY=invalid`, hit `/v1/search`. Expect `provider_error='voyage_401'` (or similar) and `status_code=500`.
5. **Auth gating:** `curl http://localhost:5000/metrics/summary` → 401. With `-H "X-API-Key: $INTERNAL_API_KEY"` → 200, JSON `{ window, summary: {...}, metricsDropped: 0 }`.
6. **Rollover:** wait until xx:05 of next hour. `SELECT count(*) FROM request_rollup_hourly` → non-zero.
7. **Top-N:** `curl -H "X-API-Key: ..." "http://localhost:5000/metrics/top?dim=query&days=1"` → array containing `"perp closer"`.
8. **Recent errors:** `curl -H "X-API-Key: ..." "http://localhost:5000/metrics/errors?days=1"` → row from step 4.
9. **429 capture:** spam `/v1/composeStack` past the 5/h cap. `curl -H "X-API-Key: ..." "http://localhost:5000/metrics/endpoints?days=1"` shows non-zero `429` for that endpoint.
10. **Build clean:** `dotnet build` zero warnings; `npm run build` clean tsc.

## Scaling levers

See `docs/runbook-scaling.md`. Seven levers, each with a metric-keyed signal, action, effort, and risk.
