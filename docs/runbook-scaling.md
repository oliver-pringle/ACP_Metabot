# TheMetaBot Scaling Runbook

**Last reviewed:** 2026-04-29
**Companion spec:** [`superpowers/specs/2026-04-29-metrics-and-scaling-design.md`](superpowers/specs/2026-04-29-metrics-and-scaling-design.md)

Seven scaling levers, ordered by likely trigger order. Each names the metric signal that fires it (queryable from `/metrics/*`), the action, the effort, and the risk. None of these are pre-implemented — pull a lever only when its signal trips.

## Reading the metrics

All signals reference operator-only endpoints gated by `X-API-Key: $INTERNAL_API_KEY`:

```bash
curl -H "X-API-Key: $INTERNAL_API_KEY" https://api.acp-metabot.dev/metrics/summary?days=7
curl -H "X-API-Key: $INTERNAL_API_KEY" https://api.acp-metabot.dev/metrics/timeseries?days=7&granularity=hour
curl -H "X-API-Key: $INTERNAL_API_KEY" https://api.acp-metabot.dev/metrics/endpoints?days=7
curl -H "X-API-Key: $INTERNAL_API_KEY" "https://api.acp-metabot.dev/metrics/top?dim=query&days=7"
curl -H "X-API-Key: $INTERNAL_API_KEY" "https://api.acp-metabot.dev/metrics/errors?days=1"
curl -H "X-API-Key: $INTERNAL_API_KEY" "https://api.acp-metabot.dev/metrics/clients?days=7"
```

Note: `/metrics/*` **is reachable through the public gateway** — Caddy reverse-proxies every path to the API container, and the X-API-Key middleware on the API gates these routes (returns `401 Unauthorized` without a valid key). The `INTERNAL_API_KEY` is therefore the sole authentication boundary; treat it like a production credential and rotate if leaked. Verified 2026-05-04 — earlier versions of this runbook claimed the path was internal-only, which was incorrect.

`.env` files aren't auto-loaded into your interactive shell; if `${INTERNAL_API_KEY}` comes back empty, source it before curling:

```bash
set -a && . /root/ACP_Metabot/.env && set +a
echo "key length: ${#INTERNAL_API_KEY}"   # should print 64
```

### Client (User-Agent) telemetry

Three endpoints answer "who is calling the gateway and what are they hitting?". All bounded by 14 d raw retention since the rollup tables don't dimension by `user_agent`.

```bash
# 1. List per User-Agent (existing). Filter to one family or exclude noise.
curl -H "X-API-Key: $INTERNAL_API_KEY" "https://api.acp-metabot.dev/metrics/clients?days=7"
curl -H "X-API-Key: $INTERNAL_API_KEY" "https://api.acp-metabot.dev/metrics/clients?days=7&family=acp-find-plugin"
curl -H "X-API-Key: $INTERNAL_API_KEY" "https://api.acp-metabot.dev/metrics/clients?days=7&excludeFamilies=curl,unknown"

# 2. High-level summary by family.
curl -H "X-API-Key: $INTERNAL_API_KEY" "https://api.acp-metabot.dev/metrics/clients/summary?days=7"

# 3. Per-endpoint breakdown — optionally restricted to one family.
#    Use this to drill into "what endpoints does curl/8.5.0 hit?".
curl -H "X-API-Key: $INTERNAL_API_KEY" "https://api.acp-metabot.dev/metrics/clients/endpoints?days=7&family=curl"
```

Families: `acp-find-plugin` (with extracted `version`), `curl`, `browser` (anything starting with `Mozilla/`), `unknown` (empty/null UA), `other` (anything else — bots, scanners, custom integrations). The C# `UserAgentClassifier` and the `ClassifiedCte` SQL in `RequestMetricsRepository` implement the same rules; tests in `ACP_Metabot.Api.Tests/UserAgentClassifierTests.cs` pin the C# side, so any drift between them is loud.

**Worked example — investigating high-volume non-plugin traffic:** if `/metrics/clients/summary` shows `curl` is dominating with N requests, run `/metrics/clients/endpoints?family=curl` to see if it's hitting `/health` (uptime monitor — fine), `/v1/search` repeatedly with one query (likely abuse — see Lever 5 below), or `/v1/*` evenly (real integrator using curl directly — interesting).

## Lever 1 — Cache query embeddings on the search hot path

**Signal:** `/metrics/timeseries` shows p95 latency on `/v1/search` exceeds 2 s sustained over 30 minutes, OR `/metrics/endpoints` shows `voyage_errors` rate exceeds 1% of `/v1/search` volume.

**Action:** Wrap `EmbedQueryAsync` in `VoyageEmbeddingProvider` with a `ConcurrentDictionary<string, Lazy<Task<float[]>>>` keyed by `(model, normalize(query))`. Cap at 1000 entries with a 1 h timestamp-based TTL. Hot queries (visible via `/metrics/top?dim=query`) collapse to one Voyage call per TTL.

**Effort:** ~2 hours. Single-file change. **Risk:** Low — cache miss is correct behaviour.

## Lever 2 — Quantize embeddings to int8

**Signal:** Container memory exceeds 70% of droplet RAM, OR `corpus.count × 1024 × 4 bytes > 200 MB` (corpus count surfaced via `/v1/health`).

**Action:** Voyage supports `output_dtype=int8` natively. 4× memory reduction, < 1% recall drop. Add a column to `offering_embeddings` for dtype, a parallel decode path in `SearchService`, an indexer flag, then trigger a reindex (`POST /index/refresh`).

**Effort:** ~1 day. **Risk:** Medium — touches the hot search loop. Validate recall against a held-out query set before flipping.

## Lever 3 — Split metrics into a separate SQLite file

**Signal:** `MetricsWriterService` logs flushes > 100 ms (instrument it before pulling this lever), OR observable "database is locked" errors in container logs.

**Action:** New `Db` instance with its own connection string for `metrics.db`. `RequestMetricsRepository` takes the metrics `Db` instead of the main one. The two databases never share locks; main DB stays clean.

**Effort:** ~3 hours. Additive; rollback is reverting one config line. **Risk:** Low.

## Lever 4 — Reputation cache TTL + warm set

**Signal:** `/metrics/endpoints` shows `/v1/agentReputation` p95 > 5 s, OR `compute_failed` errors > 1%.

**Action:**
- (a) Bump `agent_reputation_cache` TTL from 24 h to 48 h. One constant in `AgentReputationCacheRepository`.
- (b) Increase `ReputationWarmerService` warm set from current size to top-500 agents.

**Effort:** ~30 min for (a); ~1 h for (b). **Risk:** Low. (a) trades freshness for capacity; (b) costs more chain RPC.

## Lever 5 — Tighten Caddy / ASP.NET rate limits

**Signal:** `/metrics/top?dim=agent` shows a single agent address dominating > 30% of `/v1/agentReputation` calls (one buggy or hostile caller); OR `/metrics/endpoints` shows `429` count < 1% of total volume (the limiter is too generous; we're over-provisioning).

**Action:** Tune the per-IP policies in `Program.cs`. Current policy table:

| Policy | Routes | Permits/IP/hr |
|---|---|---|
| `public-search` | `POST /v1/search` | 30 |
| `public-compose` | `POST /v1/composeStack` | 5 |
| `public-search-agents` | `POST /v1/searchAgents` | 30 |
| `public-reputation` | `GET /v1/agentReputation*` | 60 |
| `public-digest` | `GET /v1/digest` | 60 |
| `public-recent-hires` | `GET /v1/recentHires` | 60 |
| `public-browse-agent` | `GET /v1/agent/{addr}`, `GET /v1/watches/{id}` | 60 |
| `public-agent-recent-jobs` | `GET /v1/agentRecentJobs` | 20 (RPC-heavy) |

Drop permit counts on the abusing policy, or add a stricter per-IP limit. The `agent-recent-jobs` policy is the tightest because each call triggers chunked RPC scans; treat it as an outlier when reading `/metrics/endpoints` latency.

**Effort:** ~5 min per knob. **Risk:** Low. Just numbers in existing code.

## Lever 6 — Read-replica horizontal split

**Signal:** Droplet CPU > 70% sustained, AND levers 1–5 are exhausted.

**Action:** Stand up a second `acp-metabot-api` container behind Caddy with a read-only mounted snapshot of `acp_metabot.db` (refreshed nightly via `sqlite3 .backup`). Route `/v1/*` reads to the replica; writers (indexer, watch poller, snapshot service, metrics writer) stay on the primary. The `agent_reputation_cache` already tolerates 24 h staleness; same for `/v1/digest`, `/v1/recentHires`, `/v1/searchAgents`, `/v1/agent/{address}`, `/v1/watches/{id}`, `/v1/categories`, `/v1/health`. Caddy `upstream` block selects round-robin or sticky-IP. **Exception:** `/v1/agentRecentJobs` requires live chain RPC and won't benefit from a snapshot replica — keep it on the primary.

**Effort:** ~2 days — Caddyfile edits, snapshot cron, container topology, deploy script.

**Risk:** Medium-high. Replication lag becomes operationally visible. Mitigation: only worth pulling once the data tolerance is acceptable.

## Lever 7 — Exit SQLite (last resort)

**Signal:** Lever 6 pulled and write-side bottleneck remains (indexer fetch durations climbing, snapshot service running long, `request_log` flushes slow).

**Action:** Workspace convention bans EF/Dapper/PG/Mongo/Redis. The remaining in-bounds option is to keep ADO.NET and switch the backing engine to a SQLite-compatible drop-in (libSQL/Turso, Cloudflare D1) — or reopen the workspace constraint with the user.

**Effort:** Weeks. Touches every repository.

**Risk:** High. **Do not pull without an explicit user decision.**
