# Security-vetted `acp_today` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface each marketplace agent's SecurityBot verdict (score + grade + status) inside the `acp_today` / `/v1/digest` marketplace digest, sourced for free over the `acp-shared` Docker bridge.

**Architecture:** A default-OFF Metabot `SecurityScanWorker` (BackgroundService) calls SecurityBot's `POST /v1/internal/scan` with SecurityBot's `INTERNAL_API_KEY` (held by Metabot as `THESECURITYBOT_API_KEY`), caches each verdict in a new `security_verdicts` SQLite table, and the `/v1/digest` handler batch-joins those verdicts onto each offering as a `security` object. Scanning is fully decoupled from digest reads — a digest request never triggers a scan.

**Tech Stack:** C# / .NET 10, ADO.NET + `Microsoft.Data.Sqlite` (WAL), ASP.NET minimal APIs, `IHttpClientFactory`, xUnit. Node/JS only for the plugin `acp_today` description bump.

**Approved design:** `docs/superpowers/specs/2026-06-08-metabot-security-vetted-today-design.md`

**Standing constraints for the executor:**
- **No `git push`, no droplet deploy.** All commits are LOCAL-only. The ops/deploy steps are documented in Task 10 for a later, manual session.
- Worker ships **default-OFF** (`SECURITY_SCAN_ENABLED`, default false) — the cross-bot key must be wired before the env flip, exactly like `MarketplacePulseWorker` / `ResourcesEmbeddingsWorker`.
- Commit messages end with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Stage only the named files per commit — never `git add -A` (the repo carries unrelated WIP).

---

## File Structure

**Create:**
- `ACP_Metabot.Api/Models/SecurityVerdict.cs` — the cached-verdict row record + the public `OfferingSecurity` digest DTO.
- `ACP_Metabot.Api/Data/SecurityVerdictRepository.cs` — ADO.NET repo (Upsert / GetByAgent / GetMany / GetStaleAgents).
- `ACP_Metabot.Api/Services/AcpClients/InternalConnectCallbacks.cs` — P39 connect-time IP pin (lifted verbatim from PrivateTrader/OracleBot).
- `ACP_Metabot.Api/Services/TheSecurityBotClient.cs` — `ITheSecurityBotClient` + impl; maps `/v1/internal/scan` → `SecurityVerdict`, never throws into the worker loop.
- `ACP_Metabot.Api/Services/SecurityScanWorker.cs` — BackgroundService; batch-scan stale agents, gentle rate.
- `ACP_Metabot.Api.Tests/SecurityVerdictRepositoryTests.cs`
- `ACP_Metabot.Api.Tests/TheSecurityBotClientTests.cs`
- `ACP_Metabot.Api.Tests/SecurityScanWorkerTests.cs`
- `ACP_Metabot.Api.Tests/DigestServiceSecurityTests.cs`

**Modify:**
- `ACP_Metabot.Api/Data/Db.cs:619` — add the `security_verdicts` CREATE TABLE inside `InitializeSchemaAsync`.
- `ACP_Metabot.Api/Models/Digest.cs:5-18` — add `Security` to the `NewOffering` record.
- `ACP_Metabot.Api/Services/DigestService.cs` — inject `SecurityVerdictRepository?`; batch-join verdicts onto `newOfferings`; thread `includeSecurity` + the security cache-key segment.
- `ACP_Metabot.Api/Program.cs` — register the HttpClient (pinned handler), client, repo, worker; thread `includeSecurity` through `HandleDigest` + the `/v1/digest` MapGet.
- `ACP_Metabot.Api.Tests/DbMigrationTests.cs` (or `DbTests.cs`) — assert the new table exists after init.
- `acp-find-plugin/mcp-server/server.js` — `acp_today` tool `description` mentions the per-agent `security` field (LOCAL-only; needs a separate plugin republish to reach users).

**Why these boundaries:** each new file has one responsibility (model / persistence / cross-bot transport / scheduling). The digest enrichment is a minimal in-place change to an existing read path. The connect-callback is its own file so it can be reused by future Metabot cross-bot clients, mirroring how OracleBot/PrivateTrader already isolate it.

---

### Task 1: `security_verdicts` table

**Files:**
- Modify: `ACP_Metabot.Api/Data/Db.cs:615-620`
- Test: `ACP_Metabot.Api.Tests/DbMigrationTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `ACP_Metabot.Api.Tests/DbMigrationTests.cs` (a fresh-DB test class already exists there; if the file's pattern differs, mirror `ReputationFeedRepositoryTests`' temp-DB setup). Add this fact inside the existing test class:

```csharp
[Fact]
public async Task InitializeSchema_CreatesSecurityVerdictsTable()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_secverdict_schema_{Guid.NewGuid():N}.db");
    try
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={dbPath}"
            }).Build();
        var db = new Db(config);
        await db.InitializeSchemaAsync();

        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name='security_verdicts';";
        var name = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("security_verdicts", name);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { }
        try { File.Delete(dbPath + "-wal"); } catch { }
        try { File.Delete(dbPath + "-shm"); } catch { }
    }
}
```

If `DbMigrationTests.cs` lacks `using Microsoft.Extensions.Configuration;` / `using ACP_Metabot.Api.Data;`, add them at the top.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~InitializeSchema_CreatesSecurityVerdictsTable"`
(working dir: `C:\code_crypto\ACP\ACP_Metabot\ACP_Metabot`)
Expected: FAIL — query returns null (table does not exist).

- [ ] **Step 3: Add the table to the schema block**

In `ACP_Metabot.Api/Data/Db.cs`, inside `InitializeSchemaAsync`, append this table to the big `cmd.CommandText` SQL string — insert it immediately after the `risk_attest_pro_bootstrap_state` table (after its closing `);` on line 619, before the closing `";` on line 620):

```sql

            CREATE TABLE IF NOT EXISTS security_verdicts (
                agent_address    TEXT PRIMARY KEY,          -- lower-cased
                status           TEXT NOT NULL,             -- scanned | not_auditable | error
                score            INTEGER,
                grade            TEXT,
                observable_count INTEGER,
                finding_count    INTEGER,
                severity_counts  TEXT,                      -- json {severity: count}
                scanned_at       TEXT NOT NULL,             -- ISO-8601 round-trip ("O")
                corpus_version   TEXT,
                last_error       TEXT                       -- server-side only; never surfaced
            );

            CREATE INDEX IF NOT EXISTS ix_security_verdicts_status_scanned
                ON security_verdicts(status, scanned_at);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~InitializeSchema_CreatesSecurityVerdictsTable"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Data/Db.cs ACP_Metabot.Api.Tests/DbMigrationTests.cs
git commit -m "feat(metabot): add security_verdicts cache table"
```

---

### Task 2: Models — `SecurityVerdict` + `OfferingSecurity`

**Files:**
- Create: `ACP_Metabot.Api/Models/SecurityVerdict.cs`

No test of its own (a pure data record + mapper; exercised by Tasks 3, 5, 8). One trivial fact guards the mapper.

- [ ] **Step 1: Write the file**

Create `ACP_Metabot.Api/Models/SecurityVerdict.cs`:

```csharp
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

/// <summary>
/// One cached SecurityBot verdict per agent (the security_verdicts row).
/// Produced by TheSecurityBotClient.ScanAsync, persisted by
/// SecurityVerdictRepository, read by DigestService for the /v1/digest join.
/// `last_error` is server-side only and is NEVER surfaced on the public digest.
/// </summary>
public sealed record SecurityVerdict(
    string AgentAddress,          // PK, lower-cased
    string Status,                // SecurityStatus.* — scanned | not_auditable | error
    int? Score,
    string? Grade,
    int? ObservableCount,
    int? FindingCount,
    string? SeverityCountsJson,
    string ScannedAt,             // ISO-8601 "O"
    string? CorpusVersion,
    string? LastError);

public static class SecurityStatus
{
    public const string Scanned      = "scanned";
    public const string NotAuditable = "not_auditable";
    public const string Error        = "error";
    public const string Pending      = "pending"; // synthetic — no row yet
}

/// <summary>
/// Public, minimal projection attached to each offering in the digest. Only
/// score / grade / status / scannedAt — never raw findings, evidence, or
/// last_error (P9 / P10).
/// </summary>
public record OfferingSecurity(
    [property: JsonPropertyName("status")]    string Status,
    [property: JsonPropertyName("score"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? Score,
    [property: JsonPropertyName("grade"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Grade,
    [property: JsonPropertyName("scannedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ScannedAt)
{
    /// <summary>Synthetic verdict for an agent with no cached row yet.</summary>
    public static readonly OfferingSecurity Pending =
        new(SecurityStatus.Pending, null, null, null);

    /// <summary>Map a cached row to its public projection (drops last_error etc.).</summary>
    public static OfferingSecurity FromVerdict(SecurityVerdict v) =>
        new(v.Status, v.Score, v.Grade, v.ScannedAt);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build ACP_Metabot.Api`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add ACP_Metabot.Api/Models/SecurityVerdict.cs
git commit -m "feat(metabot): add SecurityVerdict + OfferingSecurity models"
```

---

### Task 3: `SecurityVerdictRepository`

**Files:**
- Create: `ACP_Metabot.Api/Data/SecurityVerdictRepository.cs`
- Test: `ACP_Metabot.Api.Tests/SecurityVerdictRepositoryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ACP_Metabot.Api.Tests/SecurityVerdictRepositoryTests.cs`:

```csharp
using System.Globalization;
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class SecurityVerdictRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;

    public SecurityVerdictRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_secverdict_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new SecurityVerdictRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private static SecurityVerdict Scanned(string addr, string scannedAtIso, int score = 90) =>
        new(addr, SecurityStatus.Scanned, score, "A", 11, 0, "{}", scannedAtIso, "2026-06-08", null);

    // Insert a minimal active offering so GetStaleAgents' candidate join finds the agent.
    private async Task InsertOfferingAsync(string agentAddress, string lastSeenIso, long usageCount = 0)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings
                (agent_address, agent_name, offering_name, description, price_usdc,
                 price_type, chain, content_hash, first_seen_at, last_seen_at, usage_count, is_removed)
            VALUES ($a, 'n', 'o-' || $a, 'd', 1.0, 'per_call', 'base', $a || $ls, $ls, $ls, $u, 0);";
        cmd.Parameters.AddWithValue("$a", agentAddress);
        cmd.Parameters.AddWithValue("$ls", lastSeenIso);
        cmd.Parameters.AddWithValue("$u", usageCount);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Upsert_ThenGetByAgent_Roundtrips_AndLowercases()
    {
        var iso = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        await _repo.UpsertAsync(Scanned("0xABCDEF", iso));

        var row = await _repo.GetByAgentAsync("0xabcdef");
        Assert.NotNull(row);
        Assert.Equal("0xabcdef", row!.AgentAddress); // lower-cased on write
        Assert.Equal(SecurityStatus.Scanned, row.Status);
        Assert.Equal(90, row.Score);
        Assert.Equal("A", row.Grade);
    }

    [Fact]
    public async Task Upsert_Twice_OverwritesNotDuplicates()
    {
        var iso1 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        var iso2 = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc).ToString("O");
        await _repo.UpsertAsync(Scanned("0xa", iso1, score: 70));
        await _repo.UpsertAsync(Scanned("0xa", iso2, score: 95));

        var row = await _repo.GetByAgentAsync("0xa");
        Assert.Equal(95, row!.Score);
        Assert.Equal(iso2, row.ScannedAt);
    }

    [Fact]
    public async Task GetMany_ReturnsOnlyRequested_KeyedLowercase()
    {
        var iso = DateTime.UtcNow.ToString("O");
        await _repo.UpsertAsync(Scanned("0xa", iso));
        await _repo.UpsertAsync(Scanned("0xb", iso));

        var map = await _repo.GetManyAsync(new[] { "0xA", "0xC" });
        Assert.True(map.ContainsKey("0xa"));
        Assert.False(map.ContainsKey("0xb"));
        Assert.False(map.ContainsKey("0xc"));
    }

    [Fact]
    public async Task GetMany_EmptyInput_ReturnsEmpty()
    {
        var map = await _repo.GetManyAsync(Array.Empty<string>());
        Assert.Empty(map);
    }

    [Fact]
    public async Task GetStaleAgents_NeverScanned_Selected_HighestHiresFirst()
    {
        var now = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
        var seen = now.AddDays(-1).ToString("O");
        await InsertOfferingAsync("0xlow", seen, usageCount: 1);
        await InsertOfferingAsync("0xhigh", seen, usageCount: 100);

        var stale = await _repo.GetStaleAgentsAsync(
            now, activeWindowDays: 30,
            scannedTtl: TimeSpan.FromDays(7),
            notAuditableTtl: TimeSpan.FromDays(30),
            errorTtl: TimeSpan.FromHours(6),
            limit: 10);

        Assert.Equal(new[] { "0xhigh", "0xlow" }, stale); // never-scanned, higher traction first
    }

    [Fact]
    public async Task GetStaleAgents_FreshScanned_Excluded_StaleScanned_Included()
    {
        var now = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
        var seen = now.AddDays(-1).ToString("O");
        await InsertOfferingAsync("0xfresh", seen);
        await InsertOfferingAsync("0xstale", seen);
        await _repo.UpsertAsync(Scanned("0xfresh", now.AddDays(-1).ToString("O")));   // 1 day old < 7d TTL
        await _repo.UpsertAsync(Scanned("0xstale", now.AddDays(-10).ToString("O")));  // 10 days old > 7d TTL

        var stale = await _repo.GetStaleAgentsAsync(
            now, 30, TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromHours(6), 10);

        Assert.Contains("0xstale", stale);
        Assert.DoesNotContain("0xfresh", stale);
    }

    [Fact]
    public async Task GetStaleAgents_ErrorRow_ShortTtl_RetriesSooner()
    {
        var now = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
        var seen = now.AddDays(-1).ToString("O");
        await InsertOfferingAsync("0xerr", seen);
        await _repo.UpsertAsync(new SecurityVerdict(
            "0xerr", SecurityStatus.Error, null, null, null, null, null,
            now.AddHours(-7).ToString("O"), null, "HTTP 500")); // 7h old > 6h error TTL

        var stale = await _repo.GetStaleAgentsAsync(
            now, 30, TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromHours(6), 10);

        Assert.Contains("0xerr", stale);
    }

    [Fact]
    public async Task GetStaleAgents_RespectsLimit()
    {
        var now = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);
        var seen = now.AddDays(-1).ToString("O");
        for (int i = 0; i < 5; i++) await InsertOfferingAsync($"0x{i}", seen);

        var stale = await _repo.GetStaleAgentsAsync(
            now, 30, TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromHours(6), limit: 2);

        Assert.Equal(2, stale.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityVerdictRepositoryTests"`
Expected: FAIL to compile — `SecurityVerdictRepository` does not exist.

- [ ] **Step 3: Write the repository**

Create `ACP_Metabot.Api/Data/SecurityVerdictRepository.cs`:

```csharp
using System.Globalization;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Data;

/// <summary>
/// Persistence for cached SecurityBot verdicts (the security_verdicts table).
/// Written by SecurityScanWorker, read by DigestService for the /v1/digest
/// security join. Addresses are stored + queried lower-cased.
/// </summary>
public sealed class SecurityVerdictRepository
{
    private readonly Db _db;

    public SecurityVerdictRepository(Db db) => _db = db;

    public async Task UpsertAsync(SecurityVerdict v, CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO security_verdicts
                (agent_address, status, score, grade, observable_count, finding_count,
                 severity_counts, scanned_at, corpus_version, last_error)
            VALUES ($a, $st, $sc, $gr, $oc, $fc, $sevc, $at, $cv, $err)
            ON CONFLICT(agent_address) DO UPDATE SET
                status           = excluded.status,
                score            = excluded.score,
                grade            = excluded.grade,
                observable_count = excluded.observable_count,
                finding_count    = excluded.finding_count,
                severity_counts  = excluded.severity_counts,
                scanned_at       = excluded.scanned_at,
                corpus_version   = excluded.corpus_version,
                last_error       = excluded.last_error;";
        cmd.Parameters.AddWithValue("$a",    v.AgentAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$st",   v.Status);
        cmd.Parameters.AddWithValue("$sc",   (object?)v.Score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gr",   (object?)v.Grade ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$oc",   (object?)v.ObservableCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fc",   (object?)v.FindingCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sevc", (object?)v.SeverityCountsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at",   v.ScannedAt);
        cmd.Parameters.AddWithValue("$cv",   (object?)v.CorpusVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err",  (object?)v.LastError ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SecurityVerdict?> GetByAgentAsync(string agentAddress, CancellationToken ct = default)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, status, score, grade, observable_count, finding_count,
                   severity_counts, scanned_at, corpus_version, last_error
            FROM security_verdicts WHERE agent_address = $a;";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return Map(reader);
    }

    /// <summary>Verdicts for the given addresses, keyed by lower-cased address.</summary>
    public async Task<IReadOnlyDictionary<string, SecurityVerdict>> GetManyAsync(
        IReadOnlyCollection<string> agentAddresses, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SecurityVerdict>(StringComparer.Ordinal);
        if (agentAddresses.Count == 0) return result;

        var lowered = agentAddresses.Select(a => a.ToLowerInvariant()).Distinct().ToList();
        var paramNames = lowered.Select((_, i) => $"$a{i}").ToArray();

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT agent_address, status, score, grade, observable_count, finding_count,
                   severity_counts, scanned_at, corpus_version, last_error
            FROM security_verdicts
            WHERE agent_address IN ({string.Join(",", paramNames)});";
        for (int i = 0; i < lowered.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], lowered[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var v = Map(reader);
            result[v.AgentAddress] = v;
        }
        return result;
    }

    /// <summary>
    /// Active marketplace agents (≥1 non-removed offering seen within
    /// <paramref name="activeWindowDays"/>) whose verdict is missing or past its
    /// status-specific TTL. Never-scanned agents rank first, then by descending
    /// hire count, then oldest verdict first. Capped at <paramref name="limit"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetStaleAgentsAsync(
        DateTime nowUtc, int activeWindowDays,
        TimeSpan scannedTtl, TimeSpan notAuditableTtl, TimeSpan errorTtl,
        int limit, CancellationToken ct = default)
    {
        string Iso(DateTime d) => d.ToString("O", CultureInfo.InvariantCulture);
        var activeCutoff       = Iso(nowUtc.AddDays(-activeWindowDays));
        var scannedCutoff      = Iso(nowUtc.Subtract(scannedTtl));
        var notAuditableCutoff = Iso(nowUtc.Subtract(notAuditableTtl));
        var errorCutoff        = Iso(nowUtc.Subtract(errorTtl));

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT o.addr
            FROM (
                SELECT LOWER(agent_address) AS addr,
                       SUM(usage_count)     AS hires
                FROM offerings
                WHERE is_removed = 0 AND last_seen_at >= $activeCutoff
                GROUP BY LOWER(agent_address)
            ) o
            LEFT JOIN security_verdicts v ON v.agent_address = o.addr
            WHERE v.agent_address IS NULL
               OR (v.status = 'scanned'        AND v.scanned_at < $scannedCutoff)
               OR (v.status = 'not_auditable'  AND v.scanned_at < $notAuditableCutoff)
               OR (v.status = 'error'          AND v.scanned_at < $errorCutoff)
            ORDER BY (v.scanned_at IS NULL) DESC, o.hires DESC, v.scanned_at ASC
            LIMIT $limit;";
        cmd.Parameters.AddWithValue("$activeCutoff",       activeCutoff);
        cmd.Parameters.AddWithValue("$scannedCutoff",      scannedCutoff);
        cmd.Parameters.AddWithValue("$notAuditableCutoff", notAuditableCutoff);
        cmd.Parameters.AddWithValue("$errorCutoff",        errorCutoff);
        cmd.Parameters.AddWithValue("$limit",              limit);

        var list = new List<string>(capacity: limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    private static SecurityVerdict Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        AgentAddress:       r.GetString(0),
        Status:             r.GetString(1),
        Score:              r.IsDBNull(2) ? null : r.GetInt32(2),
        Grade:              r.IsDBNull(3) ? null : r.GetString(3),
        ObservableCount:    r.IsDBNull(4) ? null : r.GetInt32(4),
        FindingCount:       r.IsDBNull(5) ? null : r.GetInt32(5),
        SeverityCountsJson: r.IsDBNull(6) ? null : r.GetString(6),
        ScannedAt:          r.GetString(7),
        CorpusVersion:      r.IsDBNull(8) ? null : r.GetString(8),
        LastError:          r.IsDBNull(9) ? null : r.GetString(9));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityVerdictRepositoryTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Data/SecurityVerdictRepository.cs ACP_Metabot.Api.Tests/SecurityVerdictRepositoryTests.cs
git commit -m "feat(metabot): SecurityVerdictRepository (upsert/get/getMany/stale)"
```

---

### Task 4: P39 connect-callback (lift into Metabot)

**Files:**
- Create: `ACP_Metabot.Api/Services/AcpClients/InternalConnectCallbacks.cs`

No new test (lifted verbatim from two deployed bots; compile-checked here, exercised live).

- [ ] **Step 1: Create the file**

Create `ACP_Metabot.Api/Services/AcpClients/InternalConnectCallbacks.cs` (verbatim from `ACP_PrivateTrader/PrivateTrader/Services/AcpClients/InternalConnectCallbacks.cs`, namespace changed):

```csharp
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ACP_Metabot.Api.Services.AcpClients;

/// P39: SocketsHttpHandler.ConnectCallback for INTERNAL, X-API-Key-bearing
/// cross-bot HttpClients. Without a connect-time pin + no-redirect, a post-boot
/// DNS-rebind of an internal hostname (or a 3xx from a compromised peer) could
/// bounce a key-bearing request to cloud-metadata / link-local and exfiltrate
/// the key.
///
/// Re-resolves at every physical connect and pins the socket to the resolved
/// address. RFC1918 / loopback are PERMITTED (the acp-shared docker peers) as
/// are public IPs; only the genuinely dangerous rebind targets are blocked —
/// link-local incl. 169.254.169.254 cloud-metadata, 0.0.0.0/8, multicast,
/// reserved, IPv6 variants. Lifted from ACP_OracleBot / ACP_PrivateTrader.
public static class InternalConnectCallbacks
{
    public static async ValueTask<Stream> PinResolvedIp(
        SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        if (addresses.Length == 0)
            throw new HttpRequestException($"no addresses resolved for {host}");

        Exception? lastError = null;
        foreach (var addr in addresses)
        {
            if (IsDangerous(addr))
            {
                lastError = new HttpRequestException(
                    $"internal connect target {addr} blocked (metadata/link-local/reserved)");
                continue;
            }
            try
            {
                var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addr, port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new HttpRequestException($"no usable address for {host}");
    }

    private static bool IsDangerous(IPAddress addr)
    {
        if (addr.IsIPv6LinkLocal) return true;
        if (addr.IsIPv6Multicast) return true;
        if (IPAddress.IPv6Any.Equals(addr)) return true;

        if (addr.AddressFamily == AddressFamily.InterNetwork || addr.IsIPv4MappedToIPv6)
        {
            var b = addr.MapToIPv4().GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return true; // link-local + cloud metadata 169.254.169.254
            if (b[0] == 0) return true;                  // 0.0.0.0/8 "this host"
            if ((b[0] & 0xf0) == 0xe0) return true;      // multicast 224/4
            if ((b[0] & 0xf0) == 0xf0) return true;      // reserved 240/4
        }
        return false;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build ACP_Metabot.Api`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add ACP_Metabot.Api/Services/AcpClients/InternalConnectCallbacks.cs
git commit -m "feat(metabot): add P39 InternalConnectCallbacks for cross-bot clients"
```

---

### Task 5: `TheSecurityBotClient`

**Files:**
- Create: `ACP_Metabot.Api/Services/TheSecurityBotClient.cs`
- Test: `ACP_Metabot.Api.Tests/TheSecurityBotClientTests.cs`

The client maps `/v1/internal/scan` responses to a `SecurityVerdict` and NEVER throws into the worker loop (a non-2xx / transport failure becomes `status=error`). The status discriminator is the upstream `verdict` field: `"NOT_AUDITABLE"` → `not_auditable`; otherwise → `scanned`.

- [ ] **Step 1: Write the failing tests**

Create `ACP_Metabot.Api.Tests/TheSecurityBotClientTests.cs`. Uses a stub `HttpMessageHandler` + a stub `IHttpClientFactory` so no socket is opened.

```csharp
using System.Net;
using System.Text;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class TheSecurityBotClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public HttpRequestMessage? LastRequest;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            // Force the content (and any X-API-Key header) to be materialised.
            if (request.Content is not null) await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://securitybot-api:5000/")
            };
    }

    private static TheSecurityBotClient Make(StubHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TheSecurityBot:ApiKey"]  = "test-key",
                ["TheSecurityBot:BaseUrl"] = "http://securitybot-api:5000/",
            }).Build();
        var env = new FakeEnv("Production");
        return new TheSecurityBotClient(new StubFactory(handler), config, env, NullLogger<TheSecurityBotClient>.Instance);
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public FakeEnv(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public async Task ScanAsync_Scanned_MapsScoreGradeAndCounts()
    {
        const string body = """
        {"agentAddress":"0xA","verdict":"PASS","score":88,"grade":"B",
         "observableCount":11,"totalPatterns":74,"scannedAt":"2026-06-08T10:00:00.0000000Z",
         "findings":[{"severity":"Medium"},{"severity":"High"},{"severity":"High"}]}
        """;
        var client = Make(new StubHandler(HttpStatusCode.OK, body));

        var v = await client.ScanAsync("0xA", default);

        Assert.Equal(SecurityStatus.Scanned, v.Status);
        Assert.Equal(88, v.Score);
        Assert.Equal("B", v.Grade);
        Assert.Equal(11, v.ObservableCount);
        Assert.Equal(3, v.FindingCount);
        Assert.Contains("\"High\":2", v.SeverityCountsJson);
        Assert.Equal("2026-06-08T10:00:00.0000000Z", v.ScannedAt);
        Assert.Null(v.LastError);
    }

    [Fact]
    public async Task ScanAsync_NotAuditable_MapsStatus()
    {
        const string body = """
        {"agentAddress":"0xA","verdict":"NOT_AUDITABLE","reason":"no http surface"}
        """;
        var client = Make(new StubHandler(HttpStatusCode.OK, body));

        var v = await client.ScanAsync("0xA", default);

        Assert.Equal(SecurityStatus.NotAuditable, v.Status);
        Assert.Null(v.Score);
        Assert.Null(v.Grade);
    }

    [Fact]
    public async Task ScanAsync_Non2xx_MapsToError_NoThrow()
    {
        var client = Make(new StubHandler(HttpStatusCode.InternalServerError, "{\"error\":\"INTERNAL_ERROR\"}"));

        var v = await client.ScanAsync("0xA", default);

        Assert.Equal(SecurityStatus.Error, v.Status);
        Assert.NotNull(v.LastError);
        Assert.DoesNotContain("INTERNAL_ERROR", v.LastError!); // raw upstream body not echoed
    }

    [Fact]
    public async Task ScanAsync_SendsApiKeyHeader_AndAgentAddressBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"verdict\":\"PASS\",\"score\":90,\"grade\":\"A\",\"observableCount\":11,\"findings\":[]}");
        var client = Make(handler);

        await client.ScanAsync("0xABC", default);

        Assert.True(handler.LastRequest!.Headers.Contains("X-API-Key"));
        Assert.Equal("test-key", string.Join("", handler.LastRequest.Headers.GetValues("X-API-Key")));
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.EndsWith("v1/internal/scan", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public void Ctor_NonDev_KeyMissingButBaseUrlSet_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TheSecurityBot:ApiKey"]  = "",
                ["TheSecurityBot:BaseUrl"] = "http://securitybot-api:5000/",
            }).Build();
        var env = new FakeEnv("Production");
        Assert.Throws<InvalidOperationException>(() =>
            new TheSecurityBotClient(new StubFactory(new StubHandler(HttpStatusCode.OK, "{}")),
                config, env, NullLogger<TheSecurityBotClient>.Instance));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~TheSecurityBotClientTests"`
Expected: FAIL to compile — `TheSecurityBotClient` does not exist.

- [ ] **Step 3: Write the client**

Create `ACP_Metabot.Api/Services/TheSecurityBotClient.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// Cross-bot contract for ACP_SecurityBot's internal scan endpoint. Extracted so
/// SecurityScanWorker tests can substitute a fake without an HttpClient.
/// </summary>
public interface ITheSecurityBotClient
{
    /// <summary>
    /// Scan the target agent over acp-shared. Returns a SecurityVerdict ready to
    /// upsert. NEVER throws — a non-2xx / transport / parse failure maps to
    /// status=error so the worker loop continues.
    /// </summary>
    Task<SecurityVerdict> ScanAsync(string agentAddress, CancellationToken ct = default);
}

/// <summary>
/// HTTP client for ACP_SecurityBot's <c>POST /v1/internal/scan</c> over the
/// <c>acp-shared</c> bridge. Auth: <c>X-API-Key</c> = SecurityBot's
/// <c>INTERNAL_API_KEY</c>, exposed in Metabot's env as
/// <c>THESECURITYBOT_API_KEY</c> (mapped to config <c>TheSecurityBot:ApiKey</c>
/// in docker-compose). The scan is FREE — no ACP escrow.
/// </summary>
public sealed class TheSecurityBotClient : ITheSecurityBotClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<TheSecurityBotClient> _log;

    public TheSecurityBotClient(IHttpClientFactory httpFactory, IConfiguration config,
        IHostEnvironment env, ILogger<TheSecurityBotClient> log)
    {
        _httpFactory = httpFactory;
        _apiKey = config["TheSecurityBot:ApiKey"] ?? "";
        _log = log;

        // P17: fail-fast in non-Development when BaseUrl is set but the key is
        // empty (silent-401 closer), matching TheChainlinkBotClient.
        var baseUrl = config["TheSecurityBot:BaseUrl"] ?? "";
        var integrationEnabled = !string.IsNullOrEmpty(baseUrl);
        if (integrationEnabled && string.IsNullOrEmpty(_apiKey) && !env.IsDevelopment())
        {
            throw new InvalidOperationException(
                "TheSecurityBot:ApiKey is required in non-Development when " +
                "TheSecurityBot:BaseUrl is set. Set both env vars in lock-step, " +
                "or unset BaseUrl to disable the cross-bot integration.");
        }
    }

    public async Task<SecurityVerdict> ScanAsync(string agentAddress, CancellationToken ct = default)
    {
        var addr = agentAddress.ToLowerInvariant();
        var nowIso = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        try
        {
            var http = _httpFactory.CreateClient("thesecuritybot");
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/internal/scan")
            {
                Content = JsonContent.Create(new { agentAddress })
            };
            req.Headers.Add("X-API-Key", _apiKey);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Log the status only — never the raw upstream body (P30/P63).
                _log.LogWarning("[thesecuritybot] /v1/internal/scan {Addr} -> {Status}",
                    addr, (int)resp.StatusCode);
                return Error(addr, nowIso, $"HTTP {(int)resp.StatusCode}");
            }

            var dto = await resp.Content.ReadFromJsonAsync<ScanResponseDto>(cancellationToken: ct);
            if (dto is null) return Error(addr, nowIso, "empty response");

            if (string.Equals(dto.Verdict, "NOT_AUDITABLE", StringComparison.OrdinalIgnoreCase))
            {
                return new SecurityVerdict(addr, SecurityStatus.NotAuditable,
                    null, null, null, null, null, nowIso, null, null);
            }

            var findings = dto.Findings ?? new List<ScanFindingDto>();
            var sevCounts = findings
                .Where(f => !string.IsNullOrWhiteSpace(f.Severity))
                .GroupBy(f => f.Severity!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count());

            return new SecurityVerdict(
                AgentAddress:       addr,
                Status:             SecurityStatus.Scanned,
                Score:              dto.Score,
                Grade:              dto.Grade,
                ObservableCount:    dto.ObservableCount,
                FindingCount:       findings.Count,
                SeverityCountsJson: JsonSerializer.Serialize(sevCounts),
                ScannedAt:          string.IsNullOrEmpty(dto.ScannedAt) ? nowIso : dto.ScannedAt!,
                CorpusVersion:      null, // the internal scan response doesn't expose corpusVersion
                LastError:          null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // honour shutdown
        }
        catch (Exception ex)
        {
            _log.LogWarning("[thesecuritybot] scan {Addr} failed: {Type}", addr, ex.GetType().Name);
            return Error(addr, nowIso, ex.GetType().Name);
        }
    }

    private static SecurityVerdict Error(string addr, string nowIso, string reason) =>
        new(addr, SecurityStatus.Error, null, null, null, null, null, nowIso, null, reason);

    private sealed record ScanResponseDto(
        [property: JsonPropertyName("verdict")]         string? Verdict,
        [property: JsonPropertyName("score")]           int? Score,
        [property: JsonPropertyName("grade")]           string? Grade,
        [property: JsonPropertyName("observableCount")] int? ObservableCount,
        [property: JsonPropertyName("totalPatterns")]   int? TotalPatterns,
        [property: JsonPropertyName("scannedAt")]       string? ScannedAt,
        [property: JsonPropertyName("findings")]        List<ScanFindingDto>? Findings);

    private sealed record ScanFindingDto(
        [property: JsonPropertyName("severity")] string? Severity);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~TheSecurityBotClientTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Services/TheSecurityBotClient.cs ACP_Metabot.Api.Tests/TheSecurityBotClientTests.cs
git commit -m "feat(metabot): TheSecurityBotClient (internal scan -> verdict, error-safe)"
```

---

### Task 6: `SecurityScanWorker`

**Files:**
- Create: `ACP_Metabot.Api/Services/SecurityScanWorker.cs`
- Test: `ACP_Metabot.Api.Tests/SecurityScanWorkerTests.cs`

The worker is default-OFF, resolves its dependencies from a scope per tick (the repos/clients are singletons but the scope pattern matches `MarketplacePulseWorker`), pulls stale agents, scans each with a delay between them, and upserts every verdict. The single tick (`TickOnceAsync`) is public so it can be unit-tested directly without running the hosted loop.

- [ ] **Step 1: Write the failing tests**

Create `ACP_Metabot.Api.Tests/SecurityScanWorkerTests.cs`:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class SecurityScanWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly SecurityVerdictRepository _repo;
    private readonly ServiceProvider _sp;

    public SecurityScanWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_secworker_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"]      = $"Data Source={_dbPath}",
                ["SECURITY_SCAN_ENABLED"]         = "true",
                ["SECURITY_SCAN_BATCH"]           = "10",
                ["SECURITY_SCAN_DELAY_SECONDS"]   = "0", // no real delay in tests
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new SecurityVerdictRepository(_db);

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_db);
        services.AddSingleton(_repo);
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private async Task InsertOfferingAsync(string addr)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var iso = DateTime.UtcNow.ToString("O");
        cmd.CommandText = @"
            INSERT INTO offerings
                (agent_address, agent_name, offering_name, description, price_usdc,
                 price_type, chain, content_hash, first_seen_at, last_seen_at, usage_count, is_removed)
            VALUES ($a, 'n', 'o-' || $a, 'd', 1.0, 'per_call', 'base', $a, $i, $i, 0, 0);";
        cmd.Parameters.AddWithValue("$a", addr);
        cmd.Parameters.AddWithValue("$i", iso);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class FakeClient : ITheSecurityBotClient
    {
        public readonly List<string> Scanned = new();
        public Func<string, SecurityVerdict>? Map;
        public Task<SecurityVerdict> ScanAsync(string agentAddress, CancellationToken ct = default)
        {
            Scanned.Add(agentAddress);
            var v = Map?.Invoke(agentAddress)
                ?? new SecurityVerdict(agentAddress, SecurityStatus.Scanned, 90, "A", 11, 0, "{}",
                    DateTime.UtcNow.ToString("O"), null, null);
            return Task.FromResult(v);
        }
    }

    private SecurityScanWorker MakeWorker(ITheSecurityBotClient client)
    {
        var scopeFactory = _sp.GetRequiredService<IServiceScopeFactory>();
        var config = _sp.GetRequiredService<IConfiguration>();
        return new SecurityScanWorker(scopeFactory, client, config, NullLogger<SecurityScanWorker>.Instance);
    }

    [Fact]
    public async Task Tick_ScansStaleAgents_UpsertsVerdicts()
    {
        await InsertOfferingAsync("0xa");
        await InsertOfferingAsync("0xb");
        var client = new FakeClient();
        var worker = MakeWorker(client);

        var n = await worker.TickOnceAsync(CancellationToken.None);

        Assert.Equal(2, n);
        Assert.Equal(2, client.Scanned.Count);
        Assert.NotNull(await _repo.GetByAgentAsync("0xa"));
        Assert.NotNull(await _repo.GetByAgentAsync("0xb"));
    }

    [Fact]
    public async Task Tick_SkipsFreshlyScannedAgents()
    {
        await InsertOfferingAsync("0xfresh");
        await _repo.UpsertAsync(new SecurityVerdict("0xfresh", SecurityStatus.Scanned, 80, "B",
            11, 0, "{}", DateTime.UtcNow.ToString("O"), null, null));
        var client = new FakeClient();
        var worker = MakeWorker(client);

        var n = await worker.TickOnceAsync(CancellationToken.None);

        Assert.Equal(0, n);
        Assert.Empty(client.Scanned);
    }

    [Fact]
    public async Task Tick_RespectsBatchLimit()
    {
        for (int i = 0; i < 15; i++) await InsertOfferingAsync($"0x{i:x2}");
        var client = new FakeClient();
        var worker = MakeWorker(client);

        var n = await worker.TickOnceAsync(CancellationToken.None);

        Assert.Equal(10, n); // SECURITY_SCAN_BATCH = 10
        Assert.Equal(10, client.Scanned.Count);
    }

    [Fact]
    public async Task Tick_PersistsErrorVerdict_WhenClientReportsError()
    {
        await InsertOfferingAsync("0xerr");
        var client = new FakeClient
        {
            Map = a => new SecurityVerdict(a, SecurityStatus.Error, null, null, null, null, null,
                DateTime.UtcNow.ToString("O"), null, "HTTP 500")
        };
        var worker = MakeWorker(client);

        await worker.TickOnceAsync(CancellationToken.None);

        var row = await _repo.GetByAgentAsync("0xerr");
        Assert.Equal(SecurityStatus.Error, row!.Status);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityScanWorkerTests"`
Expected: FAIL to compile — `SecurityScanWorker` does not exist.

- [ ] **Step 3: Write the worker**

Create `ACP_Metabot.Api/Services/SecurityScanWorker.cs`:

```csharp
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// Background scanner that keeps the security_verdicts cache fresh by calling
/// SecurityBot's free internal scan endpoint over acp-shared. Default OFF —
/// set SECURITY_SCAN_ENABLED=true once THESECURITYBOT_API_KEY is wired (same
/// flip-on-when-ready convention as MarketplacePulseWorker).
///
/// Each tick selects up to SECURITY_SCAN_BATCH stale agents (never-scanned or
/// past their per-status TTL), scans each with SECURITY_SCAN_DELAY_SECONDS
/// between calls, and upserts every verdict. Deliberately gentle on SecurityBot
/// and the external targets it probes. Single-replica assumption: the portfolio
/// runs one Metabot instance, so the serial batch needs no distributed lock.
/// </summary>
public sealed class SecurityScanWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ITheSecurityBotClient _client;
    private readonly ILogger<SecurityScanWorker> _log;

    private readonly bool _enabled;
    private readonly int _batch;
    private readonly TimeSpan _delay;
    private readonly TimeSpan _tick;
    private readonly int _activeWindowDays;
    private readonly TimeSpan _scannedTtl;
    private readonly TimeSpan _notAuditableTtl;
    private readonly TimeSpan _errorTtl;

    public SecurityScanWorker(IServiceScopeFactory scopes, ITheSecurityBotClient client,
        IConfiguration config, ILogger<SecurityScanWorker> log)
    {
        _scopes = scopes;
        _client = client;
        _log = log;

        _enabled = config.GetValue<bool?>("SECURITY_SCAN_ENABLED") ?? false;
        _batch   = Math.Clamp(config.GetValue<int?>("SECURITY_SCAN_BATCH") ?? 10, 1, 100);
        _delay   = TimeSpan.FromSeconds(Math.Max(0, config.GetValue<int?>("SECURITY_SCAN_DELAY_SECONDS") ?? 5));
        _tick    = TimeSpan.FromSeconds(Math.Max(15, config.GetValue<int?>("SECURITY_SCAN_TICK_SECONDS") ?? 60));
        _activeWindowDays = Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_ACTIVE_WINDOW_DAYS") ?? 30);
        _scannedTtl      = TimeSpan.FromDays(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_TTL_DAYS") ?? 7));
        _notAuditableTtl = TimeSpan.FromDays(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_NOTAUDITABLE_TTL_DAYS") ?? 30));
        _errorTtl        = TimeSpan.FromHours(Math.Max(1, config.GetValue<int?>("SECURITY_SCAN_ERROR_TTL_HOURS") ?? 6));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _log.LogInformation("[security-scan] disabled — set SECURITY_SCAN_ENABLED=true to activate");
            return;
        }
        _log.LogInformation("[security-scan] enabled; batch={Batch}, delay={Delay}s, tick={Tick}",
            _batch, _delay.TotalSeconds, _tick);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "[security-scan] tick failed; continuing"); }
            try { await Task.Delay(_tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Run one batch. Returns the number of agents scanned. Public for tests.</summary>
    public async Task<int> TickOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<SecurityVerdictRepository>();

        var stale = await repo.GetStaleAgentsAsync(
            DateTime.UtcNow, _activeWindowDays,
            _scannedTtl, _notAuditableTtl, _errorTtl, _batch, ct);
        if (stale.Count == 0) return 0;

        _log.LogInformation("[security-scan] {Count} stale agents this tick", stale.Count);
        int scanned = 0;
        for (int i = 0; i < stale.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var verdict = await _client.ScanAsync(stale[i], ct);
            await repo.UpsertAsync(verdict, ct);
            scanned++;
            if (_delay > TimeSpan.Zero && i < stale.Count - 1)
                await Task.Delay(_delay, ct);
        }
        return scanned;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SecurityScanWorkerTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Services/SecurityScanWorker.cs ACP_Metabot.Api.Tests/SecurityScanWorkerTests.cs
git commit -m "feat(metabot): SecurityScanWorker (default-off, gentle batch scanner)"
```

---

### Task 7: DI registration in `Program.cs`

**Files:**
- Modify: `ACP_Metabot.Api/Program.cs` (HttpClient block ~line 67-80; repo block ~line 36; hosted-service block ~line 224)

No new test — covered by the worker/client tests + the boot smoke in Task 9. Build is the gate.

- [ ] **Step 1: Register the repository**

In `ACP_Metabot.Api/Program.cs`, after the existing `builder.Services.AddSingleton<ReputationFeedRepository>();` (line 36), add:

```csharp
builder.Services.AddSingleton<SecurityVerdictRepository>();
```

- [ ] **Step 2: Register the hardened HttpClient + client**

Immediately after the `thechainlinkbot` registration block (after line 80, `AddSingleton<ITheChainlinkBotClient>(...)`), add:

```csharp
// Cross-bot HTTP client to ACP_SecurityBot's free internal scan endpoint over
// acp-shared. BaseAddress from TheSecurityBot:BaseUrl (default: the bridge
// service name); THESECURITYBOT_API_KEY (-> TheSecurityBot:ApiKey via compose)
// read by the typed client. P39: pin the resolved IP at connect time + refuse
// 3xx so a DNS-rebind / compromised-peer redirect can't bounce the key-bearing
// request to cloud-metadata / link-local.
builder.Services.AddHttpClient("thesecuritybot", c =>
{
    var baseUrl = builder.Configuration["TheSecurityBot:BaseUrl"]
        ?? "http://securitybot-api:5000/";
    if (!baseUrl.EndsWith("/")) baseUrl += "/";
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout = TimeSpan.FromSeconds(30); // scans probe a live surface; allow a few s
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectCallback   = ACP_Metabot.Api.Services.AcpClients.InternalConnectCallbacks.PinResolvedIp,
});
builder.Services.AddSingleton<TheSecurityBotClient>();
builder.Services.AddSingleton<ITheSecurityBotClient>(sp => sp.GetRequiredService<TheSecurityBotClient>());
```

> Note: do NOT add `.AddStandardResilienceHandler()` here — its retry/timeout layering can conflict with `ConfigurePrimaryHttpMessageHandler` semantics, and `ScanAsync` already maps any failure to `status=error` (retried next tick by the TTL).

If `System.Net.Http` is not already imported at the top of `Program.cs`, add `using System.Net.Http;` (it is needed for `SocketsHttpHandler`).

- [ ] **Step 3: Register the hosted worker**

After `builder.Services.AddHostedService<BackupWorker>();` (line 224), add:

```csharp
builder.Services.AddHostedService<SecurityScanWorker>();
```

- [ ] **Step 4: Build**

Run: `dotnet build ACP_Metabot.Api`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add ACP_Metabot.Api/Program.cs
git commit -m "feat(metabot): register SecurityVerdictRepository + TheSecurityBotClient + SecurityScanWorker"
```

---

### Task 8: `/v1/digest` security enrichment

**Files:**
- Modify: `ACP_Metabot.Api/Models/Digest.cs:5-18` (add `Security` to `NewOffering`)
- Modify: `ACP_Metabot.Api/Services/DigestService.cs` (inject repo; join verdicts; `includeSecurity` flag + cache key)
- Modify: `ACP_Metabot.Api/Program.cs` (`HandleDigest` + `/v1/digest` MapGet pass `includeSecurity`)
- Test: `ACP_Metabot.Api.Tests/DigestServiceSecurityTests.cs`

- [ ] **Step 1: Add the `Security` field to `NewOffering`**

In `ACP_Metabot.Api/Models/Digest.cs`, change the `NewOffering` record (lines 5-18) to add a trailing optional `Security` parameter:

```csharp
public record NewOffering(
    [property: JsonPropertyName("offeringId")]    long OfferingId,
    [property: JsonPropertyName("agentName")]     string AgentName,
    [property: JsonPropertyName("agentAddress")]  string AgentAddress,
    [property: JsonPropertyName("offeringName")]  string OfferingName,
    [property: JsonPropertyName("description")]   string Description,
    [property: JsonPropertyName("priceUsdc")]     double PriceUsdc,
    [property: JsonPropertyName("priceType")]     string PriceType,
    [property: JsonPropertyName("chain")]         string Chain,
    [property: JsonPropertyName("firstSeenAt")]   string FirstSeenAt,
    [property: JsonPropertyName("reputation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ReputationSummary? Reputation,
    [property: JsonPropertyName("marketplaceVersion")]
        string MarketplaceVersion = "v1",
    [property: JsonPropertyName("security"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OfferingSecurity? Security = null);
```

- [ ] **Step 2: Write the failing test**

Create `ACP_Metabot.Api.Tests/DigestServiceSecurityTests.cs`. It drives the real `DigestService` against a temp DB seeded with one new offering + a verdict for its agent, and asserts the digest's offering carries the `security` projection. If the existing `DigestServiceNewResourcesTests` already has a temp-DB + dependency-construction helper, mirror it; otherwise this self-contained setup works:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class DigestServiceSecurityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;

    public DigestServiceSecurityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"acp_metabot_digsec_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private async Task InsertOfferingAsync(string addr)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var iso = DateTime.UtcNow.ToString("O");
        cmd.CommandText = @"
            INSERT INTO offerings
                (agent_address, agent_name, offering_name, description, price_usdc,
                 price_type, chain, content_hash, first_seen_at, last_seen_at, usage_count,
                 marketplace_version, is_removed)
            VALUES ($a, 'AgentN', 'off-' || $a, 'desc', 1.0, 'per_call', 'base', $a, $i, $i, 0, 'v2', 0);";
        cmd.Parameters.AddWithValue("$a", addr);
        cmd.Parameters.AddWithValue("$i", iso);
        await cmd.ExecuteNonQueryAsync();
    }

    private DigestService MakeService()
    {
        var offerings = new OfferingRepository(_db);
        var reputation = new ReputationService(/* see note */ null!, null!);
        var saturation = new SaturationCalculator(/* see note */ null!);
        var secRepo = new SecurityVerdictRepository(_db);
        return new DigestService(offerings, reputation, saturation, resourcesRepo: null, securityRepo: secRepo);
    }

    [Fact]
    public async Task Digest_AttachesSecurityVerdict_WhenPresent()
    {
        await InsertOfferingAsync("0xa");
        var secRepo = new SecurityVerdictRepository(_db);
        await secRepo.UpsertAsync(new SecurityVerdict("0xa", SecurityStatus.Scanned, 91, "A",
            11, 0, "{}", DateTime.UtcNow.ToString("O"), null, null));

        var svc = MakeService();
        var result = await svc.BuildAsync(windowDays: 1, marketplaceFilter: null,
            chainFilter: null, priceMaxUsdc: null, includeSecurity: true);

        var off = result.NewOfferings.Single(o => o.AgentAddress.Equals("0xa", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(off.Security);
        Assert.Equal(SecurityStatus.Scanned, off.Security!.Status);
        Assert.Equal(91, off.Security.Score);
        Assert.Equal("A", off.Security.Grade);
    }

    [Fact]
    public async Task Digest_Pending_WhenNoVerdict()
    {
        await InsertOfferingAsync("0xb");
        var svc = MakeService();
        var result = await svc.BuildAsync(1, null, null, null, includeSecurity: true);

        var off = result.NewOfferings.Single(o => o.AgentAddress.Equals("0xb", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SecurityStatus.Pending, off.Security!.Status);
    }

    [Fact]
    public async Task Digest_OmitsSecurity_WhenIncludeFalse()
    {
        await InsertOfferingAsync("0xc");
        var svc = MakeService();
        var result = await svc.BuildAsync(1, null, null, null, includeSecurity: false);

        var off = result.NewOfferings.Single(o => o.AgentAddress.Equals("0xc", StringComparison.OrdinalIgnoreCase));
        Assert.Null(off.Security);
    }
}
```

> **Constructor note for the executor:** `ReputationService` / `SaturationCalculator` real constructors take dependencies. Before writing this test, open `DigestServiceNewResourcesTests.cs` and reuse exactly how it constructs `DigestService` (it already solves the `reputation`/`saturation` wiring for a temp DB). Replace the `MakeService()` body above with that proven construction, then add `securityRepo: secRepo` as the new trailing argument. Do not invent constructor arguments — copy the working ones.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~DigestServiceSecurityTests"`
Expected: FAIL to compile — `DigestService` has no `securityRepo` parameter / no `includeSecurity` overload.

- [ ] **Step 4: Wire the join into `DigestService`**

In `ACP_Metabot.Api/Services/DigestService.cs`:

(a) Add the field + constructor parameter (after `_resourcesRepo`):

```csharp
    private readonly AgentResourcesRepository? _resourcesRepo;
    private readonly SecurityVerdictRepository? _securityRepo;
```

```csharp
    public DigestService(OfferingRepository repo, ReputationService reputation,
        SaturationCalculator saturation, AgentResourcesRepository? resourcesRepo = null,
        SecurityVerdictRepository? securityRepo = null)
    {
        _repo = repo;
        _reputation = reputation;
        _saturation = saturation;
        _resourcesRepo = resourcesRepo;
        _securityRepo = securityRepo;
    }
```

(b) Replace the `CacheKey` helper + the two `BuildAsync` overloads + `BuildUncachedAsync` signature so `includeSecurity` flows through and participates in the cache key:

```csharp
    private static string CacheKey(int days, string? mv, HashSet<string>? chain, double? price, bool sec)
    {
        var c = chain is null ? "" : string.Join(",", chain.OrderBy(s => s));
        return $"d={days}|m={mv ?? ""}|c={c}|p={price?.ToString() ?? ""}|s={(sec ? 1 : 0)}";
    }
```

```csharp
    // Backward-compat overload — delegate to the full version.
    public Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter = null)
        => BuildAsync(windowDays, marketplaceFilter, chainFilter: null, priceMaxUsdc: null, includeSecurity: true);

    public Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter,
        HashSet<string>? chainFilter, double? priceMaxUsdc)
        => BuildAsync(windowDays, marketplaceFilter, chainFilter, priceMaxUsdc, includeSecurity: true);

    public async Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter,
        HashSet<string>? chainFilter, double? priceMaxUsdc, bool includeSecurity)
    {
        var hourBucket = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month,
            DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc);
        var key = CacheKey(windowDays, marketplaceFilter, chainFilter, priceMaxUsdc, includeSecurity);

        if (_cache.TryGetValue(key, out var entry) && entry.Bucket == hourBucket)
            return entry.Result;

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out entry) && entry.Bucket == hourBucket)
                return entry.Result;

            var fresh = await BuildUncachedAsync(windowDays, marketplaceFilter, chainFilter, priceMaxUsdc, includeSecurity);
            _cache[key] = (hourBucket, fresh);
            return fresh;
        }
        finally { _lock.Release(); }
    }
```

Change the `BuildUncachedAsync` signature to accept the flag:

```csharp
    private async Task<DigestResult> BuildUncachedAsync(int windowDays, string? marketplaceFilter,
        HashSet<string>? chainFilter, double? priceMaxUsdc, bool includeSecurity)
```

(c) Build the verdict lookup BEFORE constructing `newOfferingDtos`, then attach `Security`. Replace the existing `newOfferingDtos` assignment (lines ~129-140) with:

```csharp
        // Security enrichment (decoupled cache; populated by SecurityScanWorker).
        IReadOnlyDictionary<string, SecurityVerdict> verdicts =
            new Dictionary<string, SecurityVerdict>();
        if (includeSecurity && _securityRepo is not null && newOfferings.Count > 0)
        {
            var addrs = newOfferings.Select(o => o.AgentAddress.ToLowerInvariant()).Distinct().ToList();
            verdicts = await _securityRepo.GetManyAsync(addrs);
        }

        OfferingSecurity? SecurityFor(string agentAddress)
        {
            if (!includeSecurity) return null;
            var key = agentAddress.ToLowerInvariant();
            return verdicts.TryGetValue(key, out var v)
                ? OfferingSecurity.FromVerdict(v)
                : OfferingSecurity.Pending;
        }

        var newOfferingDtos = newOfferings.Select(o => new NewOffering(
            OfferingId: o.Id,
            AgentName: o.AgentName,
            AgentAddress: o.AgentAddress,
            OfferingName: o.OfferingName,
            Description: o.Description,
            PriceUsdc: o.PriceUsdc,
            PriceType: o.PriceType,
            Chain: o.Chain,
            FirstSeenAt: o.FirstSeenAt.ToString("O", CultureInfo.InvariantCulture),
            Reputation: _reputation.BuildSearchSummary(o),
            MarketplaceVersion: o.MarketplaceVersion,
            Security: SecurityFor(o.AgentAddress))).ToArray();
```

(If `ACP_Metabot.Api.Models` is not already imported, the existing file already has `using ACP_Metabot.Api.Models;` — confirm `SecurityVerdict`/`OfferingSecurity` resolve.)

- [ ] **Step 5: Thread `includeSecurity` through the endpoint**

In `ACP_Metabot.Api/Program.cs`, update `HandleDigest` (line 945) and the `/v1/digest` MapGet (line 1740) — and, for consistency, the `/digest` alias at line 1043 (pass `includeSecurity: true` there since it has no query param). Change `HandleDigest`:

```csharp
async Task<IResult> HandleDigest(int? days, string? marketplace,
    string[]? chain, double? priceMaxUsdc, bool? includeSecurity, DigestService svc)
{
    var window = days is null ? 1 : Math.Clamp(days.Value, 1, 90);
    var marketplaceFilter = NormalizeMarketplace(marketplace);
    if (marketplace is not null && marketplaceFilter is null)
        return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });

    HashSet<string>? chainFilter = null;
    if (chain is { Length: > 0 })
    {
        if (chain.Length > 8)
            return Results.BadRequest(new { error = "chain accepts at most 8 entries" });
        chainFilter = new HashSet<string>(chain.Select(c => (c ?? "").Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        chainFilter.Remove("");
        if (chainFilter.Count == 0) chainFilter = null;
    }

    if (priceMaxUsdc is double cap && (double.IsNaN(cap) || cap < 0))
        return Results.BadRequest(new { error = "priceMaxUsdc must be a non-negative number" });

    var result = await svc.BuildAsync(window, marketplaceFilter, chainFilter, priceMaxUsdc,
        includeSecurity ?? true);
    return Results.Ok(result);
}
```

Update the `/v1/digest` MapGet (line 1740) to bind + forward the new query param:

```csharp
app.MapGet("/v1/digest", (int? days, string? marketplace, string[]? chain, double? priceMaxUsdc,
        bool? includeSecurity, DigestService svc)
    => HandleDigest(days, marketplace, chain, priceMaxUsdc, includeSecurity, svc))
    .RequireRateLimiting("public-digest");
```

Update the internal `/digest` alias at line 1043 to pass the new arg (it currently calls the old shape). If it calls `HandleDigest(...)`, add `includeSecurity: true`; if it calls `svc.BuildAsync(...)` directly, leave it (the 5-arg `BuildAsync` defaults to `includeSecurity: true`). Verify by reading lines 1043-1055 before editing.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~DigestServiceSecurityTests"`
Expected: PASS (3 tests).

- [ ] **Step 7: Build the whole API to confirm endpoint wiring compiles**

Run: `dotnet build ACP_Metabot.Api`
Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add ACP_Metabot.Api/Models/Digest.cs ACP_Metabot.Api/Services/DigestService.cs ACP_Metabot.Api/Program.cs ACP_Metabot.Api.Tests/DigestServiceSecurityTests.cs
git commit -m "feat(metabot): enrich /v1/digest offerings with security verdict"
```

---

### Task 9: Full suite + boot smoke

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test ACP_Metabot.Api.Tests`
Expected: All tests pass (existing + the new SecurityVerdictRepository / TheSecurityBotClient / SecurityScanWorker / DigestServiceSecurity / Db migration tests). No regressions.

- [ ] **Step 2: Boot smoke (worker default-OFF path)**

Run: `dotnet run --project ACP_Metabot.Api` (Ctrl-C after the boot banner).
Expected log line: `[security-scan] disabled — set SECURITY_SCAN_ENABLED=true to activate` (the worker registered but stays dormant with no key/flag — confirms no crash-on-boot and no unintended scanning).

- [ ] **Step 3: Optional local end-to-end (only if a SecurityBot is reachable locally)**

Skip unless a local SecurityBot is running. Otherwise this is validated on the droplet post-deploy (Task 10).

---

### Task 10: Plugin description + ops handoff (LOCAL-only; no deploy)

**Files:**
- Modify: `acp-find-plugin/mcp-server/server.js` (the `acp_today` tool `description`)
- Create/Append: `ACP/security-audit/_followups_2026-06-08.md` (ops note — see below)

- [ ] **Step 1: Bump the `acp_today` tool description**

In `acp-find-plugin/mcp-server/server.js`, find the tool definition for `acp_today` (the `tools` array entry, distinct from the handler at line 1578) and extend its `description` to mention the new field, e.g. append:

`" Each offering now carries a per-agent security object {score, grade, status, scannedAt} from SecurityBot (status 'pending' until first scanned)."`

Do not change handler logic — the field flows through `/v1/digest` automatically. This is **LOCAL-only**; it reaches users only after a separate plugin release (`npm version` + `npm publish` from a real PowerShell, per the acp-find release rule).

- [ ] **Step 2: Commit the plugin change (local)**

```bash
git add acp-find-plugin/mcp-server/server.js
git commit -m "docs(acp-find): note acp_today per-agent security field (local, pre-publish)"
```

(Run from the plugin's own git repo root if `acp-find-plugin` is a separate repo; otherwise from the Metabot repo if it is tracked there. Check `git status` first.)

- [ ] **Step 3: Record the ops/deploy handoff**

Append to `ACP/security-audit/_followups_2026-06-08.md` a new section (these steps require Oliver's machine / the droplet and are NOT done by this plan):

```markdown
## F. Security-vetted acp_today — deploy + enable (Metabot)
Code committed local-only (no push) per "do not deploy to github". To ship:
1. Push the Metabot commits, then on the droplet `cd /root/ACP_Metabot && git pull --ff-only origin main`.
2. Wire the cross-bot key (FREE path, no escrow):
   - In `/root/ACP_Metabot/.env`: `THESECURITYBOT_API_KEY=<SecurityBot INTERNAL_API_KEY>`
     (check `tail -c1 .env` for a trailing newline first).
   - In `/root/ACP_Metabot/docker-compose.yml` under the metabot-api `environment:`,
     add `- TheSecurityBot__ApiKey=${THESECURITYBOT_API_KEY:-}` (mirrors the existing
     `TheChainlinkBot__ApiKey` line). Both bots are already on `acp-shared`.
3. Enable the worker: add `SECURITY_SCAN_ENABLED=true` to `/root/ACP_Metabot/.env`
   (optional tunables: SECURITY_SCAN_BATCH=10, SECURITY_SCAN_DELAY_SECONDS=5,
   SECURITY_SCAN_TTL_DAYS=7, SECURITY_SCAN_TICK_SECONDS=60).
4. `docker compose up -d --build acp-metabot-api` (NOT restart — env changes need recreate).
5. Smoke: `docker compose logs -f acp-metabot-api | grep security-scan` should read
   "enabled; batch=10 ...". After a few ticks, `GET https://api.acp-metabot.dev/v1/digest`
   offerings carry `security:{status:...}`.
6. Plugin: republish acp-find-mcp (real PowerShell `npm publish`) so acp_today's
   description reaches users; the `security` field already flows through the gateway.
```

- [ ] **Step 4: Commit the ops note**

```bash
git add security-audit/_followups_2026-06-08.md
git commit -m "docs: ops handoff for security-vetted acp_today deploy"
```

(From the ACP workspace repo that tracks `security-audit/`. Check `git status` to confirm the right repo root.)

---

## Self-Review

**Spec coverage** (against `2026-06-08-metabot-security-vetted-today-design.md`):
- `security_verdicts` cache (§Components 1) → Task 1 + Task 3. ✅ (all columns: status/score/grade/observable_count/finding_count/severity_counts/scanned_at/corpus_version/last_error). `corpus_version` is stored nullable — the internal scan response does not expose it; documented in `TheSecurityBotClient` (set null) rather than faked.
- `TheSecurityBotClient` (§Components 2) → Task 4 (P39 handler) + Task 5. ✅ BaseUrl/key config, X-API-Key, AllowAutoRedirect=false + PinResolvedIp (Task 6 registration), ~30s timeout, P17 fail-fast, NOT_AUDITABLE→not_auditable, non-2xx→error no-throw, no raw-body leak.
- `SecurityScanWorker` (§Components 3) → Task 6. ✅ tick=60s, stale selection, all-live-agents candidate universe (offerings join), priority (never-scanned → hires → oldest), batch=10, 5s delay, single-flight serial, cancellation.
- Gateway `/v1/digest` enrichment (§Components 4) → Task 8. ✅ batch GetMany, `{score,grade,status,scannedAt}`, `pending` when absent, no findings/evidence (OfferingSecurity drops them).
- Plugin `acp_today` (§Components 5) → Task 10. ✅ description bump, no handler change, local-only.
- TTLs (§TTLs) → Task 3 GetStaleAgents + Task 6 worker config. ✅ scanned 7d / not_auditable 30d / error 6h.
- Cost $0 (§Cost) → free internal path, no escrow. ✅
- Error handling (§Error handling) → Task 5 (error mapping, no throw) + Task 6 (tick try/catch continue) + Task 8 (`pending` never breaks digest). ✅ NoopSecurityBotClient alternative deliberately NOT implemented — the default-OFF flag + error-safe client already give graceful degrade without a second type (YAGNI).
- Out-of-scope items (§Out of scope) honored: no paid offering, no raw findings, no separate composite tool, no scan-on-digest, no republish in this plan.

**Placeholder scan:** No TBD/TODO. The one deliberate "open the neighbouring test to copy the proven constructor" instruction (Task 8 Step 2) is a guard against inventing `ReputationService`/`SaturationCalculator` constructor args — the executor copies a working construction rather than guessing.

**Type consistency:** `SecurityVerdict` (10 fields) is constructed identically in Tasks 3/5/6/8 and the repo `Map`. `SecurityStatus` constants (`scanned`/`not_auditable`/`error`/`pending`) used consistently. `OfferingSecurity.FromVerdict` / `.Pending` used in Task 8. `ITheSecurityBotClient.ScanAsync` signature identical in Task 5 (def), Task 6 (consumer + fake). `GetStaleAgentsAsync` / `GetManyAsync` / `UpsertAsync` / `GetByAgentAsync` signatures match across repo (Task 3) and callers (Tasks 6, 8). `BuildAsync(..., bool includeSecurity)` overload matches the Task 8 test + the Program.cs caller.
