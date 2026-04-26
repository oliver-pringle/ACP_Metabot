# watchOffering — design spec

**Date:** 2026-04-26
**Status:** Draft, awaiting user sign-off
**Owner:** Oliver (via Claude Code)

## Goal

Add a third offering to TheMetaBot called `watchOffering`. A buyer pays once and registers a standing semantic-search query; the bot polls the marketplace at a fixed interval and POSTs new matches to a buyer-provided webhook URL over a fixed window. Single ACP job; payment upfront; initial snapshot delivered synchronously, follow-up alerts asynchronously.

## Why

ACP v2 has no push notification primitive — sellers can't initiate a job to a buyer. Recurring revenue patterns (subscriptions, alerts) don't map cleanly onto the one-shot job model. This offering bridges that gap with a webhook for out-of-band delivery while keeping the on-chain payment as a single-job interaction. It's also the first offering that turns TheMetaBot's index into a *standing* product instead of a one-shot lookup, which is the key recurring-revenue lever.

## Requirement schema (buyer input)

```json
{
  "type": "object",
  "properties": {
    "query":         { "type": "string",  "description": "Natural-language description of offerings to watch for." },
    "webhookUrl":    { "type": "string",  "description": "HTTPS URL to POST new matches to. Fire-and-forget; ensure it's publicly reachable." },
    "durationDays":  { "type": "integer", "description": "How long to watch (1-30). Defaults to 7." },
    "intervalHours": { "type": "integer", "description": "Polling interval in hours (1-24). Defaults to 6." },
    "minScore":      { "type": "number",  "description": "Optional minimum cosine similarity (0-1) for an offering to count as a match." },
    "priceMaxUsdc":  { "type": "number",  "description": "Optional max-price filter (USDC); offerings above this are ignored." },
    "maxAlerts":     { "type": "integer", "description": "Cap on total alerts delivered over the watch lifetime (1-100). Defaults to 20." }
  },
  "required": ["query", "webhookUrl"]
}
```

Validator additionally requires `webhookUrl` to start with `https://` (no `http://`, no other schemes).

## Deliverable schema (initial response on the ACP job)

```json
{
  "type": "object",
  "properties": {
    "watchId":        { "type": "string",  "description": "UUID identifying this watch." },
    "expiresAt":      { "type": "string",  "description": "ISO timestamp when the bot stops polling." },
    "intervalHours":  { "type": "integer" },
    "maxAlerts":      { "type": "integer" },
    "initialMatches": { "type": "array", "items": { "type": "object" }, "description": "Top-N matches at registration time. NOT counted toward the alert cap. Same shape as search results." }
  },
  "required": ["watchId", "expiresAt", "intervalHours", "maxAlerts", "initialMatches"]
}
```

## Webhook payload (each alert POST)

```json
{
  "watchId":         "uuid",
  "alertNumber":     3,
  "remainingAlerts": 17,
  "query":           "the original query text",
  "matches":         [/* OfferingMatch entries, only those new since last poll */],
  "polledAt":        "ISO ts"
}
```

Best-effort delivery: 3 retries with exponential backoff (1s, 4s, 16s) per attempt, no guarantees beyond that.

## Pricing

`watchOffering` flat **$0.50** regardless of duration (v1). Trivial to switch to per-day pricing later.

## SQLite schema (additions to `Db.cs`)

```sql
CREATE TABLE IF NOT EXISTS watches (
  id TEXT PRIMARY KEY,
  job_id INTEGER NOT NULL UNIQUE,
  buyer_address TEXT NOT NULL,
  query TEXT NOT NULL,
  webhook_url TEXT NOT NULL,
  duration_days INTEGER NOT NULL,
  interval_hours INTEGER NOT NULL,
  min_score REAL,
  price_max_usdc REAL,
  max_alerts INTEGER NOT NULL,
  alerts_delivered INTEGER NOT NULL DEFAULT 0,
  webhook_consecutive_failures INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'active',
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  last_polled_at TEXT
);

CREATE TABLE IF NOT EXISTS watch_seen (
  watch_id TEXT NOT NULL,
  offering_id INTEGER NOT NULL,
  first_seen_at TEXT NOT NULL,
  PRIMARY KEY (watch_id, offering_id),
  FOREIGN KEY (watch_id) REFERENCES watches(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_watches_active ON watches(status, last_polled_at);
```

`status` values: `active`, `expired`, `exhausted` (max alerts hit), `webhook_failing` (still active but webhook unreliable), `cancelled` (5+ consecutive POST failures).

`watch_seen` is the dedup table — every offering ID surfaced during the watch is recorded; subsequent polls only "alert" on offerings NOT already in this set. Initial matches are also seeded here so the buyer doesn't get an alert for them on the first poll cycle.

## Architecture

### C# tier (`ACP_Metabot.Api/`)

- `Models/Watch.cs` — record types for Watch + WatchSeen.
- `Data/WatchRepository.cs` — ADO.NET CRUD: `CreateAsync`, `GetDueAsync`, `MarkPolledAsync`, `RecordSeenAsync`, `IncrementAlertsAsync`, `MarkStatusAsync`.
- `Services/WatchService.cs` — orchestrates: `RegisterWatch(req)` returns `(watchId, expiresAt, initialMatches)`; `PollDueWatchesAsync(ct)` does one cycle.
- `Services/WebhookDeliveryService.cs` — `DeliverAsync(url, payload, ct)` with retries.
- `Services/WatchPollerBackgroundService.cs` — IHostedService that runs every 30m, calls `PollDueWatchesAsync`.
- `Program.cs`:
  - `POST /watches` — register watch (called by sidecar at offering execute time)
  - `GET /watches/{id}` — debug/status
- `Db.cs` — schema bootstrap adds the two tables.

### TS sidecar (`acp-v2/src/`)

- `offerings/watchOffering.ts` — new offering: requirementSchema, validator, `execute()` calls `POST /watches`.
- `pricing.ts` — add `watchOffering: 0.50`.
- `apiClient.ts` — add `registerWatch(req)` method and types.
- `offerings/registry.ts` — register new offering.
- No changes to `seller.ts` (it routes by offering name automatically).

## Polling flow (one cycle)

```
every 30 minutes (BackgroundService tick):
  expired = SELECT WHERE status='active' AND datetime(expires_at) < datetime('now')
  UPDATE expired set status='expired'

  due = SELECT WHERE status IN ('active','webhook_failing')
                   AND (last_polled_at IS NULL
                        OR datetime(last_polled_at) < datetime('now', '-' || interval_hours || ' hours'))
                   AND datetime('now') < expires_at

  for each watch in due:
    results = SearchService.SearchAsync(query, limit=50, minScore, priceMax, ct)
    seen_ids = SELECT offering_id FROM watch_seen WHERE watch_id=?
    new = results WHERE offering_id NOT IN seen_ids

    if new is empty:
      UPDATE last_polled_at; consecutive_failures=0; continue

    payload = { watchId, alertNumber, remainingAlerts, query, matches:new, polledAt }
    ok = WebhookDeliveryService.DeliverAsync(webhookUrl, payload)

    if ok:
      INSERT new ids INTO watch_seen
      UPDATE alerts_delivered = alerts_delivered + 1
      UPDATE consecutive_failures = 0
      UPDATE last_polled_at
      if alerts_delivered >= max_alerts: UPDATE status='exhausted'
      if status='webhook_failing': UPDATE status='active'
    else:
      UPDATE consecutive_failures = consecutive_failures + 1
      UPDATE last_polled_at
      if consecutive_failures >= 3 AND status='active': UPDATE status='webhook_failing'
      if consecutive_failures >= 5: UPDATE status='cancelled'
```

## Edge cases

| Case | Behavior |
|---|---|
| Webhook fails 3 times in a row | status → `webhook_failing` (still polled) |
| Webhook fails 5 times in a row | status → `cancelled` (no further polling) |
| Buyer URL is dead from the start | First POST fails → counter=1; eventually marks failing |
| Initial matches > maxAlerts | Initial matches don't count toward cap. Only post-registration alerts do. |
| Same query, no new matches in a window | last_polled_at advances, no webhook fired |
| Container restart mid-watch | SQLite is bind-mounted (per CLAUDE.md), state persists |
| Buyer hires watchOffering twice with same params | Two distinct watches, two distinct watchIds |

## Out of scope for v1

- Email / Telegram alerts (webhooks only)
- Per-day pricing
- Buyer-side cancel/extend API (watches expire naturally)
- Webhook HMAC signing (buyer can put a secret in the URL itself if they want auth)
- Reliability-aware filtering — that's step B
- Buyer query for status of an existing watch — debug-only `GET /watches/{id}` is internal

## Pricing tier-2 idea (parked)

A `watchOffering-extended` at $2 with longer windows (90 days) and higher caps (200 alerts). Decide after we see how v1 sells.

## Test plan

- **Smoke:** hire `watchOffering` via `acp_hire` MCP tool with a `webhookUrl` pointing at `https://webhook.site/<uuid>` (or a local ngrok). Verify initial deliverable shape, observe the first scheduled poll on the webhook viewer.
- **Manual webhook test:** add a debug endpoint `POST /watches/{id}/poll-now` that triggers a single poll on demand, gated by a header so it's not accidentally hit in production.
- **Edge case test:** point webhookUrl at `https://httpbin.org/status/500` and confirm consecutive_failures increments and status transitions correctly.
- **No production tests** until Oracle Cloud deploy lands (step between A and B per user's plan).

## Dashboard registration (after code ships)

User must register the new offering on app.virtuals.io:
- Name: `watchOffering`
- Description: (TBD — pull from offering definition; emphasize "monitoring", "alerts", "subscription-style")
- Price: $0.50
- Requirement schema: paste the JSON above
- Deliverable schema: paste the JSON above

## Implementation order

1. SQLite schema additions (Db.cs)
2. WatchRepository (ADO.NET)
3. Models, WatchService, WebhookDeliveryService
4. POST /watches endpoint
5. WatchPollerBackgroundService
6. Sidecar offering + apiClient method + pricing entry + registry
7. typecheck, dotnet build, docker compose up --build
8. Smoke test via ACP_Tester MCP with webhook.site URL
9. User registers offering on dashboard
10. End-to-end smoke on real marketplace
