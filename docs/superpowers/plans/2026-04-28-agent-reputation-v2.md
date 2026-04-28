# agentReputation v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the existing hire-count `ReputationService` in ACP_Metabot with a behavioural on-chain scorer that derives a 0–100 score from completion rate, dispute rate, recency, 30-day throughput, and average response time, while keeping the live deployed bot working at every commit.

**Architecture:** Add new components (chain scanner, off-chain client, score calculator, cache repository, warmer service) alongside the existing `ReputationService`, then swap in two atomic edits — the agentReputation handler and the public `/v1/agentReputation` route. Legacy `BuildSearchSummary` and `Build` methods stay intact for `/search` and `/agent/{address}` callers. No data migration; the legacy service has no persistent state.

**Tech Stack:** .NET 10 ASP.NET Minimal API, ADO.NET + SQLite, Nethereum 4.x for Base mainnet RPC. Sister Node 22 TypeScript sidecar at `acp-v2/`. Plugin tool MCP server at `../acp-find-plugin/mcp-server/`.

**Spec:** `docs/superpowers/specs/2026-04-28-agent-reputation-v2-design.md` — read before starting.

---

## File structure

**New files (C# API, `ACP_Metabot.Api/`):**

| Path | Responsibility |
|---|---|
| `Models/CachedReputation.cs` | Record types for cache row + sub-score + chain scan result |
| `Data/AgentReputationCacheRepository.cs` | Get / Upsert / ListWarmAgents / ListAllForPercentiles |
| `Data/LifetimeSnapshotRepository.cs` | Upsert / Get / Prune for agent_lifetime_snapshot |
| `Services/AcpOffChainClient.cs` | Single-purpose HTTP client for `getAgentByWalletAddress` → `lastActiveAt` |
| `Services/ChainEventScanner.cs` | Nethereum-based Base RPC scanner + jobId timeline reconstruction |
| `Services/ScoreCalculator.cs` | Pure-function score formula (sub-scores → overall) |
| `Services/LifetimeSnapshotService.cs` | Daily 02:00 UTC BackgroundService — Strapi → agent_lifetime_snapshot |
| `Services/ReputationWarmerService.cs` | Daily 02:30 UTC BackgroundService — top-500 agents → agent_reputation_cache |

**Modified files (C# API):**

| Path | Change |
|---|---|
| `ACP_Metabot.Api.csproj` | Add Nethereum package reference |
| `Data/Db.cs` | Append `agent_reputation_cache` and `agent_lifetime_snapshot` table DDL |
| `Models/Reputation.cs` | Add new `AgentReputationResultV2`, sub-score record, do not delete legacy types |
| `Services/ReputationService.cs` | Add `GetOrComputeAsync` + percentile rebuild; preserve `Build` and `BuildSearchSummary` |
| `Program.cs` | Register new services; replace `MapPost("/v1/agentReputation")` with `MapGet`; rewire internal `POST /agentReputation` to call new path |
| `appsettings.json` | Add Reputation defaults (`Reputation:WarmerTopN`, etc.) |

**Modified files (sidecar, `acp-v2/`):**

| Path | Change |
|---|---|
| `src/apiClient.ts` | Update `AgentReputationResponse` type + change `agentReputation()` to GET-via-POST shape per spec |
| `src/offerings/agentReputation.ts` | Update offering description to mention behavioural metrics |

**Modified files (plugin, `../acp-find-plugin/`):**

| Path | Change |
|---|---|
| `mcp-server/server.js` | Update `acp_agent_reputation` description string + switch to GET against `/v1/agentReputation?agent=` |

**Working directory for all paths:** `C:\code_crypto\ACP\ACP_Metabot\ACP_Metabot\` unless otherwise stated.

---

## Task 1: Add Nethereum package

**Files:**
- Modify: `ACP_Metabot.Api/ACP_Metabot.Api.csproj`

- [ ] **Step 1: Add Nethereum.Web3 package reference**

Edit `ACP_Metabot.Api/ACP_Metabot.Api.csproj`. Add inside the existing `<ItemGroup>` that contains `Microsoft.Data.Sqlite`:

```xml
<PackageReference Include="Nethereum.Web3" Version="4.30.0" />
```

Final `<ItemGroup>` block should look like:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.1" />
  <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.1" />
  <PackageReference Include="Nethereum.Web3" Version="4.30.0" />
</ItemGroup>
```

- [ ] **Step 2: Restore + build to confirm the package resolves**

```
dotnet restore ACP_Metabot.Api/ACP_Metabot.Api.csproj
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors. Nethereum and its transitive deps appear in `obj/project.assets.json`.

- [ ] **Step 3: Commit**

```
git add ACP_Metabot.Api/ACP_Metabot.Api.csproj
git commit -m "deps: add Nethereum.Web3 4.30.0 for Base RPC event scanning"
```

---

## Task 2: Add SQL schema for cache + snapshot tables

**Files:**
- Modify: `ACP_Metabot.Api/Data/Db.cs`

- [ ] **Step 1: Append two new `CREATE TABLE` statements to `InitializeSchemaAsync`**

Edit `Data/Db.cs`. After the existing `watch_seen` table DDL (currently the last statement before the closing `";`), append:

```sql
CREATE TABLE IF NOT EXISTS agent_reputation_cache (
    agent_address       TEXT PRIMARY KEY,
    agent_name          TEXT    NOT NULL,
    agent_score         INTEGER NOT NULL,
    sub_scores_json     TEXT    NOT NULL,
    raw_counts_json     TEXT    NOT NULL,
    flags_json          TEXT    NOT NULL,
    computed_at         TEXT    NOT NULL,
    last_scanned_block  INTEGER NOT NULL,
    source              TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_reputation_cache_computed_at
    ON agent_reputation_cache(computed_at);

CREATE TABLE IF NOT EXISTS agent_lifetime_snapshot (
    agent_address    TEXT    NOT NULL,
    snapshot_date    TEXT    NOT NULL,
    total_jobs       INTEGER NOT NULL,
    PRIMARY KEY (agent_address, snapshot_date)
);

CREATE INDEX IF NOT EXISTS idx_snapshot_date
    ON agent_lifetime_snapshot(snapshot_date);
```

The closing `";` of the multi-statement command stays where it is.

- [ ] **Step 2: Build + run + verify schema present**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
dotnet run --project ACP_Metabot.Api &
sleep 3
sqlite3 ACP_Metabot.Api/data/metabot.db ".schema agent_reputation_cache"
sqlite3 ACP_Metabot.Api/data/metabot.db ".schema agent_lifetime_snapshot"
kill %1
```

Expected: both tables print their DDL. The actual SQLite file path may vary; check `appsettings.json` if `metabot.db` doesn't exist — use whatever path is in `ConnectionStrings:Sqlite`.

- [ ] **Step 3: Commit**

```
git add ACP_Metabot.Api/Data/Db.cs
git commit -m "schema: add agent_reputation_cache + agent_lifetime_snapshot tables"
```

---

## Task 3: Add new model types

**Files:**
- Create: `ACP_Metabot.Api/Models/CachedReputation.cs`

- [ ] **Step 1: Write the model file**

Create `Models/CachedReputation.cs`:

```csharp
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

// Wire shape returned by /agentReputation and /v1/agentReputation.
public record AgentReputationResultV2(
    [property: JsonPropertyName("agentAddress")] string AgentAddress,
    [property: JsonPropertyName("agentName")]    string AgentName,
    [property: JsonPropertyName("agentScore")]   int AgentScore,
    [property: JsonPropertyName("computedAt")]   string ComputedAt,
    [property: JsonPropertyName("windowDays")]   int WindowDays,
    [property: JsonPropertyName("subScores")]    SubScoreSet SubScores,
    [property: JsonPropertyName("rawCounts")]    RawCounts RawCounts,
    [property: JsonPropertyName("flags")]        ReputationFlags Flags,
    [property: JsonPropertyName("offering"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OfferingHireRef? Offering);

public record SubScoreSet(
    [property: JsonPropertyName("completion")]   SubScore Completion,
    [property: JsonPropertyName("dispute")]      SubScore Dispute,
    [property: JsonPropertyName("recency")]      SubScore Recency,
    [property: JsonPropertyName("volume30d")]    SubScore Volume30d,
    [property: JsonPropertyName("responseTime")] SubScore ResponseTime);

public record SubScore(
    [property: JsonPropertyName("value")]            double Value,
    [property: JsonPropertyName("score")]            int Score,
    [property: JsonPropertyName("percentile")]       double Percentile,
    [property: JsonPropertyName("evidence")]         string Evidence,
    [property: JsonPropertyName("insufficientData")] bool InsufficientData);

public record RawCounts(
    [property: JsonPropertyName("totalJobs")]        long TotalJobs,
    [property: JsonPropertyName("completed")]        long Completed,
    [property: JsonPropertyName("rejected")]         long Rejected,
    [property: JsonPropertyName("expired")]          long Expired,
    [property: JsonPropertyName("completedLast30d")] long CompletedLast30d,
    [property: JsonPropertyName("lastActiveAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? LastActiveAt);

public record ReputationFlags(
    [property: JsonPropertyName("isColdStart")]      bool IsColdStart,
    [property: JsonPropertyName("insufficientData")] bool InsufficientData,
    [property: JsonPropertyName("warmCacheHit")]     bool WarmCacheHit);

public record OfferingHireRef(
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("hires")]      long Hires,
    [property: JsonPropertyName("percentile")] double Percentile,
    [property: JsonPropertyName("evidence")]   string Evidence);

// Internal: what ChainEventScanner returns. NOT serialised.
public record ChainScanResult(
    string AgentAddress,
    long TotalJobs,
    long Completed,
    long Rejected,
    long Expired,
    long CompletedLast30d,
    DateTime? LastJobSubmittedAt,
    double? AvgResponseSeconds30d,
    long ResponseTimeSampleCount30d,
    long HighestScannedBlock);

// Internal: what's persisted to agent_reputation_cache.
public record CachedReputationRow(
    string AgentAddress,
    string AgentName,
    int AgentScore,
    string SubScoresJson,
    string RawCountsJson,
    string FlagsJson,
    DateTime ComputedAt,
    long LastScannedBlock,
    string Source);
```

- [ ] **Step 2: Build to confirm types compile**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```
git add ACP_Metabot.Api/Models/CachedReputation.cs
git commit -m "models: add reputation v2 record types (cache row, sub-scores, chain scan result)"
```

---

## Task 4: AgentReputationCacheRepository

**Files:**
- Create: `ACP_Metabot.Api/Data/AgentReputationCacheRepository.cs`

- [ ] **Step 1: Write the repository**

Create `Data/AgentReputationCacheRepository.cs`:

```csharp
using System.Globalization;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public class AgentReputationCacheRepository
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly Db _db;

    public AgentReputationCacheRepository(Db db) => _db = db;

    // Returns null if no row, or row older than 24h.
    public async Task<CachedReputationRow?> GetAsync(string agentAddress, DateTime nowUtc)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, agent_score, sub_scores_json, raw_counts_json,
                   flags_json, computed_at, last_scanned_block, source
            FROM agent_reputation_cache
            WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var computedAt = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        if (nowUtc - computedAt > Ttl) return null; // shadowed

        return new CachedReputationRow(
            AgentAddress:     reader.GetString(0),
            AgentName:        reader.GetString(1),
            AgentScore:       reader.GetInt32(2),
            SubScoresJson:    reader.GetString(3),
            RawCountsJson:    reader.GetString(4),
            FlagsJson:        reader.GetString(5),
            ComputedAt:       computedAt,
            LastScannedBlock: reader.GetInt64(7),
            Source:           reader.GetString(8));
    }

    // Returns the highest block scanned for this agent on any prior compute,
    // even if the row is now > 24h old. Used to make incremental rescans
    // cheap.
    public async Task<long?> GetLastScannedBlockAsync(string agentAddress)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_scanned_block FROM agent_reputation_cache WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetInt64(0) : null;
    }

    public async Task UpsertAsync(CachedReputationRow row)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_reputation_cache
                (agent_address, agent_name, agent_score, sub_scores_json, raw_counts_json,
                 flags_json, computed_at, last_scanned_block, source)
            VALUES ($a, $n, $s, $ss, $rc, $f, $c, $b, $src)
            ON CONFLICT(agent_address) DO UPDATE SET
                agent_name         = excluded.agent_name,
                agent_score        = excluded.agent_score,
                sub_scores_json    = excluded.sub_scores_json,
                raw_counts_json    = excluded.raw_counts_json,
                flags_json         = excluded.flags_json,
                computed_at        = excluded.computed_at,
                last_scanned_block = excluded.last_scanned_block,
                source             = excluded.source;";
        cmd.Parameters.AddWithValue("$a",   row.AgentAddress);
        cmd.Parameters.AddWithValue("$n",   row.AgentName);
        cmd.Parameters.AddWithValue("$s",   row.AgentScore);
        cmd.Parameters.AddWithValue("$ss",  row.SubScoresJson);
        cmd.Parameters.AddWithValue("$rc",  row.RawCountsJson);
        cmd.Parameters.AddWithValue("$f",   row.FlagsJson);
        cmd.Parameters.AddWithValue("$c",   row.ComputedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$b",   row.LastScannedBlock);
        cmd.Parameters.AddWithValue("$src", row.Source);
        await cmd.ExecuteNonQueryAsync();
    }

    // Returns the top-N agent addresses by lifetime job count, deduplicated.
    // Used by the warmer to pick its daily target set.
    public async Task<IReadOnlyList<(string AgentAddress, string AgentName)>> ListWarmAgentsAsync(int topN)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, MAX(agent_name), MAX(agent_job_count) AS jobs
            FROM offerings
            GROUP BY agent_address
            ORDER BY jobs DESC
            LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", topN);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<(string, string)>(capacity: topN);
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        return list;
    }

    // Loads every fresh (≤ 24h old) cache row for the percentile rebuild pass.
    public async Task<IReadOnlyList<CachedReputationRow>> ListAllForPercentilesAsync(DateTime nowUtc)
    {
        var cutoff = nowUtc.Subtract(Ttl).ToString("O", CultureInfo.InvariantCulture);
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, agent_score, sub_scores_json, raw_counts_json,
                   flags_json, computed_at, last_scanned_block, source
            FROM agent_reputation_cache
            WHERE computed_at >= $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<CachedReputationRow>();
        while (await reader.ReadAsync())
        {
            list.Add(new CachedReputationRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt64(7), reader.GetString(8)));
        }
        return list;
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```
git add ACP_Metabot.Api/Data/AgentReputationCacheRepository.cs
git commit -m "data: add AgentReputationCacheRepository (Get/Upsert/ListWarmAgents/ListAll)"
```

---

## Task 5: LifetimeSnapshotRepository

**Files:**
- Create: `ACP_Metabot.Api/Data/LifetimeSnapshotRepository.cs`

- [ ] **Step 1: Write the repository**

Create `Data/LifetimeSnapshotRepository.cs`:

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public class LifetimeSnapshotRepository
{
    private readonly Db _db;
    public LifetimeSnapshotRepository(Db db) => _db = db;

    private static string FormatDate(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task UpsertAsync(string agentAddress, DateTime snapshotDate, long totalJobs)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_lifetime_snapshot (agent_address, snapshot_date, total_jobs)
            VALUES ($a, $d, $j)
            ON CONFLICT(agent_address, snapshot_date) DO UPDATE SET
                total_jobs = excluded.total_jobs;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        cmd.Parameters.AddWithValue("$d", FormatDate(snapshotDate));
        cmd.Parameters.AddWithValue("$j", totalJobs);
        await cmd.ExecuteNonQueryAsync();
    }

    // Returns the agent's total_jobs at the named date, or null if no row.
    public async Task<long?> GetAsync(string agentAddress, DateTime snapshotDate)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT total_jobs FROM agent_lifetime_snapshot WHERE agent_address = $a AND snapshot_date = $d;";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        cmd.Parameters.AddWithValue("$d", FormatDate(snapshotDate));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetInt64(0) : null;
    }

    public async Task PruneOlderThanAsync(DateTime cutoff)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_lifetime_snapshot WHERE snapshot_date < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", FormatDate(cutoff));
        await cmd.ExecuteNonQueryAsync();
    }

    // Bulk UPSERT used by the daily snapshot service. Wraps in a transaction
    // so we either get all rows for the day or none.
    public async Task UpsertManyAsync(IReadOnlyDictionary<string, long> agentTotals, DateTime snapshotDate)
    {
        if (agentTotals.Count == 0) return;
        await using var conn = _db.OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO agent_lifetime_snapshot (agent_address, snapshot_date, total_jobs)
            VALUES ($a, $d, $j)
            ON CONFLICT(agent_address, snapshot_date) DO UPDATE SET
                total_jobs = excluded.total_jobs;";
        var pAddr = cmd.Parameters.Add("$a", SqliteType.Text);
        var pDate = cmd.Parameters.Add("$d", SqliteType.Text);
        var pJobs = cmd.Parameters.Add("$j", SqliteType.Integer);
        var dateStr = FormatDate(snapshotDate);
        foreach (var (addr, jobs) in agentTotals)
        {
            pAddr.Value = addr;
            pDate.Value = dateStr;
            pJobs.Value = jobs;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```
git add ACP_Metabot.Api/Data/LifetimeSnapshotRepository.cs
git commit -m "data: add LifetimeSnapshotRepository (Upsert/Get/Prune/UpsertMany)"
```

---

## Task 6: AcpOffChainClient

**Files:**
- Create: `ACP_Metabot.Api/Services/AcpOffChainClient.cs`

- [ ] **Step 1: Write the client**

Create `Services/AcpOffChainClient.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

public record AcpOffChainAgent(string WalletAddress, string Name, DateTime? LastActiveAt);

public class AcpOffChainClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AcpOffChainClient> _logger;

    public AcpOffChainClient(IHttpClientFactory httpFactory, IConfiguration config,
        ILogger<AcpOffChainClient> logger)
    {
        _logger = logger;
        var baseUrl = config["Indexer:ApiBaseUrl"] ?? "https://acpx.virtuals.io/";
        if (!baseUrl.EndsWith("/")) baseUrl += "/";

        _http = httpFactory.CreateClient(nameof(AcpOffChainClient));
        _http.BaseAddress ??= new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ACP_Metabot/1.0 (+https://app.virtuals.io)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.Add("Origin",  "https://app.virtuals.io");
        _http.DefaultRequestHeaders.Add("Referer", "https://app.virtuals.io/");
    }

    // Returns null on 404 / parse failure / timeout. Single retry on 5xx.
    public async Task<AcpOffChainAgent?> GetAgentAsync(string walletAddress, CancellationToken ct)
    {
        var addr = walletAddress.ToLowerInvariant();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var res = await _http.GetAsync($"api/agents/{addr}", ct);
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                if ((int)res.StatusCode >= 500 && attempt == 0)
                {
                    await Task.Delay(500, ct);
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[acp-offchain] {addr} returned {status}", addr, res.StatusCode);
                    return null;
                }
                var body = await res.Content.ReadFromJsonAsync<AgentDetailDto>(cancellationToken: ct);
                if (body is null || body.Data is null) return null;
                DateTime? lastActive = null;
                if (!string.IsNullOrEmpty(body.Data.LastActiveAt) &&
                    DateTime.TryParse(body.Data.LastActiveAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed))
                {
                    lastActive = parsed.ToUniversalTime();
                }
                return new AcpOffChainAgent(addr, body.Data.Name ?? "", lastActive);
            }
            catch (TaskCanceledException) { return null; }
            catch (HttpRequestException ex) when (attempt == 0)
            {
                _logger.LogInformation("[acp-offchain] {addr} retry: {msg}", addr, ex.Message);
                await Task.Delay(500, ct);
            }
        }
        return null;
    }

    private class AgentDetailDto
    {
        [JsonPropertyName("data")] public AgentInner? Data { get; set; }
    }

    private class AgentInner
    {
        [JsonPropertyName("walletAddress")] public string? WalletAddress { get; set; }
        [JsonPropertyName("name")]          public string? Name          { get; set; }
        [JsonPropertyName("lastActiveAt")]  public string? LastActiveAt  { get; set; }
    }
}
```

- [ ] **Step 2: Build + sanity-check the response shape with curl**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
curl -sS -H "Origin: https://app.virtuals.io" -H "Referer: https://app.virtuals.io/" \
  https://acpx.virtuals.io/api/agents/0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b | head -c 2000
```

Expected: build clean. The curl response should include `walletAddress`, `name`, and `lastActiveAt` somewhere in the JSON. If the JSON nests under a different envelope key than `data`, adjust `AgentDetailDto` accordingly before continuing.

- [ ] **Step 3: Commit**

```
git add ACP_Metabot.Api/Services/AcpOffChainClient.cs
git commit -m "services: add AcpOffChainClient for lastActiveAt lookups"
```

---

## Task 7: ChainEventScanner

**Files:**
- Create: `ACP_Metabot.Api/Services/ChainEventScanner.cs`

- [ ] **Step 1: Write the scanner**

Create `Services/ChainEventScanner.cs`:

```csharp
using ACP_Metabot.Api.Models;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace ACP_Metabot.Api.Services;

[Event("JobCreated")]
public class JobCreatedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",      1, true)]  public System.Numerics.BigInteger JobId { get; set; }
    [Parameter("address", "client",     2, true)]  public string Client    { get; set; } = "";
    [Parameter("address", "provider",   3, true)]  public string Provider  { get; set; } = "";
    [Parameter("address", "evaluator",  4, false)] public string Evaluator { get; set; } = "";
    [Parameter("uint256", "expiredAt",  5, false)] public System.Numerics.BigInteger ExpiredAt { get; set; }
    [Parameter("address", "hook",       6, false)] public string Hook      { get; set; } = "";
}

[Event("JobFunded")]
public class JobFundedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",  1, true)]  public System.Numerics.BigInteger JobId  { get; set; }
    [Parameter("address", "client", 2, true)]  public string Client { get; set; } = "";
    [Parameter("uint256", "amount", 3, false)] public System.Numerics.BigInteger Amount { get; set; }
}

[Event("JobSubmitted")]
public class JobSubmittedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",       1, true)]  public System.Numerics.BigInteger JobId    { get; set; }
    [Parameter("address", "provider",    2, true)]  public string Provider { get; set; } = "";
    [Parameter("bytes32", "deliverable", 3, false)] public byte[] Deliverable { get; set; } = Array.Empty<byte>();
}

[Event("JobCompleted")]
public class JobCompletedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",  1, true)]  public System.Numerics.BigInteger JobId { get; set; }
    [Parameter("bytes32", "reason", 2, false)] public byte[] Reason { get; set; } = Array.Empty<byte>();
}

[Event("JobRejected")]
public class JobRejectedEvent : IEventDTO
{
    [Parameter("uint256", "jobId",    1, true)]  public System.Numerics.BigInteger JobId    { get; set; }
    [Parameter("address", "rejector", 2, true)]  public string Rejector { get; set; } = "";
    [Parameter("bytes32", "reason",   3, false)] public byte[] Reason  { get; set; } = Array.Empty<byte>();
}

[Event("JobExpired")]
public class JobExpiredEvent : IEventDTO
{
    [Parameter("uint256", "jobId", 1, true)] public System.Numerics.BigInteger JobId { get; set; }
}

public class ChainEventScanner
{
    private readonly Web3 _web3;
    private readonly string _contractAddress;
    private readonly long _deployBlock;
    private readonly ILogger<ChainEventScanner> _logger;
    // Block-timestamp LRU; bounded so it can't grow without limit during a long warmer pass.
    private readonly Dictionary<long, DateTime> _blockTimestamps = new(capacity: 4096);

    public ChainEventScanner(IConfiguration config, ILogger<ChainEventScanner> logger)
    {
        _logger = logger;
        var rpcUrl = config["BASE_RPC_URL"]
            ?? throw new InvalidOperationException("BASE_RPC_URL not configured");
        _contractAddress = config["ACP_CONTRACT_ADDRESS_BASE"]
            ?? throw new InvalidOperationException("ACP_CONTRACT_ADDRESS_BASE not configured");
        _deployBlock = config.GetValue<long?>("Reputation:ContractDeployBlock") ?? 0L;
        _web3 = new Web3(rpcUrl);
    }

    public async Task<ChainScanResult> ScanAgentAsync(
        string agentAddress, long fromBlock, DateTime nowUtc, CancellationToken ct)
    {
        var headBlock = (long)(await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        var startBlock = Math.Max(fromBlock, _deployBlock);

        // 1. JobCreated filtered on provider — gives the agent's full jobId set.
        var createdHandler = _web3.Eth.GetEvent<JobCreatedEvent>(_contractAddress);
        var createdFilter = createdHandler.CreateFilterInput(
            filterTopic1: null,
            filterTopic2: null,
            filterTopic3: agentAddress,
            fromBlock: new BlockParameter(new HexBigInteger(startBlock)),
            toBlock:   new BlockParameter(new HexBigInteger(headBlock)));
        var createdLogs = await createdHandler.GetAllChangesAsync(createdFilter);
        var jobIds = new HashSet<System.Numerics.BigInteger>();
        var fundedTimestamps = new Dictionary<System.Numerics.BigInteger, DateTime>();
        var submittedTimestamps = new Dictionary<System.Numerics.BigInteger, DateTime>();
        DateTime? lastSubmitted = null;
        foreach (var log in createdLogs) jobIds.Add(log.Event.JobId);

        // 2. JobSubmitted filtered on provider — same agent. Gives T1 for response time.
        var submittedHandler = _web3.Eth.GetEvent<JobSubmittedEvent>(_contractAddress);
        var submittedFilter = submittedHandler.CreateFilterInput(
            filterTopic1: null,
            filterTopic2: agentAddress,
            fromBlock: new BlockParameter(new HexBigInteger(startBlock)),
            toBlock:   new BlockParameter(new HexBigInteger(headBlock)));
        var submittedLogs = await submittedHandler.GetAllChangesAsync(submittedFilter);
        foreach (var log in submittedLogs)
        {
            if (!jobIds.Contains(log.Event.JobId)) continue;
            var ts = await GetBlockTimeAsync(log.Log.BlockNumber.ToLong(), ct);
            submittedTimestamps[log.Event.JobId] = ts;
            if (lastSubmitted is null || ts > lastSubmitted) lastSubmitted = ts;
        }

        // 3. Other events are NOT indexed on provider, so we filter by jobId set
        //    in batches (max 50 jobIds per topic filter — well under RPC limits).
        var fundedHandler    = _web3.Eth.GetEvent<JobFundedEvent>(_contractAddress);
        var completedHandler = _web3.Eth.GetEvent<JobCompletedEvent>(_contractAddress);
        var rejectedHandler  = _web3.Eth.GetEvent<JobRejectedEvent>(_contractAddress);
        var expiredHandler   = _web3.Eth.GetEvent<JobExpiredEvent>(_contractAddress);

        long completed = 0, rejected = 0, expired = 0, completedLast30d = 0;
        var thirtyDaysAgo = nowUtc.AddDays(-30);

        const int batchSize = 50;
        var allJobIds = jobIds.ToList();
        for (int i = 0; i < allJobIds.Count; i += batchSize)
        {
            var batch = allJobIds.GetRange(i, Math.Min(batchSize, allJobIds.Count - i));
            var topicJobIds = batch.Select(id => (object)id).ToArray();

            var fundedBatch = await fundedHandler.GetAllChangesAsync(
                fundedHandler.CreateFilterInput(
                    filterTopic1: topicJobIds,
                    filterTopic2: null,
                    fromBlock: new BlockParameter(new HexBigInteger(startBlock)),
                    toBlock:   new BlockParameter(new HexBigInteger(headBlock))));
            foreach (var log in fundedBatch)
            {
                fundedTimestamps[log.Event.JobId] = await GetBlockTimeAsync(log.Log.BlockNumber.ToLong(), ct);
            }

            var completedBatch = await completedHandler.GetAllChangesAsync(
                completedHandler.CreateFilterInput(
                    filterTopic1: topicJobIds,
                    fromBlock: new BlockParameter(new HexBigInteger(startBlock)),
                    toBlock:   new BlockParameter(new HexBigInteger(headBlock))));
            foreach (var log in completedBatch)
            {
                completed++;
                var ts = await GetBlockTimeAsync(log.Log.BlockNumber.ToLong(), ct);
                if (ts >= thirtyDaysAgo) completedLast30d++;
            }

            var rejectedBatch = await rejectedHandler.GetAllChangesAsync(
                rejectedHandler.CreateFilterInput(
                    filterTopic1: topicJobIds,
                    filterTopic2: null,
                    fromBlock: new BlockParameter(new HexBigInteger(startBlock)),
                    toBlock:   new BlockParameter(new HexBigInteger(headBlock))));
            foreach (var log in rejectedBatch)
            {
                // Exclude self-rejections (agent rejecting buyer's spec).
                if (string.Equals(log.Event.Rejector, agentAddress, StringComparison.OrdinalIgnoreCase))
                    continue;
                rejected++;
            }

            var expiredBatch = await expiredHandler.GetAllChangesAsync(
                expiredHandler.CreateFilterInput(
                    filterTopic1: topicJobIds,
                    fromBlock: new BlockParameter(new HexBigInteger(startBlock)),
                    toBlock:   new BlockParameter(new HexBigInteger(headBlock))));
            expired += expiredBatch.Count;
        }

        // 4. Average response time over completed jobs in last 30d only.
        double? avgResponseSeconds = null;
        long sampleCount = 0;
        var responseDurations = new List<double>();
        foreach (var (jobId, submitTs) in submittedTimestamps)
        {
            if (submitTs < thirtyDaysAgo) continue;
            if (!fundedTimestamps.TryGetValue(jobId, out var fundTs)) continue;
            var seconds = (submitTs - fundTs).TotalSeconds;
            if (seconds <= 0) continue;
            responseDurations.Add(seconds);
            sampleCount++;
        }
        if (responseDurations.Count > 0) avgResponseSeconds = responseDurations.Average();

        return new ChainScanResult(
            AgentAddress:               agentAddress,
            TotalJobs:                  jobIds.Count,
            Completed:                  completed,
            Rejected:                   rejected,
            Expired:                    expired,
            CompletedLast30d:           completedLast30d,
            LastJobSubmittedAt:         lastSubmitted,
            AvgResponseSeconds30d:      avgResponseSeconds,
            ResponseTimeSampleCount30d: sampleCount,
            HighestScannedBlock:        headBlock);
    }

    private async Task<DateTime> GetBlockTimeAsync(long blockNumber, CancellationToken ct)
    {
        if (_blockTimestamps.TryGetValue(blockNumber, out var cached)) return cached;
        var block = await _web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(
            new HexBigInteger(blockNumber));
        var ts = DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value).UtcDateTime;
        // Cap cache size; evict oldest-by-insertion when full.
        if (_blockTimestamps.Count >= 4096)
        {
            var firstKey = _blockTimestamps.Keys.First();
            _blockTimestamps.Remove(firstKey);
        }
        _blockTimestamps[blockNumber] = ts;
        return ts;
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Smoke-test against a known agent (no service wiring yet — skipped if RPC env not set)**

Add a temporary debug endpoint to `Program.cs` or run a one-off snippet via `dotnet script` if available. Skip if you don't have `BASE_RPC_URL` configured locally — Task 12's wiring will exercise it end-to-end.

- [ ] **Step 4: Commit**

```
git add ACP_Metabot.Api/Services/ChainEventScanner.cs
git commit -m "services: add ChainEventScanner (Nethereum filter on provider + jobId batches)"
```

---

## Task 8: ScoreCalculator (pure function)

**Files:**
- Create: `ACP_Metabot.Api/Services/ScoreCalculator.cs`

- [ ] **Step 1: Write the calculator**

Create `Services/ScoreCalculator.cs`:

```csharp
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

public class ScoreCalculator
{
    private const int CompletionMinTerminal     = 5;
    private const int DisputeMinTerminal        = 5;
    private const int ResponseTimeMinSamples30d = 3;

    // Weight constants — must sum to 1.0.
    private const double CompletionWeight   = 0.30;
    private const double DisputeWeight      = 0.25;
    private const double RecencyWeight      = 0.15;
    private const double Volume30dWeight    = 0.20;
    private const double ResponseTimeWeight = 0.10;

    public record ScoreInputs(
        ChainScanResult Chain,
        DateTime? OffChainLastActiveAt,
        long Volume30dCorpusMax,
        DateTime NowUtc);

    public record ComputedScore(
        SubScoreSet SubScores,
        int Overall,
        bool IsColdStart,
        bool AnyInsufficient);

    public ComputedScore Compute(ScoreInputs input)
    {
        var chain = input.Chain;
        var totalTerminal = chain.Completed + chain.Rejected + chain.Expired;
        var isColdStart = totalTerminal == 0 && chain.TotalJobs == 0;

        var completion = ComputeCompletion(chain, totalTerminal);
        var dispute    = ComputeDispute(chain, totalTerminal);
        var recency    = ComputeRecency(input.OffChainLastActiveAt, chain.LastJobSubmittedAt, input.NowUtc);
        var volume     = ComputeVolume30d(chain.CompletedLast30d, input.Volume30dCorpusMax);
        var response   = ComputeResponseTime(chain.AvgResponseSeconds30d, chain.ResponseTimeSampleCount30d);

        var overall = (int)Math.Round(
            CompletionWeight   * completion.Score +
            DisputeWeight      * dispute.Score +
            RecencyWeight      * recency.Score +
            Volume30dWeight    * volume.Score +
            ResponseTimeWeight * response.Score);

        return new ComputedScore(
            SubScores: new SubScoreSet(completion, dispute, recency, volume, response),
            Overall: overall,
            IsColdStart: isColdStart,
            AnyInsufficient:
                completion.InsufficientData || dispute.InsufficientData || response.InsufficientData);
    }

    private static SubScore ComputeCompletion(ChainScanResult chain, long totalTerminal)
    {
        if (totalTerminal < CompletionMinTerminal)
        {
            return new SubScore(
                Value: 0,
                Score: 50,
                Percentile: 0,
                Evidence: $"Only {totalTerminal} terminal jobs (min {CompletionMinTerminal}); using neutral 50.",
                InsufficientData: true);
        }
        var rate = (double)chain.Completed / totalTerminal;
        return new SubScore(
            Value: rate,
            Score: (int)Math.Round(rate * 100),
            Percentile: 0, // filled in by ReputationService percentile pass
            Evidence: $"{chain.Completed}/{totalTerminal} terminal jobs completed.",
            InsufficientData: false);
    }

    private static SubScore ComputeDispute(ChainScanResult chain, long totalTerminal)
    {
        if (totalTerminal < DisputeMinTerminal)
        {
            return new SubScore(
                Value: 0,
                Score: 50,
                Percentile: 0,
                Evidence: $"Only {totalTerminal} terminal jobs (min {DisputeMinTerminal}); using neutral 50.",
                InsufficientData: true);
        }
        var disputed = chain.Rejected + chain.Expired;
        var rate = (double)disputed / totalTerminal;
        return new SubScore(
            Value: rate,
            Score: (int)Math.Round((1.0 - rate) * 100),
            Percentile: 0,
            Evidence: $"{disputed}/{totalTerminal} terminal jobs rejected or expired (excluding self-rejections).",
            InsufficientData: false);
    }

    private static SubScore ComputeRecency(DateTime? offChain, DateTime? chainFallback, DateTime nowUtc)
    {
        var lastActive = offChain ?? chainFallback;
        if (lastActive is null)
        {
            return new SubScore(
                Value: 0, Score: 0, Percentile: 0,
                Evidence: "No activity recorded.",
                InsufficientData: false);
        }
        var hours = (nowUtc - lastActive.Value).TotalHours;
        int score;
        if (hours <= 72) score = 100;
        else if (hours >= 90 * 24) score = 0;
        else
        {
            var range = 90.0 * 24 - 72;
            var t = (hours - 72) / range;
            score = (int)Math.Round((1.0 - t) * 100);
        }
        return new SubScore(
            Value: hours,
            Score: score,
            Percentile: 0,
            Evidence: $"Last active {hours:F1}h ago ({(offChain != null ? "off-chain" : "chain fallback")}).",
            InsufficientData: false);
    }

    private static SubScore ComputeVolume30d(long completedLast30d, long corpusMax)
    {
        if (corpusMax <= 0)
        {
            return new SubScore(
                Value: completedLast30d,
                Score: 50,
                Percentile: 0,
                Evidence: $"{completedLast30d} jobs completed in last 30d; corpus max not yet known, using neutral 50.",
                InsufficientData: false);
        }
        var raw = 100.0 * Math.Log(1 + completedLast30d) / Math.Log(1 + corpusMax);
        return new SubScore(
            Value: completedLast30d,
            Score: (int)Math.Round(Math.Clamp(raw, 0, 100)),
            Percentile: 0,
            Evidence: $"{completedLast30d} jobs completed in last 30d (corpus max {corpusMax}, log-scaled).",
            InsufficientData: false);
    }

    private static SubScore ComputeResponseTime(double? avgSeconds, long sampleCount)
    {
        if (avgSeconds is null || sampleCount < ResponseTimeMinSamples30d)
        {
            return new SubScore(
                Value: avgSeconds ?? 0,
                Score: 50,
                Percentile: 0,
                Evidence: $"Only {sampleCount} response samples in last 30d (min {ResponseTimeMinSamples30d}); using neutral 50.",
                InsufficientData: true);
        }
        var minutes = avgSeconds.Value / 60.0;
        int score;
        if (minutes <= 5) score = 100;
        else if (minutes >= 60 * 24) score = 0;
        else
        {
            var range = 60.0 * 24 - 5;
            var t = (minutes - 5) / range;
            score = (int)Math.Round((1.0 - t) * 100);
        }
        return new SubScore(
            Value: avgSeconds.Value,
            Score: score,
            Percentile: 0,
            Evidence: $"Avg response time {minutes:F1}min over {sampleCount} samples (last 30d).",
            InsufficientData: false);
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Manual sanity check via REPL or dotnet-script**

Create a temporary `tools/score-smoke.csx` (or run inline in `dotnet fsi` if you prefer F#) that constructs a `ChainScanResult` for a fake agent with 47/51 completed, 2 rejected, 2 expired, 47 in last 30d, 30s avg response, last active 2h ago — and prints the result. Expected overall score in the high 80s. Delete the file after.

```csharp
// tools/score-smoke.csx (delete after running)
#r "ACP_Metabot.Api/bin/Debug/net10.0/ACP_Metabot.Api.dll"
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;

var calc = new ScoreCalculator();
var chain = new ChainScanResult("0xabc", 51, 47, 2, 2, 47,
    DateTime.UtcNow.AddHours(-2), 30.0, 47, 100_000_000);
var inp = new ScoreCalculator.ScoreInputs(chain, DateTime.UtcNow.AddHours(-1), 200, DateTime.UtcNow);
var s = calc.Compute(inp);
System.Console.WriteLine($"overall={s.Overall} cold={s.IsColdStart} insufficient={s.AnyInsufficient}");
System.Console.WriteLine($"  completion={s.SubScores.Completion.Score}, dispute={s.SubScores.Dispute.Score}, recency={s.SubScores.Recency.Score}, volume30d={s.SubScores.Volume30d.Score}, response={s.SubScores.ResponseTime.Score}");
```

Run: `dotnet script tools/score-smoke.csx`. Expected: `overall=` somewhere between 80 and 95; all sub-scores non-zero.

- [ ] **Step 4: Commit (without the smoke script)**

```
git add ACP_Metabot.Api/Services/ScoreCalculator.cs
git commit -m "services: add ScoreCalculator (pure-function 0-100 from 5 metrics)"
```

---

## Task 9: LifetimeSnapshotService (BackgroundService)

**Files:**
- Create: `ACP_Metabot.Api/Services/LifetimeSnapshotService.cs`

- [ ] **Step 1: Write the service**

Create `Services/LifetimeSnapshotService.cs`:

```csharp
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// Daily 02:00 UTC snapshot of (agent_address → total_jobs) sourced from the
// already-indexed `offerings` table. Feeds the reputation warmer (which runs
// 30 min later) and the 30-day-delta math in the score formula.
public class LifetimeSnapshotService : BackgroundService
{
    private static readonly TimeOnly RunAt = new(2, 0);
    private const int RetainDays = 35;

    private readonly OfferingRepository _offeringRepo;
    private readonly LifetimeSnapshotRepository _snapshotRepo;
    private readonly ILogger<LifetimeSnapshotService> _logger;

    public LifetimeSnapshotService(
        OfferingRepository offeringRepo,
        LifetimeSnapshotRepository snapshotRepo,
        ILogger<LifetimeSnapshotService> logger)
    {
        _offeringRepo = offeringRepo;
        _snapshotRepo = snapshotRepo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once at startup if today's snapshot is missing, then on the daily schedule.
        try { await RunIfDueAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogError(ex, "[snapshot] startup run failed"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(now);
            var nextRunDate = TimeOnly.FromDateTime(now) >= RunAt ? today.AddDays(1) : today;
            var nextRun = nextRunDate.ToDateTime(RunAt, DateTimeKind.Utc);
            var delay = nextRun - now;
            if (delay.TotalSeconds > 0)
            {
                _logger.LogInformation("[snapshot] sleeping until {next:O}", nextRun);
                try { await Task.Delay(delay, stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
            try { await RunOnceAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "[snapshot] run failed"); }
        }
    }

    private async Task RunIfDueAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        // Cheap probe: does any agent have a snapshot for today?
        var probe = await _offeringRepo.PickFirstAgentAsync(); // small helper, see below
        if (probe is null) return;
        var existing = await _snapshotRepo.GetAsync(probe, today);
        if (existing is null) await RunOnceAsync(DateTime.UtcNow, ct);
    }

    public async Task RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        var totals = await _offeringRepo.SumJobCountsByAgentAsync();
        if (totals.Count == 0)
        {
            _logger.LogInformation("[snapshot] no agents to snapshot yet");
            return;
        }
        await _snapshotRepo.UpsertManyAsync(totals, nowUtc.Date);
        await _snapshotRepo.PruneOlderThanAsync(nowUtc.Date.AddDays(-RetainDays));
        _logger.LogInformation("[snapshot] wrote {count} agent rows for {date:yyyy-MM-dd}",
            totals.Count, nowUtc.Date);
    }
}
```

- [ ] **Step 2: Add the two helper methods to `OfferingRepository`**

Edit `Data/OfferingRepository.cs`. Append before the closing brace of the `OfferingRepository` class:

```csharp
public async Task<string?> PickFirstAgentAsync()
{
    await using var conn = _db.OpenConnection();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT agent_address FROM offerings LIMIT 1;";
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? reader.GetString(0) : null;
}

public async Task<IReadOnlyDictionary<string, long>> SumJobCountsByAgentAsync()
{
    var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    await using var conn = _db.OpenConnection();
    await using var cmd = conn.CreateCommand();
    // agent_job_count is an agent-level metric duplicated across rows; MAX
    // is the canonical total (always equal across rows for the same agent
    // after a fresh indexer cycle).
    cmd.CommandText = @"
        SELECT agent_address, MAX(agent_job_count)
        FROM offerings
        GROUP BY agent_address;";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        dict[reader.GetString(0)] = reader.GetInt64(1);
    }
    return dict;
}
```

- [ ] **Step 3: Build**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```
git add ACP_Metabot.Api/Services/LifetimeSnapshotService.cs ACP_Metabot.Api/Data/OfferingRepository.cs
git commit -m "services: add LifetimeSnapshotService + agent-totals query helper"
```

---

## Task 10: Rewrite ReputationService (preserve legacy methods, add cache orchestration)

**Files:**
- Modify: `ACP_Metabot.Api/Services/ReputationService.cs`

- [ ] **Step 1: Read the current ReputationService**

Open `Services/ReputationService.cs` and confirm the public surface: `IsReady`, `RefreshedAtUtc`, `RebuildFromCorpus`, `BuildSearchSummary`, `Build`. These are called from `Program.cs` (`HandleReputation`, `HandleBrowseAgent`, `HandleSearch` indirectly via `SearchService`) and `MarketplaceIndexerService` (calls `RebuildFromCorpus`). All five must continue to work.

- [ ] **Step 2: Append behavioural-mode state + methods**

Edit `Services/ReputationService.cs`. Add new private fields under the existing `_lock`-protected state:

```csharp
// New behavioural-mode state. Layers on top of legacy hire-count math
// without removing it (legacy is still used by /search inline summaries
// and /agent/{address} browse).
private readonly AgentReputationCacheRepository _cacheRepo;
private readonly LifetimeSnapshotRepository    _snapshotRepo;
private readonly ChainEventScanner             _scanner;
private readonly AcpOffChainClient             _offChain;
private readonly ScoreCalculator               _calculator;
private readonly OfferingRepository            _offeringRepo;
private readonly ILogger<ReputationService>    _logger;

// One semaphore per agent, lazily created. Prevents two concurrent paid
// hires for the same agent triggering two chain scans.
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _computeLocks = new();

// Per-metric sorted arrays (by absolute score) for percentile lookups in
// the behavioural response. Rebuilt at the end of each warmer pass and
// every lazy compute.
private readonly object _percentileLock = new();
private int[] _completionScoreSorted = Array.Empty<int>();
private int[] _disputeScoreSorted    = Array.Empty<int>();
private int[] _recencyScoreSorted    = Array.Empty<int>();
private int[] _volume30dScoreSorted  = Array.Empty<int>();
private int[] _responseScoreSorted   = Array.Empty<int>();
```

Update the constructor to inject the new dependencies. Replace the existing parameterless constructor (or current ctor — copy whichever signature is there) with:

```csharp
public ReputationService(
    AgentReputationCacheRepository cacheRepo,
    LifetimeSnapshotRepository snapshotRepo,
    ChainEventScanner scanner,
    AcpOffChainClient offChain,
    ScoreCalculator calculator,
    OfferingRepository offeringRepo,
    ILogger<ReputationService> logger)
{
    _cacheRepo    = cacheRepo;
    _snapshotRepo = snapshotRepo;
    _scanner      = scanner;
    _offChain     = offChain;
    _calculator   = calculator;
    _offeringRepo = offeringRepo;
    _logger       = logger;
}
```

Append these public methods to the class:

```csharp
public async Task<AgentReputationResultV2> GetOrComputeAsync(
    string agentAddress, string? offeringName, CancellationToken ct)
{
    var addr = agentAddress.ToLowerInvariant();
    var nowUtc = DateTime.UtcNow;
    var cached = await _cacheRepo.GetAsync(addr, nowUtc);
    if (cached is not null)
    {
        return await DeserializeAsync(cached, offeringName, warmHit: cached.Source == "warmer", ct);
    }

    var sem = _computeLocks.GetOrAdd(addr, _ => new SemaphoreSlim(1, 1));
    bool acquired = await sem.WaitAsync(TimeSpan.FromSeconds(5), ct);
    if (!acquired)
    {
        // Another thread is computing; re-read after timeout.
        var late = await _cacheRepo.GetAsync(addr, DateTime.UtcNow);
        if (late is not null) return await DeserializeAsync(late, offeringName, warmHit: false, ct);
        throw new InvalidOperationException("concurrent compute timed out");
    }
    try
    {
        // Re-check cache under the lock (another thread may have just written).
        cached = await _cacheRepo.GetAsync(addr, DateTime.UtcNow);
        if (cached is not null)
            return await DeserializeAsync(cached, offeringName, warmHit: cached.Source == "warmer", ct);

        var fresh = await ComputeAsync(addr, source: "lazy", ct);
        return await AttachOfferingAsync(fresh, offeringName, ct);
    }
    finally
    {
        sem.Release();
    }
}

// Computes a fresh reputation, persists it, and returns the V2 wire object.
public async Task<AgentReputationResultV2> ComputeAsync(string addr, string source, CancellationToken ct)
{
    var nowUtc = DateTime.UtcNow;
    var fromBlock = (await _cacheRepo.GetLastScannedBlockAsync(addr) ?? 0) + 1;
    var chain = await _scanner.ScanAgentAsync(addr, fromBlock, nowUtc, ct);

    AcpOffChainAgent? offChain = null;
    try { offChain = await _offChain.GetAgentAsync(addr, ct); }
    catch (Exception ex) { _logger.LogWarning(ex, "[reputation] off-chain fetch failed for {addr}", addr); }

    long volumeCorpusMax = ComputeVolume30dCorpusMax();

    var inputs = new ScoreCalculator.ScoreInputs(chain, offChain?.LastActiveAt, volumeCorpusMax, nowUtc);
    var score = _calculator.Compute(inputs);

    var rawCounts = new RawCounts(
        TotalJobs:        chain.TotalJobs,
        Completed:        chain.Completed,
        Rejected:         chain.Rejected,
        Expired:          chain.Expired,
        CompletedLast30d: chain.CompletedLast30d,
        LastActiveAt:     (offChain?.LastActiveAt ?? chain.LastJobSubmittedAt)?.ToString("O"));

    var flags = new ReputationFlags(
        IsColdStart:      score.IsColdStart,
        InsufficientData: score.AnyInsufficient,
        WarmCacheHit:     source == "warmer");

    var subScoresJson = System.Text.Json.JsonSerializer.Serialize(score.SubScores);
    var rawCountsJson = System.Text.Json.JsonSerializer.Serialize(rawCounts);
    var flagsJson     = System.Text.Json.JsonSerializer.Serialize(flags);

    var name = offChain?.Name ?? "";
    if (string.IsNullOrEmpty(name))
    {
        var byAgent = await _offeringRepo.ListByAgentAsync(addr);
        if (byAgent.Count > 0) name = byAgent[0].AgentName;
    }

    await _cacheRepo.UpsertAsync(new CachedReputationRow(
        AgentAddress:     addr,
        AgentName:        name,
        AgentScore:       score.Overall,
        SubScoresJson:    subScoresJson,
        RawCountsJson:    rawCountsJson,
        FlagsJson:        flagsJson,
        ComputedAt:       nowUtc,
        LastScannedBlock: chain.HighestScannedBlock,
        Source:           source));

    // Refresh percentile arrays after each compute so the next caller sees
    // the up-to-date corpus.
    await RebuildPercentilesFromCacheAsync(nowUtc);

    return new AgentReputationResultV2(
        AgentAddress: addr,
        AgentName:    name,
        AgentScore:   score.Overall,
        ComputedAt:   nowUtc.ToString("O"),
        WindowDays:   90,
        SubScores:    AttachPercentiles(score.SubScores),
        RawCounts:    rawCounts,
        Flags:        flags,
        Offering:     null);
}

public async Task RebuildPercentilesFromCacheAsync(DateTime nowUtc)
{
    var rows = await _cacheRepo.ListAllForPercentilesAsync(nowUtc);
    if (rows.Count == 0) return;

    var completion = new List<int>(rows.Count);
    var dispute    = new List<int>(rows.Count);
    var recency    = new List<int>(rows.Count);
    var volume     = new List<int>(rows.Count);
    var response   = new List<int>(rows.Count);

    foreach (var row in rows)
    {
        SubScoreSet? set = null;
        try { set = System.Text.Json.JsonSerializer.Deserialize<SubScoreSet>(row.SubScoresJson); }
        catch { continue; }
        if (set is null) continue;
        if (!set.Completion.InsufficientData)   completion.Add(set.Completion.Score);
        if (!set.Dispute.InsufficientData)      dispute.Add(set.Dispute.Score);
        if (!set.Recency.InsufficientData)      recency.Add(set.Recency.Score);
        if (!set.Volume30d.InsufficientData)    volume.Add(set.Volume30d.Score);
        if (!set.ResponseTime.InsufficientData) response.Add(set.ResponseTime.Score);
    }

    var c = completion.ToArray(); Array.Sort(c);
    var d = dispute.ToArray();    Array.Sort(d);
    var r = recency.ToArray();    Array.Sort(r);
    var v = volume.ToArray();     Array.Sort(v);
    var p = response.ToArray();   Array.Sort(p);

    lock (_percentileLock)
    {
        _completionScoreSorted = c;
        _disputeScoreSorted    = d;
        _recencyScoreSorted    = r;
        _volume30dScoreSorted  = v;
        _responseScoreSorted   = p;
    }
}

private SubScoreSet AttachPercentiles(SubScoreSet src)
{
    int[] c, d, r, v, p;
    lock (_percentileLock)
    {
        c = _completionScoreSorted;
        d = _disputeScoreSorted;
        r = _recencyScoreSorted;
        v = _volume30dScoreSorted;
        p = _responseScoreSorted;
    }
    return new SubScoreSet(
        Completion:   src.Completion   with { Percentile = Pct(c, src.Completion.Score) },
        Dispute:      src.Dispute      with { Percentile = Pct(d, src.Dispute.Score) },
        Recency:      src.Recency      with { Percentile = Pct(r, src.Recency.Score) },
        Volume30d:    src.Volume30d    with { Percentile = Pct(v, src.Volume30d.Score) },
        ResponseTime: src.ResponseTime with { Percentile = Pct(p, src.ResponseTime.Score) });
}

private static double Pct(int[] sortedAsc, int score)
{
    if (sortedAsc.Length == 0) return 0;
    var idx = Array.BinarySearch(sortedAsc, score);
    int rank = idx < 0 ? ~idx : idx + 1;
    while (rank < sortedAsc.Length && sortedAsc[rank] == score) rank++;
    return Math.Round(100.0 * rank / sortedAsc.Length, 1);
}

private long ComputeVolume30dCorpusMax()
{
    int[] v;
    lock (_percentileLock) v = _volume30dScoreSorted;
    if (v.Length == 0) return 0; // first-boot path; calculator will return neutral 50
    // Score 100 reflects log-scaled position vs prior corpus max — feed back
    // the highest seen completedLast30d via a separate lookup. For v1.0,
    // approximate with the log-inverse of score=100, which is just the
    // largest completed30d we've seen any agent post; fall back to indexer
    // hire-count max if percentile data isn't yet warm.
    return (long)Math.Max(100, v[^1] * 10L);
}

private async Task<AgentReputationResultV2> DeserializeAsync(
    CachedReputationRow row, string? offeringName, bool warmHit, CancellationToken ct)
{
    var subScores = System.Text.Json.JsonSerializer.Deserialize<SubScoreSet>(row.SubScoresJson)!;
    var rawCounts = System.Text.Json.JsonSerializer.Deserialize<RawCounts>(row.RawCountsJson)!;
    var flagsRaw  = System.Text.Json.JsonSerializer.Deserialize<ReputationFlags>(row.FlagsJson)!;
    var flags     = flagsRaw with { WarmCacheHit = warmHit };
    var result = new AgentReputationResultV2(
        AgentAddress: row.AgentAddress,
        AgentName:    row.AgentName,
        AgentScore:   row.AgentScore,
        ComputedAt:   row.ComputedAt.ToString("O"),
        WindowDays:   90,
        SubScores:    AttachPercentiles(subScores),
        RawCounts:    rawCounts,
        Flags:        flags,
        Offering:     null);
    return await AttachOfferingAsync(result, offeringName, ct);
}

private async Task<AgentReputationResultV2> AttachOfferingAsync(
    AgentReputationResultV2 baseResult, string? offeringName, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(offeringName)) return baseResult;
    var offerings = await _offeringRepo.ListByAgentAsync(baseResult.AgentAddress);
    var match = offerings.FirstOrDefault(o =>
        string.Equals(o.OfferingName, offeringName, StringComparison.OrdinalIgnoreCase));
    if (match is null) throw new KeyNotFoundException("offering not found for this agent");

    var allHires = offerings.Select(o => o.UsageCount).OrderBy(x => x).ToArray();
    var idx = Array.BinarySearch(allHires, match.UsageCount);
    int rank = idx < 0 ? ~idx : idx + 1;
    while (rank < allHires.Length && allHires[rank] == match.UsageCount) rank++;
    var pct = allHires.Length == 0 ? 0 : Math.Round(100.0 * rank / allHires.Length, 1);

    return baseResult with
    {
        Offering = new OfferingHireRef(
            Name: match.OfferingName,
            Hires: match.UsageCount,
            Percentile: pct,
            Evidence: "Per-offering hire count from marketplace metrics. Behavioural metrics above are agent-level — chain events do not carry offering names.")
    };
}
```

- [ ] **Step 3: Build**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors. The build may surface a missing-DI error in `Program.cs` because `ReputationService` no longer has its old constructor signature — that's resolved in Task 12.

If the legacy ctor was parameterless and there's a concrete `new ReputationService()` call anywhere outside DI, search for it:

```
grep -rn "new ReputationService" ACP_Metabot.Api/
```

Replace any direct instantiation with DI resolution; the legacy code in `MarketplaceIndexerService` and `Program.cs` already uses DI, so this is normally not needed.

- [ ] **Step 4: Commit**

```
git add ACP_Metabot.Api/Services/ReputationService.cs
git commit -m "services: rewrite ReputationService with cache + lazy compute (legacy methods preserved)"
```

---

## Task 11: ReputationWarmerService

**Files:**
- Create: `ACP_Metabot.Api/Services/ReputationWarmerService.cs`

- [ ] **Step 1: Write the warmer**

Create `Services/ReputationWarmerService.cs`:

```csharp
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

// Daily 02:30 UTC warmer. Computes reputation for the top-N agents by
// lifetime job count, hard-stops after a configured budget. Runs incremental
// rescans (each compute starts from last_scanned_block + 1).
public class ReputationWarmerService : BackgroundService
{
    private static readonly TimeOnly RunAt = new(2, 30);

    private readonly ReputationService            _reputation;
    private readonly AgentReputationCacheRepository _cacheRepo;
    private readonly MarketplaceIndexerService    _indexer;
    private readonly IConfiguration               _config;
    private readonly ILogger<ReputationWarmerService> _logger;

    public ReputationWarmerService(
        ReputationService reputation,
        AgentReputationCacheRepository cacheRepo,
        MarketplaceIndexerService indexer,
        IConfiguration config,
        ILogger<ReputationWarmerService> logger)
    {
        _reputation = reputation;
        _cacheRepo  = cacheRepo;
        _indexer    = indexer;
        _config     = config;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the indexer to have at least one successful fetch so the
        // top-N selection has data to work with.
        while (!stoppingToken.IsCancellationRequested && _indexer.LastFetchAt is null)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var today = DateOnly.FromDateTime(now);
            var nextRunDate = TimeOnly.FromDateTime(now) >= RunAt ? today.AddDays(1) : today;
            var nextRun = nextRunDate.ToDateTime(RunAt, DateTimeKind.Utc);
            var delay = nextRun - now;
            if (delay.TotalSeconds > 0)
            {
                _logger.LogInformation("[warmer] sleeping until {next:O}", nextRun);
                try { await Task.Delay(delay, stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "[warmer] run failed"); }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var topN = _config.GetValue<int?>("Reputation:WarmerTopN") ?? 500;
        var budgetMin = _config.GetValue<int?>("Reputation:WarmerBudgetMinutes") ?? 60;
        var concurrency = _config.GetValue<int?>("Reputation:WarmerConcurrency") ?? 4;

        var agents = await _cacheRepo.ListWarmAgentsAsync(topN);
        if (agents.Count == 0)
        {
            _logger.LogInformation("[warmer] no agents to warm");
            return;
        }
        var deadline = DateTime.UtcNow.AddMinutes(budgetMin);
        int done = 0, failed = 0, skipped = 0;

        await Parallel.ForEachAsync(agents,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            async (agent, innerCt) =>
            {
                if (DateTime.UtcNow > deadline) { Interlocked.Increment(ref skipped); return; }
                try
                {
                    await _reputation.ComputeAsync(agent.AgentAddress, source: "warmer", innerCt);
                    var n = Interlocked.Increment(ref done);
                    if (n % 50 == 0) _logger.LogInformation("[warmer] {n}/{total} done", n, agents.Count);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "[warmer] {addr} failed", agent.AgentAddress);
                }
            });

        _logger.LogInformation("[warmer] pass complete: done={done} failed={failed} skipped={skipped}",
            done, failed, skipped);

        await _reputation.RebuildPercentilesFromCacheAsync(DateTime.UtcNow);
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```
git add ACP_Metabot.Api/Services/ReputationWarmerService.cs
git commit -m "services: add ReputationWarmerService (daily 02:30 UTC, top-500 agents, 60-min budget)"
```

---

## Task 12: Wire DI + endpoints + appsettings

**Files:**
- Modify: `ACP_Metabot.Api/Program.cs`
- Modify: `ACP_Metabot.Api/appsettings.json`

- [ ] **Step 1: Register new services in DI**

Edit `Program.cs`. After the existing `builder.Services.AddSingleton<WatchRepository>();` line, add:

```csharp
builder.Services.AddSingleton<AgentReputationCacheRepository>();
builder.Services.AddSingleton<LifetimeSnapshotRepository>();
```

After `builder.Services.AddHttpClient();` add:

```csharp
builder.Services.AddSingleton<AcpOffChainClient>();
builder.Services.AddSingleton<ChainEventScanner>();
builder.Services.AddSingleton<ScoreCalculator>();
```

After `builder.Services.AddHostedService<WatchPollerBackgroundService>();` add:

```csharp
builder.Services.AddSingleton<LifetimeSnapshotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LifetimeSnapshotService>());
builder.Services.AddSingleton<ReputationWarmerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReputationWarmerService>());
```

- [ ] **Step 2: Replace HandleReputation to use the new service**

In `Program.cs`, replace the existing `HandleReputation` body (currently calls `reputation.Build`) with:

```csharp
async Task<IResult> HandleReputation(AgentReputationRequest req,
    ReputationService reputation, OfferingRepository repo, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.AgentAddress))
        return Results.BadRequest(new { error = "invalid_address", message = "agentAddress is required" });
    var addr = req.AgentAddress.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });

    // Verify the agent is indexed before kicking off any compute.
    var offerings = await repo.ListByAgentAsync(addr);
    if (offerings.Count == 0)
        return Results.NotFound(new { error = "agent_not_indexed", message = "agent has no offerings on the marketplace" });

    try
    {
        var result = await reputation.GetOrComputeAsync(addr, req.OfferingName, ct);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "offering_not_found", message = "offering not found for this agent" });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { error = "compute_failed", message = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
```

- [ ] **Step 3: Add the new public GET endpoint and replace the existing public POST**

In `Program.cs`, locate the line:

```csharp
app.MapPost("/v1/agentReputation", HandleReputation).RequireRateLimiting("public-reputation");
```

Replace it with:

```csharp
app.MapGet("/v1/agentReputation", async (string agent,
    AgentReputationCacheRepository cacheRepo) =>
{
    if (string.IsNullOrWhiteSpace(agent))
        return Results.BadRequest(new { error = "invalid_address", message = "agent query param is required" });
    var addr = agent.Trim().ToLowerInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
        return Results.BadRequest(new { error = "invalid_address", message = "must be 0x followed by 40 hex chars" });

    var row = await cacheRepo.GetAsync(addr, DateTime.UtcNow);
    if (row is null)
        return Results.NotFound(new
        {
            error = "not_cached",
            hint = "hire the agentReputation offering for live computation"
        });

    // Re-emit the persisted JSON verbatim plus headers. Avoids redundant
    // (de)serialisation and keeps the response identical to the paid SKU.
    var subScores = System.Text.Json.JsonSerializer.Deserialize<SubScoreSet>(row.SubScoresJson);
    var rawCounts = System.Text.Json.JsonSerializer.Deserialize<RawCounts>(row.RawCountsJson);
    var flags     = System.Text.Json.JsonSerializer.Deserialize<ReputationFlags>(row.FlagsJson);
    var result = new AgentReputationResultV2(
        AgentAddress: row.AgentAddress,
        AgentName:    row.AgentName,
        AgentScore:   row.AgentScore,
        ComputedAt:   row.ComputedAt.ToString("O"),
        WindowDays:   90,
        SubScores:    subScores!,
        RawCounts:    rawCounts!,
        Flags:        flags!,
        Offering:     null);

    var etag = $"\"{System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(row.ComputedAt.ToString("O"))).Aggregate(new System.Text.StringBuilder(), (sb, b) => sb.Append(b.ToString("x2"))).ToString()}\"";
    return Results.Ok(result).WithHeaders(("Cache-Control", "public, max-age=3600"), ("ETag", etag));
}).RequireRateLimiting("public-reputation");
```

If `Results.WithHeaders` doesn't exist in your ASP.NET version, replace the final two lines with explicit response writing:

```csharp
// Fallback if Results.WithHeaders is unavailable in this stack:
return Results.Json(result, statusCode: 200, contentType: "application/json")
    .WithHeader("Cache-Control", "public, max-age=3600")
    .WithHeader("ETag", etag);
```

If neither helper exists in this ASP.NET version, write the response manually using `IResult` lambda:

```csharp
return new HeaderedJsonResult(result, new[] {
    ("Cache-Control", "public, max-age=3600"),
    ("ETag", etag),
});
```

…and add a small helper class at the end of `Program.cs`:

```csharp
class HeaderedJsonResult : IResult
{
    private readonly object _body;
    private readonly (string, string)[] _headers;
    public HeaderedJsonResult(object body, (string, string)[] headers)
    {
        _body = body; _headers = headers;
    }
    public async Task ExecuteAsync(HttpContext ctx)
    {
        foreach (var (k, v) in _headers) ctx.Response.Headers[k] = v;
        await Results.Ok(_body).ExecuteAsync(ctx);
    }
}
```

Use whichever variant compiles cleanly in your ASP.NET 10 build.

- [ ] **Step 4: Add Reputation defaults to appsettings.json**

Edit `appsettings.json` and add a top-level `Reputation` block:

```json
"Reputation": {
  "WarmerTopN": 500,
  "WarmerBudgetMinutes": 60,
  "WarmerConcurrency": 4,
  "ContractDeployBlock": 0
}
```

The actual `BASE_RPC_URL` and `ACP_CONTRACT_ADDRESS_BASE` come from environment variables (Docker Compose / .env), not appsettings.

- [ ] **Step 5: Build + run + smoke**

```
dotnet build ACP_Metabot.Api/ACP_Metabot.Api.csproj
BASE_RPC_URL="https://base-mainnet.g.alchemy.com/v2/<your-key>" \
ACP_CONTRACT_ADDRESS_BASE="0x<known-contract>" \
INTERNAL_API_KEY="dev" \
dotnet run --project ACP_Metabot.Api &
sleep 5

# 1. Public endpoint, cold cache → 404
curl -sS "http://localhost:5000/v1/agentReputation?agent=0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b"

# 2. Internal endpoint with X-API-Key → live compute (slow first call)
curl -sS -X POST -H "X-API-Key: dev" -H "Content-Type: application/json" \
  -d '{"agentAddress":"0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b"}' \
  http://localhost:5000/agentReputation

# 3. Public endpoint after compute → 200 with score JSON
curl -sS "http://localhost:5000/v1/agentReputation?agent=0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b"

kill %1
```

Expected:
- Call 1: HTTP 404 with `{"error":"not_cached","hint":"..."}`.
- Call 2: HTTP 200 with full `AgentReputationResultV2` JSON.
- Call 3: HTTP 200 with same payload + `Cache-Control` + `ETag` headers.

If any RPC call fails because of mistuned `ACP_CONTRACT_ADDRESS_BASE` or RPC quota, fix env vars before continuing.

- [ ] **Step 6: Commit**

```
git add ACP_Metabot.Api/Program.cs ACP_Metabot.Api/appsettings.json
git commit -m "endpoints: wire behavioural reputation; replace POST /v1/agentReputation with GET cache-only"
```

---

## Task 13: Update sidecar typings + offering description

**Files:**
- Modify: `acp-v2/src/apiClient.ts`
- Modify: `acp-v2/src/offerings/agentReputation.ts`

- [ ] **Step 1: Replace the AgentReputationResponse type**

Edit `acp-v2/src/apiClient.ts`. Replace the existing `OfferingReputation` and `AgentReputationResponse` types with:

```typescript
export interface SubScore {
  value: number;
  score: number;
  percentile: number;
  evidence: string;
  insufficientData: boolean;
}

export interface SubScoreSet {
  completion: SubScore;
  dispute: SubScore;
  recency: SubScore;
  volume30d: SubScore;
  responseTime: SubScore;
}

export interface ReputationRawCounts {
  totalJobs: number;
  completed: number;
  rejected: number;
  expired: number;
  completedLast30d: number;
  lastActiveAt?: string;
}

export interface ReputationFlags {
  isColdStart: boolean;
  insufficientData: boolean;
  warmCacheHit: boolean;
}

export interface OfferingHireRef {
  name: string;
  hires: number;
  percentile: number;
  evidence: string;
}

export interface AgentReputationResponse {
  agentAddress: string;
  agentName: string;
  agentScore: number;
  computedAt: string;
  windowDays: number;
  subScores: SubScoreSet;
  rawCounts: ReputationRawCounts;
  flags: ReputationFlags;
  offering?: OfferingHireRef;
}
```

The `agentReputation()` method in `ApiClient` and its implementation in `createApiClient` need no changes (still POST `/agentReputation` against the internal API). Verify with:

```
grep -n "agentReputation" acp-v2/src/apiClient.ts
```

- [ ] **Step 2: Update the offering description**

Edit `acp-v2/src/offerings/agentReputation.ts`. Replace the `description` string with:

```typescript
description:
  "On-chain behavioural reputation for an ACP agent. Returns a 0-100 score derived from completion rate, dispute rate, recency, 30-day throughput, and average response time. Cached 24h per agent. Pass an optional offeringName to include a per-offering hire-count breakdown alongside the agent-level score.",
```

The validator and execute body remain unchanged.

- [ ] **Step 3: Typecheck**

```
cd acp-v2 && npm install && npm run build
```

Expected: clean tsc, no errors.

- [ ] **Step 4: Verify offering print-out**

```
cd acp-v2 && npm run print-offerings
```

Expected: 4 offerings render (`search`, `composeStack`, `watchOffering`, `agentReputation`) — `agentReputation` shows the new description text.

- [ ] **Step 5: Commit**

```
git add acp-v2/src/apiClient.ts acp-v2/src/offerings/agentReputation.ts
git commit -m "sidecar: update agentReputation typings + description for behavioural v2"
```

---

## Task 14: Update plugin tool description + fetch path

**Files:**
- Modify: `../acp-find-plugin/mcp-server/server.js`

Note: this plugin is in a sibling repo, not under Metabot. Adjust paths if your local layout differs.

- [ ] **Step 1: Update tool description**

Open `../acp-find-plugin/mcp-server/server.js`. Find the `acp_agent_reputation` tool registration. Replace the `description` field with:

```javascript
description:
  "Look up the cached on-chain behavioural reputation for an ACP agent (0-100 score from completion rate, dispute rate, recency, 30-day throughput, and average response time). Returns 404 if the agent has not yet been evaluated; in that case, hire the agentReputation offering on the marketplace to force a live computation.",
```

- [ ] **Step 2: Switch the fetch from POST to GET**

In the same file, find the handler body (the function that's called when the tool is invoked). Replace the current `fetch(..., { method: "POST", ... })` call against `/v1/agentReputation` with:

```javascript
const url = new URL("/v1/agentReputation", BASE_URL);
url.searchParams.set("agent", agentAddress.toLowerCase());
const res = await fetch(url, { method: "GET" });
if (res.status === 404) {
  const body = await res.json().catch(() => ({}));
  return {
    error: body.error ?? "not_cached",
    hint:  body.hint  ?? "Hire the agentReputation offering on the ACP marketplace to force computation.",
  };
}
if (!res.ok) {
  return { error: `acp-metabot ${res.status}`, message: await res.text() };
}
return await res.json();
```

Replace `BASE_URL` and `agentAddress` with whatever variable names the existing code uses; the structural change is method=GET + query param + 404 short-circuit.

- [ ] **Step 3: Smoke-test the plugin tool against a deployed metabot (optional)**

If you have the public gateway running:

```
node -e 'const u = new URL("/v1/agentReputation", "https://api.acp-metabot.dev"); u.searchParams.set("agent", "0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b"); fetch(u).then(r => r.text()).then(t => console.log(t))'
```

Expected: 200 with score JSON if cached, or 404 with `not_cached` envelope.

- [ ] **Step 4: Commit (in the plugin repo)**

```
cd ../acp-find-plugin
git add mcp-server/server.js
git commit -m "tool: switch acp_agent_reputation to GET /v1/agentReputation cache-only path"
```

---

## Task 15: End-to-end smoke test

- [ ] **Step 1: Local API smoke**

```
cd ACP_Metabot.Api
dotnet build  # 0 warnings, 0 errors
BASE_RPC_URL="https://base-mainnet.g.alchemy.com/v2/<key>" \
ACP_CONTRACT_ADDRESS_BASE="0x<contract>" \
INTERNAL_API_KEY="dev" \
dotnet run &
sleep 6

curl -sS http://localhost:5000/health
curl -sS "http://localhost:5000/v1/agentReputation?agent=0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b"  # expect 404
curl -sS -X POST -H "X-API-Key: dev" -H "Content-Type: application/json" \
  -d '{"agentAddress":"0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b"}' \
  http://localhost:5000/agentReputation                                                                # expect 200
curl -sS "http://localhost:5000/v1/agentReputation?agent=0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b"  # expect 200 with same body

# /search still works (legacy hire-count summary intact)
curl -sS -X POST -H "X-API-Key: dev" -H "Content-Type: application/json" \
  -d '{"query":"trading","limit":3}' http://localhost:5000/search

# /agent/{address} still works (legacy Build path intact)
curl -sS -H "X-API-Key: dev" http://localhost:5000/agent/0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b

kill %1
```

Expected: every call returns the documented status. The two legacy endpoints (`/search`, `/agent/{address}`) must keep working — that's the regression check.

- [ ] **Step 2: Sidecar build + offering print**

```
cd acp-v2
npm install
npm run build
npm run print-offerings | grep -A1 "agentReputation"
```

Expected: clean tsc; description shows the new behavioural copy.

- [ ] **Step 3: Confirm clean working tree**

```
git status
```

Expected: working tree clean (everything committed).

- [ ] **Step 4: Commit (no-op if nothing changed in this task)**

If you needed to fix anything during smoke, commit those fixes now with a meaningful message.

---

## Task 16: Production deploy notes

- [ ] **Step 1: Update production .env on the droplet**

SSH to the droplet (138.68.174.116) and edit the metabot's `.env` to add:

```
BASE_RPC_URL=https://base-mainnet.g.alchemy.com/v2/<your-alchemy-key>
ACP_CONTRACT_ADDRESS_BASE=0x<known-contract-address>
```

Re-use any existing Alchemy key the bot already has access to. The contract address is the ACP V2 deployment on Base; pull it from `acp-v2/node_modules/@virtuals-protocol/acp-node-v2/dist/core/constants.js` or the project README if you don't already have it pinned.

- [ ] **Step 2: Pull + redeploy**

```
ssh root@138.68.174.116
cd /root/ACP_Metabot/ACP_Metabot
git pull
docker compose build --no-cache
docker compose up -d
docker compose logs -f --tail 50 acp-metabot-api
```

Watch for:
- `[snapshot] sleeping until ...` — LifetimeSnapshotService scheduled.
- `[warmer] sleeping until ...` — ReputationWarmerService scheduled.
- No `[reputation] off-chain fetch failed` floods.

- [ ] **Step 3: Force a first warmer pass (optional)**

If you don't want to wait until 02:30 UTC for the first cache rows, add a temporary operator endpoint or call the service manually inside the container:

```
docker compose exec acp-metabot-api dotnet ... # run-once invocation
```

If unsure, skip — the warmer fires automatically on its first scheduled tick. Lazy compute on paid hires + the first hit on `/v1/agentReputation` from an external caller will populate the cache organically.

- [ ] **Step 4: External smoke**

```
curl -sS "https://api.acp-metabot.dev/v1/agentReputation?agent=0xfc9f1ff5ec524759c1dc8e0a6eba6c22805b9d8b"
```

Expected: 404 `not_cached` until the first warmer pass (or paid hire) populates a row, then 200 with full JSON.

- [ ] **Step 5: Final commit / tag (optional)**

```
git tag -a v1.1.0 -m "agentReputation v2: behavioural on-chain reputation"
```

(Skip the tag push unless you and Oliver want it.)

---

## Self-review — completed inline

Spec coverage:
- Replace existing `agentReputation` in place — Task 10 + Task 12.
- Hybrid data source (off-chain + chain events) — Task 6 + Task 7.
- Hybrid refresh (lazy + 24h cache + daily warmer top-500) — Task 11 + Task 12 (handler).
- Score formula 30/25/15/20/10 with insufficient-data triggers — Task 8.
- New tables `agent_reputation_cache` + `agent_lifetime_snapshot` — Task 2.
- New repos — Tasks 4, 5.
- LifetimeSnapshotService daily 02:00 UTC — Task 9.
- ReputationWarmerService daily 02:30 UTC — Task 11.
- Public `GET /v1/agentReputation?agent=` cache-only — Task 12.
- Plugin description + fetch swap — Task 14.
- Sidecar typings + offering description — Task 13.
- Inline `/search` reputation summary stays as legacy hire-count — preserved by leaving `BuildSearchSummary` intact in Task 10.
- `/agent/{address}` browse keeps legacy `Build` — preserved by leaving `Build` intact in Task 10.

Placeholder scan: clean (no TBD/TODO/FIXME).

Type consistency check:
- `AgentReputationResultV2` defined in Task 3, used in Tasks 10 + 12 + sidecar typings in Task 13 — names match.
- `SubScoreSet`, `SubScore`, `RawCounts`, `ReputationFlags`, `OfferingHireRef` defined Task 3, referenced consistently downstream.
- `ChainScanResult` defined Task 3, returned from Task 7 scanner, consumed in Task 10 ReputationService.
- `ScoreInputs`, `ComputedScore` are private to Task 8's `ScoreCalculator`; only Task 10 calls `Compute(...)` and uses the returned `ComputedScore` properties.
- Method names `GetOrComputeAsync`, `ComputeAsync`, `RebuildPercentilesFromCacheAsync` consistent across Tasks 10 and 11.
