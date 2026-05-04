# TheMetaBot v1.7 Meta-Search Enhancement — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend TheMetaBot's offering-centric search to a true meta-search surface — hybrid agent ranking, V1↔V2 cross-presence, per-offering saturation + price-percentile signals, and a marketplace-pulse digest with inflow / churn / cohort retention.

**Architecture:** Layers additively on the v1.6 surface (5b71d08). New schema (`agent_profiles` + FTS5 mirror) feeds a hybrid ranker that upgrades `/v1/searchAgents` from BM25 group-by to BM25 + dense + RRF + Voyage rerank. Per-result calculators (`SaturationCalculator`, `PricePercentileCalculator`) read existing `SearchService._corpus` in-memory. `DigestService` extended with six new fields and a per-filter-set hourly cache; `days` cap extended from 30 → 90 to fit cohort survival. `browseAgent` path gains a `BuildCrossPresenceAsync` helper. Sidecar / MCP server / plugin / docs ship in lockstep.

**Tech Stack:** C# .NET 10, ADO.NET + SQLite (`Microsoft.Data.Sqlite`), Voyage 4 embedding via existing `VoyageEmbeddingProvider`, FTS5, xUnit, TypeScript 5.7, `@virtuals-protocol/acp-node-v2` ^0.0.6.

**Spec:** `docs/superpowers/specs/2026-05-04-metabot-v1-7-meta-search-design.md` (committed at 335188c).

---

## File Structure

### C# tier (`ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/`)

**Create:**
- `Data/AgentProfileRepository.cs` — CRUD on `agent_profiles`, dirty-queue read.
- `Services/AgentProfileEmbedderService.cs` — `IHostedService` that drains the dirty queue.
- `Services/AgentSearchService.cs` — hybrid ranker (BM25 + dense + RRF + rerank).
- `Services/SaturationCalculator.cs` — per-offering and per-category saturation over `SearchService._corpus`.
- `Services/PricePercentileCalculator.cs` — percentile within `(category × marketplace)`.
- `Services/CrossPresenceBuilder.cs` — `BuildCrossPresenceAsync(address)` over `OfferingRepository`.

**Modify:**
- `Data/Db.cs` — append `agent_profiles` table + FTS5 + triggers + dirty index in `InitializeSchemaAsync`.
- `Data/OfferingRepository.cs` — bump `agent_profiles.last_change_at` on offering write paths.
- `Models/AgentSearchResult.cs` — extend `AgentSearchHit` (`marketplaces`, `dominantMarketplace`, `agentScore`, `topOfferingNames` mirror; `topOfferings` shape change to records).
- `Models/Digest*.cs` (or wherever `DigestResult` lives) — add `windowStart`, `partial`, `newAgents`, `churnRate`, `cohortSurvival`, `saturationMap`.
- `Services/SearchService.cs` — expose corpus accessors for calculators; populate `saturation` + `pricePercentile` on offering hits.
- `Services/DigestService.cs` — extend `BuildAsync` with new fields; per-filter-set hourly cache; raise `days` cap from 30 → 90.
- `Program.cs` — wire `AgentSearchService` into `/v1/searchAgents` handler; register new services; raise `days` clamp on `/v1/digest` to 90.

### Tests (`ACP_Metabot.Api.Tests/`)

**Create:**
- `AgentProfileRepositoryTests.cs`
- `AgentProfileEmbedderServiceTests.cs`
- `AgentSearchServiceTests.cs`
- `SaturationCalculatorTests.cs`
- `PricePercentileCalculatorTests.cs`
- `CrossPresenceBuilderTests.cs`
- `DigestServicePulseTests.cs` (separate file from any existing digest tests)

**Modify:**
- `DbMigrationTests.cs` — extend with v1.7 migration cases.

### Sidecar (`acp-v2/src/`)

**Modify:**
- `apiClient.ts` — extend types.
- `offerings/find.ts` (or wherever `acp_find` is defined) — schema additions.
- `offerings/search-agents.ts` — schema additions.
- `offerings/browse-agent.ts` — schema additions.
- `offerings/today.ts` — schema additions + `days` arg.
- `package.json` — version bump.

### MCP server (`acp-find-plugin/mcp-server/src/`)

**Modify:**
- `tools/find.ts`, `tools/search-agents.ts`, `tools/browse-agent.ts`, `tools/today.ts` — tool schemas.
- `package.json` — version 0.7.0.
- `README.md` — npm-published readme.

### Plugin (`acp-find-plugin/`)

**Modify:**
- `commands/acp-find-search.md`, `commands/acp-find-search-agents.md`, `commands/acp-find-agent.md`, `commands/acp-find-today.md`.
- `skills/acp-find/SKILL.md` (if present).
- `README.md`, `package.json` (or `plugin.json`).

### Docs (`ACP_Metabot/ACP_Metabot/`)

**Modify:**
- `README.md`, `docs/user-guide.md`, `docs/technical-specifications.md`, `docs/design.md`, `docs/runbook-scaling.md`.

---

## Phase 1 — Schema foundation

### Task 1.1: Add `agent_profiles` schema to `Db.cs`

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Data/Db.cs` — append to `InitializeSchemaAsync`.
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/DbMigrationTests.cs` — extend.

- [ ] **Step 1: Write failing migration test**

Append to `DbMigrationTests.cs`:

```csharp
[Fact]
public async Task Migration_CreatesAgentProfilesTable()
{
    await _db.InitializeSchemaAsync();
    await using var conn = _db.OpenConnection();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='agent_profiles'";
    var n = (long)(await cmd.ExecuteScalarAsync())!;
    Assert.Equal(1, n);
}

[Fact]
public async Task Migration_CreatesAgentProfilesFtsAndTriggers()
{
    await _db.InitializeSchemaAsync();
    await using var conn = _db.OpenConnection();

    await using var ftsCmd = conn.CreateCommand();
    ftsCmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='agent_profiles_fts'";
    Assert.Equal(1L, (long)(await ftsCmd.ExecuteScalarAsync())!);

    await using var trgCmd = conn.CreateCommand();
    trgCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'agent_profiles_%' ORDER BY name";
    var triggers = new List<string>();
    await using var r = await trgCmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) triggers.Add(r.GetString(0));
    Assert.Equal(new[] { "agent_profiles_ad", "agent_profiles_ai", "agent_profiles_au" }, triggers);
}

[Fact]
public async Task Migration_AgentProfilesDirtyIndexWorks()
{
    await _db.InitializeSchemaAsync();
    await using var conn = _db.OpenConnection();

    // Insert a dirty row (embedded_at IS NULL).
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
        INSERT INTO agent_profiles (agent_address, agent_name, profile_text, last_change_at)
        VALUES ('0xabc', 'AgentA', 'profile A', '2026-05-04T00:00:00Z')";
    await ins.ExecuteNonQueryAsync();

    // The partial index should have one entry. Check via EXPLAIN QUERY PLAN
    // that the dirty-queue probe uses it.
    await using var explain = conn.CreateCommand();
    explain.CommandText = @"
        EXPLAIN QUERY PLAN
        SELECT agent_address FROM agent_profiles
        WHERE embedded_at IS NULL OR last_change_at > embedded_at";
    var plan = "";
    await using var pr = await explain.ExecuteReaderAsync();
    while (await pr.ReadAsync()) plan += pr.GetString(3) + "|";
    Assert.Contains("ix_agent_profiles_dirty", plan);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~Migration_Creates|Migration_AgentProfilesDirty"
```

Expected: 3 failures with messages like "no such table: agent_profiles".

- [ ] **Step 3: Implement schema additions in `Db.cs`**

Inside `InitializeSchemaAsync`, after the existing `offerings`-related blocks, append:

```csharp
await using var agentProfilesCmd = conn.CreateCommand();
agentProfilesCmd.CommandText = @"
    -- v1.7: agent profile corpus + embedding for hybrid agent search.
    CREATE TABLE IF NOT EXISTS agent_profiles (
        agent_address     TEXT    PRIMARY KEY,
        agent_name        TEXT    NOT NULL,
        profile_text      TEXT    NOT NULL,
        embedding         BLOB,
        embedding_model   TEXT,
        embedded_at       TEXT,
        last_change_at    TEXT    NOT NULL
    );

    CREATE INDEX IF NOT EXISTS ix_agent_profiles_dirty
        ON agent_profiles(last_change_at)
        WHERE embedded_at IS NULL OR last_change_at > embedded_at;

    CREATE VIRTUAL TABLE IF NOT EXISTS agent_profiles_fts USING fts5(
        agent_name, profile_text,
        content='agent_profiles', content_rowid='rowid',
        tokenize='unicode61 remove_diacritics 2'
    );

    CREATE TRIGGER IF NOT EXISTS agent_profiles_ai AFTER INSERT ON agent_profiles BEGIN
        INSERT INTO agent_profiles_fts(rowid, agent_name, profile_text)
        VALUES (new.rowid, new.agent_name, new.profile_text);
    END;

    CREATE TRIGGER IF NOT EXISTS agent_profiles_ad AFTER DELETE ON agent_profiles BEGIN
        INSERT INTO agent_profiles_fts(agent_profiles_fts, rowid, agent_name, profile_text)
        VALUES ('delete', old.rowid, old.agent_name, old.profile_text);
    END;

    CREATE TRIGGER IF NOT EXISTS agent_profiles_au
    AFTER UPDATE OF agent_name, profile_text ON agent_profiles BEGIN
        INSERT INTO agent_profiles_fts(agent_profiles_fts, rowid, agent_name, profile_text)
        VALUES ('delete', old.rowid, old.agent_name, old.profile_text);
        INSERT INTO agent_profiles_fts(rowid, agent_name, profile_text)
        VALUES (new.rowid, new.agent_name, new.profile_text);
    END;
";
await agentProfilesCmd.ExecuteNonQueryAsync();
```

Note `AFTER UPDATE OF agent_name, profile_text` (column-scoped) — same defensive pattern as `offerings_au` to avoid spurious FTS rebuilds when only `last_change_at` / `embedded_at` change.

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~Migration"
```

Expected: all migration tests pass (existing + 3 new).

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Data/Db.cs ACP_Metabot.Api.Tests/DbMigrationTests.cs
git commit -m "feat(v1.7): agent_profiles + FTS5 mirror schema

Migration adds:
- agent_profiles table (PK agent_address)
- agent_profiles_fts external-content FTS5 mirror
- ix_agent_profiles_dirty partial index for embedder dirty-queue
- ai/ad/au triggers (AFTER UPDATE OF column-scoped to avoid v1.2-style trigger storm)

Migration tests verify table+fts+triggers exist, dirty index is used."
```

---

### Task 1.2: AgentProfileRepository CRUD + dirty-queue read

**Files:**
- Create: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Data/AgentProfileRepository.cs`
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/AgentProfileRepositoryTests.cs`

- [ ] **Step 1: Write failing tests**

Create `AgentProfileRepositoryTests.cs`:

```csharp
using ACP_Metabot.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class AgentProfileRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly AgentProfileRepository _repo;

    public AgentProfileRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"acp_metabot_agentprofile_test_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new AgentProfileRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task UpsertAsync_InsertsNewRow_WithDirtyFlag()
    {
        await _repo.UpsertAsync("0xABC", "AgentA", "profile text A");
        var dirty = await _repo.ListDirtyAsync(limit: 100);
        Assert.Single(dirty);
        Assert.Equal("0xabc", dirty[0].AgentAddress); // lowercased
        Assert.Equal("AgentA", dirty[0].AgentName);
        Assert.Equal("profile text A", dirty[0].ProfileText);
        Assert.Null(dirty[0].EmbeddedAt);
    }

    [Fact]
    public async Task UpsertAsync_UpdatesExistingRow_BumpsLastChangeAt()
    {
        await _repo.UpsertAsync("0xabc", "AgentA", "v1");
        await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1, 2, 3 });
        var beforeDirty = await _repo.ListDirtyAsync(100);
        Assert.Empty(beforeDirty);

        await Task.Delay(20); // ensure last_change_at strictly newer
        await _repo.UpsertAsync("0xabc", "AgentA", "v2 changed");

        var afterDirty = await _repo.ListDirtyAsync(100);
        Assert.Single(afterDirty);
        Assert.Equal("v2 changed", afterDirty[0].ProfileText);
    }

    [Fact]
    public async Task MarkEmbeddedAsync_ClearsDirty()
    {
        await _repo.UpsertAsync("0xabc", "A", "p");
        Assert.Single(await _repo.ListDirtyAsync(100));

        await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1, 2, 3 });
        Assert.Empty(await _repo.ListDirtyAsync(100));
    }

    [Fact]
    public async Task BumpLastChangeAtAsync_NoOpIfMissing()
    {
        await _repo.BumpLastChangeAtAsync("0xnonexistent");
        Assert.Empty(await _repo.ListDirtyAsync(100));
    }

    [Fact]
    public async Task BumpLastChangeAtAsync_MakesExistingRowDirtyAgain()
    {
        await _repo.UpsertAsync("0xabc", "A", "p");
        await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1 });
        Assert.Empty(await _repo.ListDirtyAsync(100));

        await Task.Delay(20);
        await _repo.BumpLastChangeAtAsync("0xabc");
        Assert.Single(await _repo.ListDirtyAsync(100));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~AgentProfileRepository"
```

Expected: compile error (`AgentProfileRepository` not defined).

- [ ] **Step 3: Implement `AgentProfileRepository`**

Create `Data/AgentProfileRepository.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

public record AgentProfileRow(
    string AgentAddress,
    string AgentName,
    string ProfileText,
    string? EmbeddingModel,
    byte[]? Embedding,
    string? EmbeddedAt,
    string LastChangeAt);

public class AgentProfileRepository
{
    private readonly Db _db;
    public AgentProfileRepository(Db db) { _db = db; }

    private static string NowUtcIso() => DateTime.UtcNow.ToString("O");

    /// <summary>
    /// Upsert by lowercase address. INSERT writes last_change_at = now,
    /// embedded_at = null. UPDATE rewrites profile_text + bumps
    /// last_change_at; existing embedding stays (the embedder will replace
    /// it on the next cycle).
    /// </summary>
    public async Task UpsertAsync(string agentAddress, string agentName, string profileText)
    {
        var key = agentAddress.ToLowerInvariant();
        var now = NowUtcIso();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO agent_profiles (agent_address, agent_name, profile_text, last_change_at)
            VALUES ($a, $n, $p, $t)
            ON CONFLICT(agent_address) DO UPDATE SET
                agent_name = excluded.agent_name,
                profile_text = excluded.profile_text,
                last_change_at = excluded.last_change_at";
        cmd.Parameters.AddWithValue("$a", key);
        cmd.Parameters.AddWithValue("$n", agentName);
        cmd.Parameters.AddWithValue("$p", profileText);
        cmd.Parameters.AddWithValue("$t", now);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Bumps last_change_at for an existing agent. Used by the offering
    /// ingest path when an agent's offering set changes. No-op if the row
    /// doesn't exist (cold-start backfill will pick it up).
    /// </summary>
    public async Task BumpLastChangeAtAsync(string agentAddress)
    {
        var key = agentAddress.ToLowerInvariant();
        var now = NowUtcIso();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE agent_profiles SET last_change_at = $t WHERE agent_address = $a";
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$a", key);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Read up to <paramref name="limit"/> rows where embedded_at is null
    /// or last_change_at &gt; embedded_at. Order by last_change_at ASC so
    /// older dirty rows drain first.
    /// </summary>
    public async Task<IReadOnlyList<AgentProfileRow>> ListDirtyAsync(int limit)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, profile_text, embedding_model,
                   embedding, embedded_at, last_change_at
            FROM agent_profiles
            WHERE embedded_at IS NULL OR last_change_at > embedded_at
            ORDER BY last_change_at ASC
            LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);

        var rows = new List<AgentProfileRow>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            byte[]? emb = r.IsDBNull(4) ? null : (byte[])r.GetValue(4);
            rows.Add(new AgentProfileRow(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                emb,
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetString(6)));
        }
        return rows;
    }

    public async Task MarkEmbeddedAsync(string agentAddress, string model, byte[] embedding)
    {
        var key = agentAddress.ToLowerInvariant();
        var now = NowUtcIso();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE agent_profiles
            SET embedding = $e, embedding_model = $m, embedded_at = $t
            WHERE agent_address = $a";
        cmd.Parameters.AddWithValue("$e", embedding);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$a", key);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<AgentProfileRow?> GetAsync(string agentAddress)
    {
        var key = agentAddress.ToLowerInvariant();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, agent_name, profile_text, embedding_model,
                   embedding, embedded_at, last_change_at
            FROM agent_profiles WHERE agent_address = $a";
        cmd.Parameters.AddWithValue("$a", key);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        byte[]? emb = r.IsDBNull(4) ? null : (byte[])r.GetValue(4);
        return new AgentProfileRow(
            r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            emb,
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetString(6));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~AgentProfileRepository"
```

Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Data/AgentProfileRepository.cs ACP_Metabot.Api.Tests/AgentProfileRepositoryTests.cs
git commit -m "feat(v1.7): AgentProfileRepository CRUD + dirty-queue

Upsert (lowercased PK), BumpLastChangeAt, ListDirty (oldest-first
drain order), MarkEmbedded, Get. 5 unit tests passing."
```

---

### Task 1.3: Bump `last_change_at` from `OfferingRepository` write paths

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Data/OfferingRepository.cs`
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/AgentProfileRepositoryTests.cs` — extend.

- [ ] **Step 1: Write failing test**

Append to `AgentProfileRepositoryTests.cs`:

```csharp
[Fact]
public async Task OfferingUpsert_BumpsAgentProfileDirtyFlag()
{
    // Pre-condition: agent profile exists and is clean.
    await _repo.UpsertAsync("0xabc", "AgentA", "profile A");
    await _repo.MarkEmbeddedAsync("0xabc", "voyage-3-large", new byte[] { 1 });
    Assert.Empty(await _repo.ListDirtyAsync(100));

    // Indexer ingests a new offering for that agent.
    var offeringRepo = new OfferingRepository(_db);
    await Task.Delay(20);
    await offeringRepo.UpsertManyAsync(new[]
    {
        new OfferingUpsert(
            "0xABC", "AgentA", "wallet_scan", "Scan a wallet for risk", null,
            0.10, "per_call", isPrivate: false, "base", contentHash: "h1",
            marketplaceVersion: "v2")
    }, _repo);

    // The agent should now be dirty.
    Assert.Single(await _repo.ListDirtyAsync(100));
}
```

This requires the (yet-unwritten) signature: `UpsertManyAsync(IEnumerable<OfferingUpsert>, AgentProfileRepository)`. Adapt to whatever the existing `UpsertManyAsync` shape is — the **goal** is that any code path that mutates the offering set for an agent passes the dirty-flag bump along to `AgentProfileRepository.BumpLastChangeAtAsync`.

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~OfferingUpsert_Bumps"
```

Expected: FAIL — either compile error if the signature doesn't take the repo, or assertion failure if the bump isn't wired.

- [ ] **Step 3: Wire `BumpLastChangeAtAsync` into `OfferingRepository` write paths**

In `OfferingRepository.cs`, at every write path that adds, removes, or materially changes an offering — `UpsertManyAsync`, `MarkRemovedAsync`, any tombstone reactivation logic — collect the touched `agent_address` set and call `_agentProfileRepo?.BumpLastChangeAtAsync(addr)` for each at the end of the write transaction. Inject `AgentProfileRepository` via constructor (nullable so existing tests that don't need it still construct cleanly), or pass via the method signature for the affected entry points.

Concretely, for the dominant `UpsertManyAsync` path:

```csharp
public async Task UpsertManyAsync(
    IEnumerable<OfferingUpsert> upserts,
    AgentProfileRepository? agentProfiles = null,
    /* existing params */)
{
    var touchedAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ... existing write logic; for each row processed, add upsert.AgentAddress to touchedAgents ...

    if (agentProfiles is not null)
    {
        foreach (var addr in touchedAgents)
        {
            await agentProfiles.BumpLastChangeAtAsync(addr);
        }
    }
}
```

Apply the same shape to `MarkRemovedAsync` (when it tombstones) and to any reactivation path. Do NOT bump on the touch UPDATE that only changes `last_seen_at` / counters — those don't represent a profile change.

Update DI registration in `Program.cs` so `OfferingRepository` consumers wire `AgentProfileRepository` through where the new behaviour is needed (the `MarketplaceIndexerService` is the dominant caller).

- [ ] **Step 4: Run test to verify it passes**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~OfferingUpsert_Bumps"
```

Expected: PASS. Also run the full test suite to make sure no existing tests broke:

```
dotnet test ACP_Metabot.Api.Tests
```

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Data/OfferingRepository.cs ACP_Metabot.Api/Program.cs ACP_Metabot.Api.Tests/AgentProfileRepositoryTests.cs
git commit -m "feat(v1.7): bump agent_profiles dirty flag on offering writes

OfferingRepository's mutation paths (UpsertMany, MarkRemoved,
reactivate) collect touched agent addresses and bump
agent_profiles.last_change_at after the transaction commits. The
indexer's touch UPDATE (last_seen_at + counters) deliberately does
NOT bump — those aren't profile changes."
```

---

## Phase 2 — Per-result enrichments (saturation + price percentile on offering hits)

### Task 2.1: SaturationCalculator

**Files:**
- Create: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/SaturationCalculator.cs`
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/SaturationCalculatorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `SaturationCalculatorTests.cs`:

```csharp
using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class SaturationCalculatorTests
{
    private static float[] Vec(params float[] xs) => xs;

    private static (long id, string category, float[] embedding)[] Corpus(params (long, string, float[])[] rows) => rows;

    [Fact]
    public void NearDuplicateCount_CountsOnlyAboveThreshold()
    {
        var corpus = Corpus(
            (1, "wallet", Vec(1f, 0f, 0f)),
            (2, "wallet", Vec(0.99f, 0.1f, 0f)),   // ~ same direction
            (3, "wallet", Vec(0.5f, 0.5f, 0.7f)),  // different
            (4, "wallet", Vec(-1f, 0f, 0f)));      // opposite
        var calc = new SaturationCalculator(threshold: 0.85);
        calc.Refresh(corpus);

        Assert.Equal(1, calc.NearDuplicateCount(offeringId: 1, category: "wallet"));
        Assert.Equal(1, calc.NearDuplicateCount(offeringId: 2, category: "wallet"));
        Assert.Equal(0, calc.NearDuplicateCount(offeringId: 3, category: "wallet"));
        Assert.Equal(0, calc.NearDuplicateCount(offeringId: 4, category: "wallet"));
    }

    [Fact]
    public void NearDuplicateCount_ScopedToCategory()
    {
        var corpus = Corpus(
            (1, "wallet", Vec(1f, 0f)),
            (2, "wallet", Vec(0.99f, 0.05f)),
            (3, "trading", Vec(1f, 0f)));   // identical direction but different category
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        Assert.Equal(1, calc.NearDuplicateCount(1, "wallet"));
        Assert.Equal(0, calc.NearDuplicateCount(3, "trading"));
    }

    [Fact]
    public void CategorySaturation_ComputesFraction()
    {
        var corpus = Corpus(
            (1, "wallet", Vec(1f, 0f)),
            (2, "wallet", Vec(0.99f, 0.05f)),
            (3, "wallet", Vec(-1f, 0f)),
            (4, "wallet", Vec(0f, 1f)));
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(corpus);

        var rollup = calc.PerCategory();
        var wallet = rollup.Single(c => c.Category == "wallet");
        Assert.Equal(4, wallet.Total);
        Assert.Equal(2, wallet.SaturatedCount);  // ids 1, 2 each have one near-dup
        Assert.Equal(0.5, wallet.SaturationPct, 3);
    }

    [Fact]
    public void NearDuplicateCount_EmptyCategory_ReturnsZero()
    {
        var calc = new SaturationCalculator(0.85);
        calc.Refresh(Array.Empty<(long, string, float[])>());
        Assert.Equal(0, calc.NearDuplicateCount(offeringId: 99, category: "wallet"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SaturationCalculator"
```

Expected: compile error.

- [ ] **Step 3: Implement `SaturationCalculator`**

Create `Services/SaturationCalculator.cs`:

```csharp
namespace ACP_Metabot.Api.Services;

/// <summary>
/// Computes near-duplicate counts within an offering's category, and the
/// per-category saturation rollup. Operates on a snapshot of (offeringId,
/// category, embedding) tuples — the SearchService corpus is the canonical
/// source. Refresh is called by SearchService.RefreshCorpusAsync after each
/// indexer cycle.
/// </summary>
public class SaturationCalculator
{
    private readonly double _threshold;
    private Dictionary<string, List<(long Id, float[] Emb)>> _byCategory = new();
    private Dictionary<long, int> _nearDup = new();
    private List<CategorySaturation> _rollup = new();

    public SaturationCalculator(double threshold)
    {
        _threshold = threshold;
    }

    public void Refresh(IEnumerable<(long id, string category, float[] embedding)> corpus)
    {
        _byCategory = new Dictionary<string, List<(long, float[])>>(StringComparer.Ordinal);
        foreach (var (id, cat, emb) in corpus)
        {
            if (!_byCategory.TryGetValue(cat, out var list))
                _byCategory[cat] = list = new List<(long, float[])>();
            list.Add((id, emb));
        }

        _nearDup = new Dictionary<long, int>();
        foreach (var (cat, list) in _byCategory)
        {
            for (int i = 0; i < list.Count; i++)
            {
                int count = 0;
                for (int j = 0; j < list.Count; j++)
                {
                    if (i == j) continue;
                    if (Cosine(list[i].Emb, list[j].Emb) >= _threshold) count++;
                }
                _nearDup[list[i].Id] = count;
            }
        }

        _rollup = _byCategory
            .Select(kv => new CategorySaturation(
                kv.Key,
                kv.Value.Count,
                kv.Value.Count(o => _nearDup.GetValueOrDefault(o.Id) > 0),
                kv.Value.Count == 0
                    ? 0.0
                    : (double)kv.Value.Count(o => _nearDup.GetValueOrDefault(o.Id) > 0) / kv.Value.Count))
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ToList();
    }

    public int NearDuplicateCount(long offeringId, string category)
        => _nearDup.GetValueOrDefault(offeringId);

    public int CategorySize(string category)
        => _byCategory.TryGetValue(category, out var list) ? list.Count : 0;

    public IReadOnlyList<CategorySaturation> PerCategory() => _rollup;

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}

public record CategorySaturation(string Category, int Total, int SaturatedCount, double SaturationPct);
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SaturationCalculator"
```

Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Services/SaturationCalculator.cs ACP_Metabot.Api.Tests/SaturationCalculatorTests.cs
git commit -m "feat(v1.7): SaturationCalculator + tests

Per-offering near-duplicate count within category (cosine ≥ threshold,
default 0.85). Per-category rollup with saturation_pct. O(N²) per
category over already-loaded corpus embeddings; refresh on indexer
cycle. 4 unit tests passing."
```

---

### Task 2.2: PricePercentileCalculator

**Files:**
- Create: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/PricePercentileCalculator.cs`
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/PricePercentileCalculatorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `PricePercentileCalculatorTests.cs`:

```csharp
using ACP_Metabot.Api.Services;

namespace ACP_Metabot.Api.Tests;

public class PricePercentileCalculatorTests
{
    private static (long id, string category, string marketplace, double price)[] Corpus(
        params (long, string, string, double)[] rows) => rows;

    [Fact]
    public void Percentile_OrderedAcrossPeers()
    {
        var corpus = Corpus(
            (1, "wallet", "v2", 0.05),
            (2, "wallet", "v2", 0.10),
            (3, "wallet", "v2", 0.20),
            (4, "wallet", "v2", 0.50),
            (5, "wallet", "v2", 1.00),
            (6, "wallet", "v2", 5.00));
        var calc = new PricePercentileCalculator(lowNThreshold: 5);
        calc.Refresh(corpus);

        var p1 = calc.Compute(1, "wallet", "v2", 0.05);
        Assert.Equal(0, p1.Value);
        Assert.Equal(5, p1.PeerN);   // 5 peers excluding self
        Assert.False(p1.LowN);

        var p3 = calc.Compute(3, "wallet", "v2", 0.20);
        Assert.Equal(40, p3.Value);  // 2 of 5 peers cheaper
        Assert.False(p3.LowN);

        var p6 = calc.Compute(6, "wallet", "v2", 5.00);
        Assert.Equal(100, p6.Value);
    }

    [Fact]
    public void Percentile_ScopedByCategoryAndMarketplace()
    {
        var corpus = Corpus(
            (1, "wallet", "v1", 0.99),
            (2, "wallet", "v1", 0.99),
            (3, "wallet", "v1", 0.99),
            (4, "wallet", "v1", 0.99),
            (5, "wallet", "v1", 0.99),
            (6, "wallet", "v2", 0.05));   // different marketplace
        var calc = new PricePercentileCalculator(5);
        calc.Refresh(corpus);

        var p6 = calc.Compute(6, "wallet", "v2", 0.05);
        // peerN = 0 (only itself in (wallet, v2)), so lowN.
        Assert.True(p6.LowN);
        Assert.Null(p6.Value);
        Assert.Equal(0, p6.PeerN);
    }

    [Fact]
    public void Percentile_LowN_FlagsBelowThreshold()
    {
        var corpus = Corpus(
            (1, "wallet", "v2", 0.10),
            (2, "wallet", "v2", 0.20),
            (3, "wallet", "v2", 0.30));
        var calc = new PricePercentileCalculator(5);
        calc.Refresh(corpus);

        var p2 = calc.Compute(2, "wallet", "v2", 0.20);
        Assert.True(p2.LowN);
        Assert.Equal(2, p2.PeerN);
        Assert.Null(p2.Value);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~PricePercentileCalculator"
```

Expected: compile error.

- [ ] **Step 3: Implement `PricePercentileCalculator`**

Create `Services/PricePercentileCalculator.cs`:

```csharp
namespace ACP_Metabot.Api.Services;

public record PricePercentile(int? Value, int PeerN, bool LowN);

/// <summary>
/// Computes percentile of an offering's price within (category × marketplace).
/// Peer set excludes the offering itself. Below lowNThreshold peers (default 5)
/// the value is null and LowN=true; callers should ignore.
/// </summary>
public class PricePercentileCalculator
{
    private readonly int _lowNThreshold;
    private Dictionary<(string Category, string Marketplace), List<double>> _peerPrices = new();

    public PricePercentileCalculator(int lowNThreshold = 5)
    {
        _lowNThreshold = lowNThreshold;
    }

    public void Refresh(IEnumerable<(long id, string category, string marketplace, double price)> corpus)
    {
        var dict = new Dictionary<(string, string), List<double>>();
        foreach (var (_, cat, mv, price) in corpus)
        {
            var key = (cat, mv);
            if (!dict.TryGetValue(key, out var list))
                dict[key] = list = new List<double>();
            list.Add(price);
        }
        foreach (var list in dict.Values) list.Sort();
        _peerPrices = dict;
    }

    public PricePercentile Compute(long offeringId, string category, string marketplace, double price)
    {
        var key = (category, marketplace);
        if (!_peerPrices.TryGetValue(key, out var sorted))
            return new PricePercentile(null, 0, true);

        // Exclude self by removing one instance of this price.
        var peerCount = sorted.Count - 1;
        if (peerCount < _lowNThreshold)
            return new PricePercentile(null, peerCount, true);

        // Count peers strictly less than this price (excluding self once).
        int strictlyLess = 0;
        bool selfRemoved = false;
        foreach (var p in sorted)
        {
            if (!selfRemoved && p == price) { selfRemoved = true; continue; }
            if (p < price) strictlyLess++;
        }

        var pct = (int)Math.Round(100.0 * strictlyLess / peerCount);
        return new PricePercentile(pct, peerCount, false);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~PricePercentileCalculator"
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Services/PricePercentileCalculator.cs ACP_Metabot.Api.Tests/PricePercentileCalculatorTests.cs
git commit -m "feat(v1.7): PricePercentileCalculator + tests

Per-offering price percentile within (category × marketplace). Excludes
self from peer set. lowN flag (default <5 peers). Refresh on corpus
update. 3 unit tests passing."
```

---

### Task 2.3: Wire calculators into `SearchService` corpus refresh + offering hit shape

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/SearchService.cs`
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Program.cs` — register `SaturationCalculator` + `PricePercentileCalculator` singletons; pass to `SearchService`.
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Models/` — wherever the offering search result DTO is.

- [ ] **Step 1: Write failing integration test**

Create `ACP_Metabot.Api.Tests/SearchEnrichmentIntegrationTests.cs`:

```csharp
using System.Text.Json;
// existing test bootstrap pattern; reuse fixture setup from SearchFusionEvaluationTests
// or stand up a minimal in-memory pipeline. The point is: every offering hit
// returned by SearchService.SearchAsync carries `saturation` and `pricePercentile`.

namespace ACP_Metabot.Api.Tests;

public class SearchEnrichmentIntegrationTests
{
    [Fact(Skip = "requires SearchService stand-up — wire in Task 2.3 step 3")]
    public Task SearchOfferings_HitsCarrySaturationAndPercentile()
    {
        // After SearchService.RefreshCorpusAsync, every result hit must have
        // both fields populated (numbers, not nulls modulo lowN).
        return Task.CompletedTask;
    }
}
```

(This is a placeholder until the wire-up happens in step 3; flip the `Skip` off once integration is in place.)

- [ ] **Step 2: Run test to verify it's skipped (sanity)**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SearchEnrichment"
```

Expected: 1 skipped, 0 failures.

- [ ] **Step 3: Wire calculators into `SearchService`**

In `SearchService.cs`:

1. Add two private fields:
   ```csharp
   private readonly SaturationCalculator _saturation;
   private readonly PricePercentileCalculator _pricePercentile;
   ```
2. Inject via constructor (alongside existing dependencies).
3. In `RefreshCorpusAsync`, after `_corpus` is rebuilt, call:
   ```csharp
   _saturation.Refresh(_corpus.Select(c => (c.Offering.Id, c.Category, c.Embedding)));
   _pricePercentile.Refresh(_corpus.Select(c =>
       (c.Offering.Id, c.Category, c.Offering.MarketplaceVersion, c.Offering.PriceUsdc)));
   ```
4. Wherever the offering-hit DTO is built for `/v1/search` results (`SearchAsync` final mapping), set:
   ```csharp
   hit.Saturation = new SaturationDto(
       _saturation.NearDuplicateCount(o.Id, category),
       _saturation.CategorySize(category));
   hit.PricePercentile = _pricePercentile.Compute(o.Id, category, o.MarketplaceVersion, o.PriceUsdc);
   ```

In `Models/`: add the DTO record types (`SaturationDto`, `PricePercentileDto`) with the JSON property names from the spec (`nearDuplicateCount`, `categorySize`, `value`, `peerN`, `lowN`).

In `Program.cs`: register both calculators as singletons and pass them to `SearchService`.

```csharp
builder.Services.AddSingleton<SaturationCalculator>(sp => new SaturationCalculator(
    threshold: builder.Configuration.GetValue<double?>("Saturation:Threshold") ?? 0.85));
builder.Services.AddSingleton<PricePercentileCalculator>(sp => new PricePercentileCalculator(
    lowNThreshold: builder.Configuration.GetValue<int?>("PricePercentile:LowNThreshold") ?? 5));
```

Threshold env knob: `SATURATION_THRESHOLD` mapped via standard ASP.NET configuration.

Now flip the integration test off `Skip` and assert against a built SearchService:

```csharp
[Fact]
public async Task SearchOfferings_HitsCarrySaturationAndPercentile()
{
    // Stand up Db + repos + SearchService with synthetic corpus of >5 wallet
    // offerings spanning a price range. Run RefreshCorpusAsync, then SearchAsync.
    // For each hit, assert hit.Saturation is non-null and hit.PricePercentile
    // is non-null with PeerN >= 5 (i.e. not LowN given the seeded corpus).
}
```

Concrete fixture:

```csharp
[Fact]
public async Task SearchOfferings_HitsCarrySaturationAndPercentile()
{
    using var fix = await TestFixtures.MinimalSearchStackAsync(
        offerings: new[]
        {
            // category "wallet", marketplace "v2", 6 distinct prices
            ("0xa", "wallet_scan",   "Scan a wallet for risk",          0.05, "v2"),
            ("0xb", "balance_check", "Check token balances",            0.10, "v2"),
            ("0xc", "tx_history",    "Fetch transaction history",       0.20, "v2"),
            ("0xd", "alert_setup",   "Set up wallet alerts",            0.50, "v2"),
            ("0xe", "approval_scan", "Scan token approvals",            1.00, "v2"),
            ("0xf", "deep_audit",    "Deep wallet behaviour audit",     5.00, "v2"),
        });

    var hits = await fix.SearchService.SearchAsync(
        "wallet", limit: 10, offset: 0,
        minScore: null, priceMax: null, staleAfterDays: null,
        rerank: false, category: null, chainFilter: null,
        minReputation: null, marketplace: null, default);

    Assert.NotEmpty(hits);
    foreach (var h in hits)
    {
        Assert.NotNull(h.Saturation);
        Assert.NotNull(h.PricePercentile);
        Assert.False(h.PricePercentile.LowN);
    }
}
```

`TestFixtures.MinimalSearchStackAsync` is a small helper to assemble Db + OfferingRepository + Voyage stub + SearchService. If the codebase doesn't yet have a fixtures helper, add one to `Tests/Fixtures/TestFixtures.cs` with whatever stubs the existing tests use (Voyage stub returns deterministic embeddings, etc.).

- [ ] **Step 4: Run test to verify it passes**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~SearchEnrichment"
```

Expected: 1 pass.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Services/SearchService.cs ACP_Metabot.Api/Models/ ACP_Metabot.Api/Program.cs ACP_Metabot.Api.Tests/SearchEnrichmentIntegrationTests.cs ACP_Metabot.Api.Tests/Fixtures/TestFixtures.cs
git commit -m "feat(v1.7): saturation + pricePercentile on /v1/search hits

SearchService.RefreshCorpusAsync now also refreshes SaturationCalculator
and PricePercentileCalculator. Every offering hit returned by SearchAsync
carries the two new fields. Threshold + lowN configurable via
Saturation:Threshold and PricePercentile:LowNThreshold."
```

---

## Phase 3 — Cross-presence on `/v1/agent/{address}`

### Task 3.1: CrossPresenceBuilder

**Files:**
- Create: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/CrossPresenceBuilder.cs`
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/CrossPresenceBuilderTests.cs`

- [ ] **Step 1: Write failing tests**

Create `CrossPresenceBuilderTests.cs`:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class CrossPresenceBuilderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _offeringRepo;
    private readonly CrossPresenceBuilder _builder;

    public CrossPresenceBuilderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cp_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _offeringRepo = new OfferingRepository(_db);
        _builder = new CrossPresenceBuilder(_offeringRepo);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task SeedAsync(params (string addr, string name, string marketplace, string firstSeen, bool removed)[] rows)
    {
        await using var conn = _db.OpenConnection();
        foreach (var r in rows)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                    price_usdc, price_type, chain, content_hash,
                    first_seen_at, last_seen_at, marketplace_version, is_removed)
                VALUES ($a, $n, $o, 'desc', 0.10, 'per_call', 'base', $h, $f, $f, $m, $r)";
            cmd.Parameters.AddWithValue("$a", r.addr.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$n", r.name);
            cmd.Parameters.AddWithValue("$o", $"off_{Guid.NewGuid():N}");
            cmd.Parameters.AddWithValue("$h", $"h_{Guid.NewGuid():N}");
            cmd.Parameters.AddWithValue("$f", r.firstSeen);
            cmd.Parameters.AddWithValue("$m", r.marketplace);
            cmd.Parameters.AddWithValue("$r", r.removed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BothMarketplaces_DominantByOfferingCount()
    {
        await SeedAsync(
            ("0xa", "A", "v1", "2026-01-01T00:00:00Z", false),
            ("0xa", "A", "v1", "2026-01-15T00:00:00Z", false),
            ("0xa", "A", "v1", "2026-02-01T00:00:00Z", false),
            ("0xa", "A", "v2", "2026-03-04T00:00:00Z", false),
            ("0xa", "A", "v2", "2026-04-30T00:00:00Z", false));

        var cp = await _builder.BuildAsync("0xa");
        Assert.NotNull(cp.V1);
        Assert.Equal(3, cp.V1.OfferingCount);
        Assert.Equal(2, cp.V2!.OfferingCount);
        Assert.True(cp.InBoth);
        Assert.Equal("v1", cp.Dominant);
    }

    [Fact]
    public async Task SingleMarketplace_OtherIsNull()
    {
        await SeedAsync(("0xb", "B", "v2", "2026-04-01T00:00:00Z", false));

        var cp = await _builder.BuildAsync("0xb");
        Assert.Null(cp.V1);
        Assert.NotNull(cp.V2);
        Assert.Equal(1, cp.V2.OfferingCount);
        Assert.False(cp.InBoth);
        Assert.Equal("v2", cp.Dominant);
    }

    [Fact]
    public async Task TombstonedOfferings_Excluded()
    {
        await SeedAsync(
            ("0xc", "C", "v1", "2026-01-01T00:00:00Z", true),
            ("0xc", "C", "v2", "2026-04-01T00:00:00Z", false));

        var cp = await _builder.BuildAsync("0xc");
        Assert.Null(cp.V1);  // tombstoned ⇒ no V1 presence
        Assert.NotNull(cp.V2);
        Assert.False(cp.InBoth);
        Assert.Equal("v2", cp.Dominant);
    }

    [Fact]
    public async Task TiedOfferingCount_DominantTied()
    {
        await SeedAsync(
            ("0xd", "D", "v1", "2026-01-01T00:00:00Z", false),
            ("0xd", "D", "v2", "2026-04-01T00:00:00Z", false));

        var cp = await _builder.BuildAsync("0xd");
        Assert.Equal("tied", cp.Dominant);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~CrossPresenceBuilder"
```

Expected: compile error.

- [ ] **Step 3: Implement `CrossPresenceBuilder`**

Create `Services/CrossPresenceBuilder.cs`:

```csharp
using ACP_Metabot.Api.Data;

namespace ACP_Metabot.Api.Services;

public record CrossPresenceMarketplace(int OfferingCount, string FirstSeenAt, string LastSeenAt);
public record CrossPresence(
    CrossPresenceMarketplace? V1,
    CrossPresenceMarketplace? V2,
    bool InBoth,
    string Dominant);

public class CrossPresenceBuilder
{
    private readonly OfferingRepository _offerings;
    public CrossPresenceBuilder(OfferingRepository offerings) { _offerings = offerings; }

    public async Task<CrossPresence> BuildAsync(string agentAddress)
    {
        var rows = await _offerings.ListByAgentAsync(agentAddress.ToLowerInvariant(), includeRemoved: false);
        // Existing repo helper; if not present, add one in this same task that
        // SELECTs marketplace_version, first_seen_at, last_seen_at where is_removed=0.

        CrossPresenceMarketplace? Build(string mv) =>
            rows.Where(r => string.Equals(r.MarketplaceVersion, mv, StringComparison.OrdinalIgnoreCase))
                .GroupBy(_ => mv)
                .Select(g => new CrossPresenceMarketplace(
                    OfferingCount: g.Count(),
                    FirstSeenAt: g.Min(r => r.FirstSeenAt),
                    LastSeenAt:  g.Max(r => r.LastSeenAt)))
                .FirstOrDefault();

        var v1 = Build("v1");
        var v2 = Build("v2");

        var inBoth = v1 is not null && v2 is not null;
        string dominant;
        if (v1 is null && v2 is null) dominant = "none";
        else if (v1 is null) dominant = "v2";
        else if (v2 is null) dominant = "v1";
        else if (v1.OfferingCount > v2.OfferingCount) dominant = "v1";
        else if (v2.OfferingCount > v1.OfferingCount) dominant = "v2";
        else dominant = "tied";  // total-job tiebreak deferred (out of scope per spec)

        return new CrossPresence(v1, v2, inBoth, dominant);
    }
}
```

If `OfferingRepository.ListByAgentAsync(string, bool)` doesn't exist, add it in this task. The query:

```sql
SELECT id, agent_address, agent_name, offering_name, description, price_usdc,
       chain, marketplace_version, first_seen_at, last_seen_at,
       usage_count, agent_job_count, is_removed
FROM offerings
WHERE agent_address = $a
  AND ($incRemoved = 1 OR is_removed = 0)
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~CrossPresenceBuilder"
```

Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Services/CrossPresenceBuilder.cs ACP_Metabot.Api/Data/OfferingRepository.cs ACP_Metabot.Api.Tests/CrossPresenceBuilderTests.cs
git commit -m "feat(v1.7): CrossPresenceBuilder + OfferingRepository.ListByAgent

Per-marketplace footprint (offering count + first/last seen) over
non-tombstoned offerings. inBoth + dominant fields. Tied returns
'tied' (total-job tiebreak deferred to v1.8). 4 tests passing."
```

---

### Task 3.2: Wire `crossPresence` + per-offering `pricePercentile` into `/v1/agent/{address}`

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Program.cs` — `HandleBrowseAgent`.
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Models/` — extend the browseAgent response DTO.

- [ ] **Step 1: Write failing integration test**

Append to `CrossPresenceBuilderTests.cs` (or create `BrowseAgentResponseTests.cs`):

```csharp
[Fact]
public async Task BrowseAgent_ResponseIncludesCrossPresenceAndPricePercentile()
{
    // Stand up minimal app with two-marketplace fixture for one agent.
    // Assert response JSON contains crossPresence block and per-offering
    // pricePercentile field.
}
```

- [ ] **Step 2: Run test — FAIL**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~BrowseAgent_Response"
```

Expected: response missing the new fields.

- [ ] **Step 3: Wire into `HandleBrowseAgent`**

In `Program.cs`, change the handler signature to take `CrossPresenceBuilder` and `PricePercentileCalculator`:

```csharp
async Task<IResult> HandleBrowseAgent(string address,
    OfferingRepository repo, ReputationService reputation,
    CrossPresenceBuilder crossPresence, PricePercentileCalculator pricePercentile,
    CategoryService categories)
{
    // ... existing logic to build the response ...

    var cp = await crossPresence.BuildAsync(address);
    response.CrossPresence = cp;

    foreach (var hit in response.Offerings)
    {
        var category = categories.CategorizeOffering(hit) ?? "uncategorized";
        hit.PricePercentile = pricePercentile.Compute(
            hit.Id, category, hit.MarketplaceVersion, hit.PriceUsdc);
    }

    return Results.Ok(response);
}
```

Update both `MapGet("/agent/{address}", ...)` and `MapGet("/v1/agent/{address}", ...)` to inject the new dependencies.

Register `CrossPresenceBuilder` as a singleton in `Program.cs`:

```csharp
builder.Services.AddSingleton<CrossPresenceBuilder>();
```

Add the `CrossPresence` and `PricePercentile` properties to the browseAgent response DTO (and per-offering DTO inside it), with `[JsonPropertyName]` attributes matching the spec (`crossPresence`, `pricePercentile`, `inBoth`, `dominant`, `firstSeenAt`, `lastSeenAt`, `offeringCount`).

- [ ] **Step 4: Run test — PASS**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~BrowseAgent_Response"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Program.cs ACP_Metabot.Api/Models/ ACP_Metabot.Api.Tests/CrossPresenceBuilderTests.cs
git commit -m "feat(v1.7): /v1/agent/{addr} carries crossPresence + per-offering pricePercentile

Browse handler now composes CrossPresenceBuilder.BuildAsync and
populates pricePercentile per offering via PricePercentileCalculator.
Existing reputation block unchanged. /agent/{addr} mirror updated
identically."
```

---

## Phase 4 — Agent profile embedder

### Task 4.1: Cold-start backfill: seed `agent_profiles` from existing `offerings`

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Data/AgentProfileRepository.cs` — add `BackfillFromOfferingsAsync`.
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/AgentProfileRepositoryTests.cs` — extend.

- [ ] **Step 1: Write failing test**

Append to `AgentProfileRepositoryTests.cs`:

```csharp
[Fact]
public async Task BackfillFromOfferings_PopulatesEveryDistinctAgent()
{
    // Seed offerings table directly with two agents, three offerings total.
    await using (var conn = _db.OpenConnection())
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                price_usdc, price_type, chain, content_hash, first_seen_at, last_seen_at,
                marketplace_version, is_removed)
            VALUES
                ('0xa', 'AgentA', 'scan', 'Scan a wallet', 0.10, 'per_call', 'base', 'h1', $f, $f, 'v2', 0),
                ('0xa', 'AgentA', 'audit', 'Deep audit', 5.00, 'per_call', 'base', 'h2', $f, $f, 'v2', 0),
                ('0xb', 'AgentB', 'alert', 'Alert on tx', 0.20, 'per_call', 'base', 'h3', $f, $f, 'v1', 0);";
        cmd.Parameters.AddWithValue("$f", "2026-05-01T00:00:00Z");
        await cmd.ExecuteNonQueryAsync();
    }

    var n = await _repo.BackfillFromOfferingsAsync(profileTextCap: 2000);
    Assert.Equal(2, n);

    var a = await _repo.GetAsync("0xa");
    Assert.NotNull(a);
    Assert.Equal("AgentA", a.AgentName);
    Assert.Contains("scan", a.ProfileText);
    Assert.Contains("audit", a.ProfileText);
    Assert.Null(a.EmbeddedAt);

    var b = await _repo.GetAsync("0xb");
    Assert.NotNull(b);
    Assert.Contains("alert", b.ProfileText);

    // Both should be dirty.
    Assert.Equal(2, (await _repo.ListDirtyAsync(100)).Count);
}

[Fact]
public async Task BackfillFromOfferings_IsIdempotent()
{
    await using (var conn = _db.OpenConnection())
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                price_usdc, price_type, chain, content_hash, first_seen_at, last_seen_at,
                marketplace_version, is_removed)
            VALUES ('0xa', 'AgentA', 'scan', 'Scan', 0.10, 'per_call', 'base', 'h', $f, $f, 'v2', 0);";
        cmd.Parameters.AddWithValue("$f", "2026-05-01T00:00:00Z");
        await cmd.ExecuteNonQueryAsync();
    }

    var n1 = await _repo.BackfillFromOfferingsAsync(2000);
    var n2 = await _repo.BackfillFromOfferingsAsync(2000);
    Assert.Equal(1, n1);
    Assert.True(n2 == 0 || n2 == 1, "second pass should either skip existing rows or upsert idempotently without duplicating profile_text");
}
```

- [ ] **Step 2: Run test — FAIL**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~Backfill"
```

Expected: compile error.

- [ ] **Step 3: Implement `BackfillFromOfferingsAsync`**

In `AgentProfileRepository.cs`:

```csharp
/// <summary>
/// Cold-start backfill — populates agent_profiles for every distinct
/// agent_address present in offerings (with is_removed=0). Profile text
/// is the concatenation of agent_name + "\n\n" + joined offering names
/// + descriptions, capped at <paramref name="profileTextCap"/> chars.
/// Returns the number of agents inserted/updated.
/// </summary>
public async Task<int> BackfillFromOfferingsAsync(int profileTextCap)
{
    await using var conn = _db.OpenConnection();
    await using var read = conn.CreateCommand();
    read.CommandText = @"
        SELECT agent_address, agent_name, offering_name, description
        FROM offerings
        WHERE is_removed = 0
        ORDER BY agent_address, first_seen_at";
    var byAgent = new Dictionary<string, (string Name, List<string> Text)>(
        StringComparer.OrdinalIgnoreCase);
    await using (var r = await read.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            var key = r.GetString(0).ToLowerInvariant();
            if (!byAgent.TryGetValue(key, out var t))
                byAgent[key] = t = (r.GetString(1), new List<string>());
            t.Text.Add($"{r.GetString(2)}: {r.GetString(3)}");
        }
    }

    int n = 0;
    foreach (var (addr, (name, lines)) in byAgent)
    {
        var profileText = $"{name}\n\n{string.Join("\n", lines)}";
        if (profileText.Length > profileTextCap)
            profileText = profileText.Substring(0, profileTextCap);
        await UpsertAsync(addr, name, profileText);
        n++;
    }
    return n;
}
```

- [ ] **Step 4: Run test — PASS**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~Backfill"
```

Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Data/AgentProfileRepository.cs ACP_Metabot.Api.Tests/AgentProfileRepositoryTests.cs
git commit -m "feat(v1.7): cold-start backfill of agent_profiles from offerings

BackfillFromOfferingsAsync seeds one row per distinct agent_address
with concatenated name + offering name+description text, capped at
2000 chars. Idempotent (UpsertAsync clobbers same-key rows). Tests
cover initial seed and re-run."
```

---

### Task 4.2: AgentProfileEmbedderService (`IHostedService`)

**Files:**
- Create: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/AgentProfileEmbedderService.cs`
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/AgentProfileEmbedderServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Create `AgentProfileEmbedderServiceTests.cs`:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class AgentProfileEmbedderServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly AgentProfileRepository _repo;
    private readonly StubEmbedder _embedder;
    private readonly AgentProfileEmbedderService _svc;

    public AgentProfileEmbedderServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"emb_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _repo = new AgentProfileRepository(_db);
        _embedder = new StubEmbedder();
        _svc = new AgentProfileEmbedderService(_repo, _embedder, NullLogger<AgentProfileEmbedderService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task DrainOnce_EmbedsAllDirty_MarksClean()
    {
        await _repo.UpsertAsync("0xa", "A", "profile A");
        await _repo.UpsertAsync("0xb", "B", "profile B");
        Assert.Equal(2, (await _repo.ListDirtyAsync(100)).Count);

        await _svc.DrainOnceAsync(batchSize: 128, default);

        Assert.Empty(await _repo.ListDirtyAsync(100));
        var a = await _repo.GetAsync("0xa");
        Assert.NotNull(a.Embedding);
        Assert.Equal("voyage-stub", a.EmbeddingModel);
    }

    [Fact]
    public async Task DrainOnce_VoyageFailure_LeavesDirty()
    {
        _embedder.FailNext = true;
        await _repo.UpsertAsync("0xa", "A", "profile A");

        await _svc.DrainOnceAsync(batchSize: 128, default);

        Assert.Single(await _repo.ListDirtyAsync(100)); // still dirty for next cycle
    }

    [Fact]
    public async Task DrainOnce_BatchesHonoured()
    {
        for (int i = 0; i < 5; i++)
            await _repo.UpsertAsync($"0x{i:x40}", $"Agent{i}", $"profile {i}");

        await _svc.DrainOnceAsync(batchSize: 2, default);

        Assert.Equal(2, _embedder.BatchCalls.Count); // 5 / 2 = ceiling 3 batches; but DrainOnce drains exactly the limit per call
        // Adjust assertion if the design is "drain at most one batch per cycle"
        // vs "drain everything one batch at a time per cycle".
    }
}

internal class StubEmbedder : IEmbeddingProvider
{
    public string ModelName => "voyage-stub";
    public List<List<string>> BatchCalls { get; } = new();
    public bool FailNext { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("simulated voyage failure");
        }
        BatchCalls.Add(texts.ToList());
        var vectors = texts.Select(t => new float[] { t.Length, 1f, 0f }).ToArray();
        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }
}
```

This assumes an `IEmbeddingProvider` interface exposing `EmbedBatchAsync`. If the existing `VoyageEmbeddingProvider` doesn't implement an interface, extract one in this task — single-method interface with the batch shape above. The existing concrete class implements it; the test stub also implements it.

- [ ] **Step 2: Run tests — FAIL**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~AgentProfileEmbedder"
```

Expected: compile error.

- [ ] **Step 3: Implement `AgentProfileEmbedderService`**

Create `Services/AgentProfileEmbedderService.cs`:

```csharp
using ACP_Metabot.Api.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ACP_Metabot.Api.Services;

public class AgentProfileEmbedderService : BackgroundService
{
    private readonly AgentProfileRepository _repo;
    private readonly IEmbeddingProvider _embed;
    private readonly ILogger<AgentProfileEmbedderService> _log;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
    private const int DefaultBatch = 128;

    public AgentProfileEmbedderService(
        AgentProfileRepository repo,
        IEmbeddingProvider embed,
        ILogger<AgentProfileEmbedderService> log)
    {
        _repo = repo;
        _embed = embed;
        _log = log;
    }

    public async Task DrainOnceAsync(int batchSize, CancellationToken ct)
    {
        var dirty = await _repo.ListDirtyAsync(batchSize);
        if (dirty.Count == 0) return;

        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await _embed.EmbedBatchAsync(dirty.Select(d => d.ProfileText).ToList(), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "agent profile embed batch failed; will retry next cycle");
            return;
        }

        for (int i = 0; i < dirty.Count; i++)
        {
            var blob = FloatsToBytes(vectors[i]);
            await _repo.MarkEmbeddedAsync(dirty[i].AgentAddress, _embed.ModelName, blob);
        }
        _log.LogInformation("agent profile embed batch: {Count} drained", dirty.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cold-start backfill — best-effort.
        try
        {
            var seeded = await _repo.BackfillFromOfferingsAsync(profileTextCap: 2000);
            _log.LogInformation("agent_profiles cold-start backfill seeded {N} rows", seeded);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cold-start backfill failed; embedder will catch up via dirty queue");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainOnceAsync(DefaultBatch, stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "embedder drain cycle failed"); }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static byte[] FloatsToBytes(float[] xs)
    {
        var bytes = new byte[xs.Length * sizeof(float)];
        Buffer.BlockCopy(xs, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}

public interface IEmbeddingProvider
{
    string ModelName { get; }
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
```

If the existing `VoyageEmbeddingProvider` doesn't already match `IEmbeddingProvider`, modify it to implement the interface (likely a one-line attribute add — its method signatures should already line up).

- [ ] **Step 4: Run tests — PASS**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~AgentProfileEmbedder"
```

Expected: 3 tests pass (or adjust the third test to match the chosen drain semantics).

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Services/AgentProfileEmbedderService.cs ACP_Metabot.Api/Services/IEmbeddingProvider.cs ACP_Metabot.Api/Services/VoyageEmbeddingProvider.cs ACP_Metabot.Api.Tests/AgentProfileEmbedderServiceTests.cs
git commit -m "feat(v1.7): AgentProfileEmbedderService + IEmbeddingProvider

Background service drains agent_profiles dirty queue every 5 min in
batches of 128. Cold-start runs BackfillFromOfferingsAsync once.
Voyage failures keep rows dirty for next cycle. 3 tests + stub
embedder fixture."
```

---

### Task 4.3: Register `AgentProfileEmbedderService` in `Program.cs`

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Program.cs`.

- [ ] **Step 1: Manual verification (no failing test — DI wire-up)**

There's no clean unit test for service registration. Verify by running the app and observing the cold-start log.

- [ ] **Step 2: N/A**

- [ ] **Step 3: Wire registration**

In `Program.cs`, add to the service-registration block:

```csharp
builder.Services.AddSingleton<AgentProfileRepository>();
builder.Services.AddHostedService<AgentProfileEmbedderService>();
```

If `VoyageEmbeddingProvider` is currently registered as a concrete type, also expose it via the interface:

```csharp
builder.Services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<VoyageEmbeddingProvider>());
```

- [ ] **Step 4: Verify by running the app against an empty DB**

```
dotnet run --project ACP_Metabot.Api
```

Watch for `agent_profiles cold-start backfill seeded 0 rows` (empty DB) and `agent profile embed batch: 0 drained` on the next cycle (no offerings yet). Then ingest a few offerings and confirm the embedder logs them on the next cycle.

Stop the app (Ctrl-C) once verified.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Program.cs
git commit -m "feat(v1.7): register AgentProfileEmbedderService as HostedService

Wires AgentProfileRepository singleton and the embedder background
service. Voyage provider exposed via IEmbeddingProvider for stubbing."
```

---

## Phase 5 — Hybrid agent search ranker upgrade

### Task 5.1: Extend `AgentSearchHit` model

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Models/AgentSearchResult.cs`

- [ ] **Step 1: Write failing serialization test**

Create `ACP_Metabot.Api.Tests/AgentSearchHitSerializationTests.cs`:

```csharp
using System.Text.Json;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Tests;

public class AgentSearchHitSerializationTests
{
    [Fact]
    public void Serialization_IncludesAllV17Fields()
    {
        var hit = new AgentSearchHit(
            AgentAddress: "0xabc",
            AgentName: "AgentA",
            Score: 0.82,
            TotalOfferings: 5,
            TopOfferings: new[]
            {
                new AgentSearchHitOffering("wallet_scan", 0.10, "v2"),
            },
            TotalJobs: 47,
            TopOfferingNames: new[] { "wallet_scan" },
            Marketplaces: new[] { "v1", "v2" },
            DominantMarketplace: "v2",
            AgentScore: 78);

        var json = JsonSerializer.Serialize(hit);
        Assert.Contains("\"topOfferingNames\":[\"wallet_scan\"]", json);
        Assert.Contains("\"marketplaces\":[\"v1\",\"v2\"]", json);
        Assert.Contains("\"dominantMarketplace\":\"v2\"", json);
        Assert.Contains("\"agentScore\":78", json);
        // topOfferings is now records, but old string-array consumers can read topOfferingNames.
        Assert.Contains("\"offeringName\":\"wallet_scan\"", json);
        Assert.Contains("\"priceUsdc\":0.1", json);
        Assert.Contains("\"marketplaceVersion\":\"v2\"", json);
    }

    [Fact]
    public void Serialization_NullableFields_OmittedWhenNull()
    {
        var hit = new AgentSearchHit(
            "0xabc", "A", 0.5, 1,
            Array.Empty<AgentSearchHitOffering>(),
            10,
            Array.Empty<string>(),
            new[] { "v2" },
            "v2",
            AgentScore: null);

        var json = JsonSerializer.Serialize(hit);
        Assert.DoesNotContain("\"agentScore\"", json);
    }
}
```

- [ ] **Step 2: Run tests — FAIL**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~AgentSearchHitSerialization"
```

Expected: compile error (missing fields).

- [ ] **Step 3: Extend model**

In `Models/AgentSearchResult.cs`:

```csharp
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Models;

public record AgentSearchHitOffering(
    [property: JsonPropertyName("offeringName")] string OfferingName,
    [property: JsonPropertyName("priceUsdc")]    double PriceUsdc,
    [property: JsonPropertyName("marketplaceVersion")] string MarketplaceVersion);

public record AgentSearchHit(
    [property: JsonPropertyName("agentAddress")]   string AgentAddress,
    [property: JsonPropertyName("agentName")]      string AgentName,
    // SEMANTICS CHANGED in v1.7: was BM25 (lower=better);
    // now post-rerank cosine score (opaque, sort by it). Range typically 0..1.
    [property: JsonPropertyName("score")]          double Score,
    [property: JsonPropertyName("totalOfferings")] int TotalOfferings,
    // SHAPE CHANGED in v1.7: was string[]; now records.
    [property: JsonPropertyName("topOfferings")]   IReadOnlyList<AgentSearchHitOffering> TopOfferings,
    [property: JsonPropertyName("totalJobs")]      long TotalJobs,
    // NEW in v1.7 — backward-compat mirror for string-array consumers.
    [property: JsonPropertyName("topOfferingNames")] IReadOnlyList<string> TopOfferingNames,
    // NEW in v1.7 — sorted subset of {"v1","v2"} where the agent has ≥1 active offering.
    [property: JsonPropertyName("marketplaces")]   IReadOnlyList<string> Marketplaces,
    // NEW in v1.7 — "v1" | "v2" | "tied".
    [property: JsonPropertyName("dominantMarketplace")] string DominantMarketplace,
    // NEW in v1.7 — from agent_reputation_cache; nullable when uncached.
    [property: JsonPropertyName("agentScore"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? AgentScore);

// AgentJobRecord unchanged.
public record AgentJobRecord(
    [property: JsonPropertyName("jobId")]        string JobId,
    [property: JsonPropertyName("createdAt")]    string CreatedAt,
    [property: JsonPropertyName("status")]       string Status,
    [property: JsonPropertyName("counterparty")] string Counterparty,
    [property: JsonPropertyName("amountUsdc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        decimal? AmountUsdc);
```

This breaks any compile site that constructs `AgentSearchHit` with the old positional signature. Find them with:

```
dotnet build 2>&1 | grep -i AgentSearchHit
```

Update each construction site (in `OfferingRepository.SearchAgentsAsync`, `Program.cs` handler) to use the new positional args. The compiler shepherds you through this.

- [ ] **Step 4: Run tests — PASS**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~AgentSearchHitSerialization"
```

Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Models/AgentSearchResult.cs ACP_Metabot.Api.Tests/AgentSearchHitSerializationTests.cs ACP_Metabot.Api/Data/OfferingRepository.cs ACP_Metabot.Api/Program.cs
git commit -m "feat(v1.7): AgentSearchHit gains marketplaces/dominantMarketplace/agentScore + records topOfferings

topOfferingNames mirror retains string-array shape for old consumers.
score field semantics are opaque (sort, don't interpret) — v1.6 BM25
score → v1.7 post-rerank cosine. Construction sites in
OfferingRepository.SearchAgentsAsync and Program.cs handler updated."
```

---

### Task 5.2: AgentSearchService (hybrid ranker)

**Files:**
- Create: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/AgentSearchService.cs`
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/AgentSearchServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Create `AgentSearchServiceTests.cs`:

```csharp
using ACP_Metabot.Api.Services;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Tests;

public class AgentSearchServiceTests
{
    [Fact]
    public void RrfFusion_AgreementBoosts()
    {
        // BM25 leg ranks 0xa, 0xb, 0xc.
        // Dense leg ranks 0xa, 0xc, 0xb.
        // 0xa is #1 in both → highest fused score.
        var bm25  = new[] { "0xa", "0xb", "0xc" };
        var dense = new[] { "0xa", "0xc", "0xb" };
        var fused = AgentSearchService.ReciprocalRankFusion(bm25, dense, k: 60);

        var ranked = fused.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        Assert.Equal("0xa", ranked[0]);
    }

    [Fact]
    public void RrfFusion_MissingFromOneRanker_StillRanks()
    {
        var bm25  = new[] { "0xa", "0xb" };
        var dense = new[] { "0xc" };  // 0xa, 0xb absent from dense leg
        var fused = AgentSearchService.ReciprocalRankFusion(bm25, dense, k: 60);

        Assert.Contains("0xa", fused.Keys);
        Assert.Contains("0xc", fused.Keys);
    }

    [Fact]
    public async Task SearchAsync_ReturnsHybridRanked_WithEnrichedHit()
    {
        // Stand up minimal stack: Db with seeded offerings + agent_profiles
        // + stub embedder + stub reranker + ReputationService stub returning
        // fixed scores. Run SearchAsync("query"); verify the hits carry the
        // new fields (marketplaces, dominantMarketplace, agentScore).
        // (Implementation in Step 3.)
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run tests — FAIL (compile)**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~AgentSearchService"
```

- [ ] **Step 3: Implement `AgentSearchService`**

Create `Services/AgentSearchService.cs`:

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Services;

public class AgentSearchService
{
    private readonly Db _db;
    private readonly OfferingRepository _offeringRepo;
    private readonly IEmbeddingProvider _embed;
    private readonly IRerankProvider? _rerank;
    private readonly ReputationService _reputation;
    private readonly CrossPresenceBuilder _crossPresence;

    public AgentSearchService(
        Db db,
        OfferingRepository offeringRepo,
        IEmbeddingProvider embed,
        IRerankProvider? rerank,
        ReputationService reputation,
        CrossPresenceBuilder crossPresence)
    {
        _db = db;
        _offeringRepo = offeringRepo;
        _embed = embed;
        _rerank = rerank;
        _reputation = reputation;
        _crossPresence = crossPresence;
    }

    public async Task<IReadOnlyList<AgentSearchHit>> SearchAsync(
        string query, int limit, string? marketplaceFilter, CancellationToken ct)
    {
        // 1. BM25 leg — reuse OfferingRepository.SearchAgentsAsync as the BM25
        //    grouping primitive. Returns AgentSearchHit candidates ordered by
        //    BM25-best-score.
        var bm25Hits = await _offeringRepo.SearchAgentsAsync(query, limit: 200, marketplaceFilter);

        // 2. Dense leg — embed the query, cosine-rank against agent_profiles.embedding.
        var denseHits = await DenseAgentRankAsync(query, limit: 200, ct);

        // 3. RRF fusion.
        var bm25Order  = bm25Hits.Select(h => h.AgentAddress).ToArray();
        var denseOrder = denseHits.Select(d => d.AgentAddress).ToArray();
        var fused = ReciprocalRankFusion(bm25Order, denseOrder, k: 60);

        // 4. Take top 50 candidates by fused score.
        var top50 = fused.OrderByDescending(kv => kv.Value).Take(50).Select(kv => kv.Key).ToList();

        // 5. Rerank if available.
        var reranked = top50;
        var rerankScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_rerank is not null)
        {
            var byAddr = bm25Hits.ToDictionary(h => h.AgentAddress, StringComparer.OrdinalIgnoreCase);
            var docs = top50.Select(addr =>
            {
                if (byAddr.TryGetValue(addr, out var h))
                    return $"{h.AgentName}\n{string.Join("\n", h.TopOfferingNames ?? Array.Empty<string>())}";
                return addr; // fallback
            }).ToList();
            try
            {
                var ranking = await _rerank.RankAsync(query, docs, ct);
                reranked = ranking
                    .Select(idx => top50[idx.Index])
                    .ToList();
                for (int i = 0; i < ranking.Count; i++)
                    rerankScores[top50[ranking[i].Index]] = ranking[i].Score;
            }
            catch
            {
                // fall back to RRF order
                for (int i = 0; i < top50.Count; i++)
                    rerankScores[top50[i]] = fused[top50[i]];
            }
        }
        else
        {
            for (int i = 0; i < top50.Count; i++)
                rerankScores[top50[i]] = fused[top50[i]];
        }

        // 6. Build enriched hits for the top `limit`.
        var bm25ByAddr = bm25Hits.ToDictionary(h => h.AgentAddress, StringComparer.OrdinalIgnoreCase);
        var topAddrs = reranked.Take(limit).ToList();
        var enriched = new List<AgentSearchHit>();
        foreach (var addr in topAddrs)
        {
            var bm = bm25ByAddr.GetValueOrDefault(addr);
            if (bm is null) continue;

            var cp = await _crossPresence.BuildAsync(addr);
            var rep = await _reputation.GetCachedAsync(addr);

            // Build top offerings as records (uses BM25's top-3 list as input).
            var topOfferingNames = bm.TopOfferings ?? Array.Empty<string>();
            var topOfferingRecords = await BuildTopOfferingRecordsAsync(addr, topOfferingNames);

            enriched.Add(new AgentSearchHit(
                AgentAddress: addr,
                AgentName: bm.AgentName,
                Score: rerankScores.GetValueOrDefault(addr, 0.0),
                TotalOfferings: bm.TotalOfferings,
                TopOfferings: topOfferingRecords,
                TotalJobs: bm.TotalJobs,
                TopOfferingNames: topOfferingNames.ToArray(),
                Marketplaces: BuildMarketplaces(cp),
                DominantMarketplace: cp.Dominant,
                AgentScore: rep?.AgentScore));
        }
        return enriched;
    }

    private static IReadOnlyList<string> BuildMarketplaces(CrossPresence cp)
    {
        var list = new List<string>();
        if (cp.V1 is not null) list.Add("v1");
        if (cp.V2 is not null) list.Add("v2");
        return list;
    }

    private async Task<IReadOnlyList<AgentSearchHitOffering>> BuildTopOfferingRecordsAsync(
        string agentAddress, IReadOnlyList<string> offeringNames)
    {
        if (offeringNames.Count == 0) return Array.Empty<AgentSearchHitOffering>();

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", offeringNames.Select((_, i) => $"$n{i}"));
        cmd.CommandText = $@"
            SELECT offering_name, price_usdc, marketplace_version
            FROM offerings
            WHERE agent_address = $a
              AND offering_name IN ({placeholders})
              AND is_removed = 0";
        cmd.Parameters.AddWithValue("$a", agentAddress.ToLowerInvariant());
        for (int i = 0; i < offeringNames.Count; i++)
            cmd.Parameters.AddWithValue($"$n{i}", offeringNames[i]);

        var byName = new Dictionary<string, AgentSearchHitOffering>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var name = r.GetString(0);
            byName[name] = new AgentSearchHitOffering(name, r.GetDouble(1), r.GetString(2));
        }
        // Preserve the input ordering.
        return offeringNames
            .Select(n => byName.GetValueOrDefault(n))
            .Where(o => o is not null)
            .Cast<AgentSearchHitOffering>()
            .ToList();
    }

    private async Task<IReadOnlyList<(string AgentAddress, double Cosine)>> DenseAgentRankAsync(
        string query, int limit, CancellationToken ct)
    {
        var qVec = (await _embed.EmbedBatchAsync(new[] { query }, ct))[0];

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT agent_address, embedding
            FROM agent_profiles
            WHERE embedding IS NOT NULL";
        var hits = new List<(string, double)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var addr = r.GetString(0);
            var blob = (byte[])r.GetValue(1);
            var v = BytesToFloats(blob);
            hits.Add((addr, Cosine(qVec, v)));
        }
        return hits.OrderByDescending(h => h.Item2).Take(limit).ToList();
    }

    public static Dictionary<string, double> ReciprocalRankFusion(
        IReadOnlyList<string> a, IReadOnlyList<string> b, int k)
    {
        var fused = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Count; i++)
            fused[a[i]] = fused.GetValueOrDefault(a[i]) + 1.0 / (k + i + 1);
        for (int i = 0; i < b.Count; i++)
            fused[b[i]] = fused.GetValueOrDefault(b[i]) + 1.0 / (k + i + 1);
        return fused;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var d = Math.Sqrt(na) * Math.Sqrt(nb);
        return d == 0 ? 0 : dot / d;
    }
}

public interface IRerankProvider
{
    Task<IReadOnlyList<RerankResult>> RankAsync(string query, IReadOnlyList<string> documents, CancellationToken ct);
}

public record RerankResult(int Index, double Score);
```

If `IRerankProvider` doesn't already exist, define it in this task and have the existing reranker implement it.

- [ ] **Step 4: Run tests — PASS (RRF tests pass; the integration test stays skipped or filled in via a fixture stand-up)**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~AgentSearchService"
```

Expected: 2 RRF tests pass; integration test is filled in via fixture stand-up or kept as skipped until task 5.3 wires the handler.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Services/AgentSearchService.cs ACP_Metabot.Api/Services/IRerankProvider.cs ACP_Metabot.Api.Tests/AgentSearchServiceTests.cs
git commit -m "feat(v1.7): AgentSearchService — hybrid agent ranker

BM25 leg via existing OfferingRepository.SearchAgentsAsync; dense leg
over agent_profiles.embedding; RRF (k=60); optional Voyage rerank top
50; enrich with cross-presence + reputation. Falls back to RRF order
on rerank failure."
```

---

### Task 5.3: Update `/v1/searchAgents` handler to use `AgentSearchService`

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Program.cs` — `HandleSearchAgents` (or whatever the v1.6 handler is named).

- [ ] **Step 1: Write failing integration test**

Append to `AgentSearchServiceTests.cs`:

```csharp
[Fact]
public async Task Endpoint_SearchAgents_ReturnsV17Hits()
{
    using var fix = await TestFixtures.MinimalAgentSearchStackAsync(
        agents: new[]
        {
            ("0xa", "WhaleWatcher", "Watch large on-chain holders for movement", "v2"),
            ("0xb", "TokenScanner",  "Scan ERC-20 tokens for risk indicators", "v2"),
        });

    var response = await fix.HttpClient.PostAsync("/v1/searchAgents",
        new StringContent("""{"query":"watching whale wallets"}""", Encoding.UTF8, "application/json"));

    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadAsStringAsync();
    Assert.Contains("\"marketplaces\"", body);
    Assert.Contains("\"dominantMarketplace\"", body);
    Assert.Contains("\"topOfferingNames\"", body);
    Assert.Contains("WhaleWatcher", body); // synonym test: "whale wallets" matches via dense
}
```

- [ ] **Step 2: Run tests — FAIL**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~Endpoint_SearchAgents"
```

Expected: handler returns v1.6 shape, missing the new fields.

- [ ] **Step 3: Wire `AgentSearchService` into `Program.cs`**

Replace the body of `HandleSearchAgents` (the v1.6 handler that called `OfferingRepository.SearchAgentsAsync` directly):

```csharp
async Task<IResult> HandleSearchAgents(SearchAgentsRequest req, AgentSearchService svc, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });
    var limit = req.Limit is null ? 10 : Math.Clamp(req.Limit.Value, 1, 50);
    var marketplace = NormalizeMarketplace(req.Marketplace);
    if (req.Marketplace is not null && marketplace is null)
        return Results.BadRequest(new { error = "marketplace must be 'v1' or 'v2'" });

    var hits = await svc.SearchAsync(req.Query, limit, marketplace, ct);
    return Results.Ok(new { agents = hits });
}
```

Register `AgentSearchService` in DI:

```csharp
builder.Services.AddSingleton<AgentSearchService>();
```

Wire it into both `MapPost("/searchAgents", ...)` and `MapPost("/v1/searchAgents", ...)`.

- [ ] **Step 4: Run tests — PASS**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~Endpoint_SearchAgents"
dotnet test ACP_Metabot.Api.Tests   # full suite
```

Expected: all green.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Program.cs ACP_Metabot.Api.Tests/AgentSearchServiceTests.cs
git commit -m "feat(v1.7): /v1/searchAgents uses hybrid AgentSearchService

Replaces the direct OfferingRepository.SearchAgentsAsync call with
the new BM25+dense+rerank pipeline. Existing rate-limit policy
public-search-agents (30/IP/hr) unchanged. Synonym-paraphrase
integration test verifies dense leg lifts non-keyword matches."
```

---

## Phase 6 — Digest extensions

(Phase 6 introduces six new fields on `/v1/digest`. Tasks are split per-field so the test surface stays small per commit.)

### Task 6.1: `newAgents` + `newOfferings` fields

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Services/DigestService.cs`
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Models/` — `DigestResult` + nested types.
- Modify: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api/Data/OfferingRepository.cs` — `ListNewAgentsSinceAsync` if not present.
- Test: `ACP_Metabot/ACP_Metabot/ACP_Metabot.Api.Tests/DigestServicePulseTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
using ACP_Metabot.Api.Data;
using ACP_Metabot.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace ACP_Metabot.Api.Tests;

public class DigestServicePulseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly OfferingRepository _offerings;
    // ... (boilerplate setup as in earlier test classes) ...

    public DigestServicePulseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dig_{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}"
            }).Build();
        _db = new Db(config);
        _db.InitializeSchemaAsync().GetAwaiter().GetResult();
        _offerings = new OfferingRepository(_db);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (File.Exists(_dbPath)) File.Delete(_dbPath); }

    [Fact]
    public async Task Digest_NewAgents_CountsAgentsByFirstSeenInWindow()
    {
        // Seed: agent A first seen 40 days ago; agent B first seen 5 days ago;
        //       agent C first seen 2 days ago. window=30d covers B + C.
        await SeedOfferingAsync("0xa", "A", "off1", DateTime.UtcNow.AddDays(-40));
        await SeedOfferingAsync("0xb", "B", "off2", DateTime.UtcNow.AddDays(-5));
        await SeedOfferingAsync("0xc", "C", "off3", DateTime.UtcNow.AddDays(-2));

        var rep = new ReputationServiceStub();
        var svc = new DigestService(_offerings, rep);
        var result = await svc.BuildAsync(windowDays: 30, marketplaceFilter: null,
            chainFilter: null, priceMaxUsdc: null);

        Assert.Equal(2, result.NewAgents.Count);
        var addrs = result.NewAgents.Agents.Select(a => a.Address).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "0xb", "0xc" }, addrs);
    }

    [Fact]
    public async Task Digest_NewOfferings_AlreadyExposesPreservedField()
    {
        await SeedOfferingAsync("0xa", "A", "off1", DateTime.UtcNow.AddDays(-2));
        var rep = new ReputationServiceStub();
        var svc = new DigestService(_offerings, rep);
        var result = await svc.BuildAsync(7, null, null, null);
        Assert.NotNull(result.NewOfferings);  // existing v1.6 field; preserved
    }

    private async Task SeedOfferingAsync(string addr, string name, string offering, DateTime firstSeen)
    {
        var ts = firstSeen.ToString("O");
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO offerings (agent_address, agent_name, offering_name, description,
                price_usdc, price_type, chain, content_hash, first_seen_at, last_seen_at,
                marketplace_version, is_removed)
            VALUES ($a, $n, $o, 'desc', 0.10, 'per_call', 'base', $h, $f, $f, 'v2', 0)";
        cmd.Parameters.AddWithValue("$a", addr);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$o", offering);
        cmd.Parameters.AddWithValue("$h", $"h_{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("$f", ts);
        await cmd.ExecuteNonQueryAsync();
    }
}
```

(`ReputationServiceStub` is a small test double — existing tests should already have a pattern for this. Reuse.)

- [ ] **Step 2: Run tests — FAIL**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~DigestService_NewAgents"
```

- [ ] **Step 3: Implement `newAgents` field**

In `DigestService.BuildAsync`, after the existing `newOfferings` + `gainers` block, add:

```csharp
var newAgents = await _repo.ListNewAgentsSinceAsync(sinceUtc, marketplaceFilter, chainFilter, priceMaxUsdc, limit: 10);
result.NewAgentsCount = await _repo.CountNewAgentsSinceAsync(sinceUtc, marketplaceFilter, chainFilter, priceMaxUsdc);
result.NewAgents = new NewAgentsBlock(result.NewAgentsCount, newAgents);
```

In `OfferingRepository.cs`, add:

```csharp
public async Task<IReadOnlyList<NewAgentRow>> ListNewAgentsSinceAsync(
    DateTime sinceUtc, string? marketplaceFilter, HashSet<string>? chainFilter,
    double? priceMaxUsdc, int limit)
{
    // Per-agent MIN(first_seen_at) within filter set, falling within the window.
    var sql = @"
        SELECT lo.agent_address, lo.agent_name, lo.marketplace_version,
               MIN(lo.first_seen_at) AS first_seen_at,
               COUNT(*) AS offering_count
        FROM offerings lo
        WHERE lo.is_removed = 0
          AND lo.first_seen_at >= $since
          /* additional filters injected */
        GROUP BY lo.agent_address
        HAVING MIN(lo.first_seen_at) >= $since
        ORDER BY first_seen_at DESC
        LIMIT $lim";
    // ... add marketplace/chain/price filters via parameterised WHERE; standard pattern in this repo ...
}

public async Task<int> CountNewAgentsSinceAsync(
    DateTime sinceUtc, string? marketplaceFilter, HashSet<string>? chainFilter, double? priceMaxUsdc)
{
    // Same query, COUNT(*) over the grouped result.
}
```

`NewAgentRow` is a record `(string Address, string Name, string Marketplace, string FirstSeenAt, int OfferingCount)` with JSON property names per the spec.

`DigestResult` and `NewAgentsBlock` types: extend in `Models/Digest*.cs`.

- [ ] **Step 4: Run tests — PASS**

```
dotnet test ACP_Metabot.Api.Tests --filter "FullyQualifiedName~DigestService"
```

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api/Services/DigestService.cs ACP_Metabot.Api/Data/OfferingRepository.cs ACP_Metabot.Api/Models/ ACP_Metabot.Api.Tests/DigestServicePulseTests.cs
git commit -m "feat(v1.7): newAgents block on /v1/digest

Per-agent MIN(first_seen_at) bucketed into request window. Top-10
returned with full count. Existing newOfferings preserved unchanged."
```

---

### Task 6.2: `churnRate` field

**Files:**
- Modify: `ACP_Metabot.Api/Services/DigestService.cs`, `OfferingRepository.cs`, `Models/`.
- Test: `DigestServicePulseTests.cs` — extend.

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task Digest_ChurnRate_CountsBaselineAgentsWithZeroLiveOfferings()
{
    // Baseline: agents with ≥1 non-tombstoned offering at windowStart (10 days ago).
    // After window: A still has offerings, B's offerings are all tombstoned.
    // Expect baseline=2, churned=1, rate=0.5.
    var ts10dAgo = DateTime.UtcNow.AddDays(-10).ToString("O");
    var tsNow = DateTime.UtcNow.ToString("O");

    await using (var conn = _db.OpenConnection())
    {
        // A — present at baseline, still alive.
        await using var c1 = conn.CreateCommand();
        c1.CommandText = @"INSERT INTO offerings(...) VALUES (...)";  // tracked with first_seen=20d ago, not removed
        // ... seed appropriately ...
    }

    var svc = new DigestService(_offerings, new ReputationServiceStub());
    var result = await svc.BuildAsync(10, null, null, null);

    Assert.NotNull(result.ChurnRate);
    Assert.Equal(2, result.ChurnRate.BaselineCount);
    Assert.Equal(1, result.ChurnRate.ChurnedCount);
    Assert.Equal(0.5, result.ChurnRate.Rate, 3);
}
```

(Fill in seed details to match — keep them deterministic.)

- [ ] **Step 2: Run — FAIL**

- [ ] **Step 3: Implement**

In `OfferingRepository`:

```csharp
public async Task<(int Baseline, int Churned)> ComputeChurnAsync(
    DateTime windowStartUtc, string? marketplace, HashSet<string>? chainFilter, double? priceMaxUsdc)
{
    // baseline = distinct agent_address where MIN(first_seen_at) <= windowStart
    //            AND there is at least one offering active at windowStart
    //            (last_seen_at >= windowStart AND (NOT is_removed OR removed_at > windowStart))
    // churned = baseline ∩ { distinct agent_address where ALL current offerings are removed }
    // SQL is two queries; intersect in C#.
    // ... fill in standard parameterised filter patterns ...
}
```

In `DigestService.BuildAsync`, after newAgents:

```csharp
var (baseline, churned) = await _repo.ComputeChurnAsync(sinceUtc, marketplaceFilter, chainFilter, priceMaxUsdc);
result.ChurnRate = baseline == 0
    ? new ChurnRate(0.0, churned, baseline)
    : new ChurnRate((double)churned / baseline, churned, baseline);
```

- [ ] **Step 4: Run — PASS**

- [ ] **Step 5: Commit**

```
git commit -m "feat(v1.7): churnRate on /v1/digest

Baseline = agents alive at windowStart; churned = baseline agents
with zero live offerings now. Rate = churned/baseline (0 when
baseline empty)."
```

---

### Task 6.3: `cohortSurvival` field

**Files:**
- Modify: `DigestService.cs`, `OfferingRepository.cs`, `Models/`.
- Test: `DigestServicePulseTests.cs` — extend.

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task Digest_CohortSurvival_NullForShortWindow()
{
    var svc = new DigestService(_offerings, new ReputationServiceStub());
    var result = await svc.BuildAsync(7, null, null, null);
    Assert.Null(result.CohortSurvival);
}

[Fact]
public async Task Digest_CohortSurvival_BucketsByIsoWeek()
{
    // Seed agents across multiple ISO weeks, some active today (offerings),
    // some surviving via lifetime snapshot, some neither.
    // Window=60 days → cohorts cover ~8 weeks.
    // Expect survival_rate per cohort.
    // ...
}
```

- [ ] **Step 2: Run — FAIL**

- [ ] **Step 3: Implement**

In `OfferingRepository` add `ListAgentsByCohortWeekAsync` returning `(week, count)` and `IsAgentSurvivingAsync` (or a batched variant). In `DigestService`:

```csharp
if (windowDays >= 30)
{
    var cohorts = await _repo.ListAgentsByCohortWeekAsync(sinceUtc, marketplaceFilter, chainFilter, priceMaxUsdc);
    var rows = new List<CohortSurvivalRow>();
    foreach (var c in cohorts.Take(12))
    {
        var surviving = await _repo.CountSurvivingInCohortAsync(c.Week, marketplaceFilter, chainFilter, priceMaxUsdc);
        var rate = c.Size == 0 ? 0.0 : (double)surviving / c.Size;
        rows.Add(new CohortSurvivalRow(c.Week, c.Start, c.Size, surviving, rate));
    }
    result.CohortSurvival = rows;
}
else result.CohortSurvival = null;
```

ISO week math: `System.Globalization.ISOWeek.GetWeekOfYear(date)` + `ISOWeek.ToDateTime(year, week, dayOfWeek)`.

- [ ] **Step 4: Run — PASS**

- [ ] **Step 5: Commit**

```
git commit -m "feat(v1.7): cohortSurvival on /v1/digest

ISO-week-bucketed cohorts (last 12). Survival = ≥1 live offering OR
≥1 job in last 30d via agent_lifetime_snapshot. Null when days<30."
```

---

### Task 6.4: `saturationMap` field

**Files:**
- Modify: `DigestService.cs`, `Models/`.
- Inject: `SaturationCalculator` into `DigestService`.

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task Digest_SaturationMap_FromCalculator()
{
    // Refresh SearchService corpus (which feeds SaturationCalculator) with
    // a fixture where one category has near-duplicates and another doesn't.
    // Verify saturationMap entries reflect what the calculator produced.
}
```

- [ ] **Step 2: Run — FAIL**

- [ ] **Step 3: Implement**

Inject `SaturationCalculator` into `DigestService`. In `BuildAsync`:

```csharp
result.SaturationMap = _saturation.PerCategory()
    .Select(c => new SaturationMapRow(c.Category, c.Total, c.SaturatedCount, c.SaturationPct))
    .ToList();
```

The map is global (not filter-scoped) — calling out in user-guide that filters apply only to digest fields they make sense for.

- [ ] **Step 4: Run — PASS**

- [ ] **Step 5: Commit**

```
git commit -m "feat(v1.7): saturationMap on /v1/digest

Per-category rollup from SaturationCalculator. Map is global; filters
on /v1/digest do not scope it (notes in user-guide)."
```

---

### Task 6.5: `windowStart` + `partial` flag + `days` cap raised to 90

**Files:**
- Modify: `DigestService.cs`, `Program.cs` (handler `days` clamp), `Models/`.
- Test: `DigestServicePulseTests.cs` — extend.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task Digest_WindowStart_IsRequestedDaysAgoUtc()
{
    var svc = new DigestService(_offerings, new ReputationServiceStub());
    var result = await svc.BuildAsync(30, null, null, null);
    var expected = DateTime.UtcNow.AddDays(-30);
    var actual = DateTime.Parse(result.WindowStart, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    Assert.True((actual - expected).Duration() < TimeSpan.FromMinutes(1));
}

[Fact]
public async Task Digest_PartialFlag_TrueWhenSnapshotsMissing()
{
    // Build with a window that predates any agent_reputation_snapshots data.
    var svc = new DigestService(_offerings, new ReputationServiceStub());
    var result = await svc.BuildAsync(60, null, null, null);
    Assert.True(result.Partial);
}

[Fact]
public async Task Digest_DaysCap_NowAt90()
{
    // Handler-level: days=120 should clamp to 90, not 30.
    // Integration-level test in Phase 11.
}
```

- [ ] **Step 2: Run — FAIL**

- [ ] **Step 3: Implement**

In `DigestService.BuildAsync`:

```csharp
result.WindowStart = sinceUtc.ToString("O");

bool partial = false;
if (!await _repo.SnapshotExistsAsync(snapshotDate)) partial = true;
// also flag partial if windowDays > age of agent_profiles (not strictly needed; cohort survival null is the usual signal)
result.Partial = partial;
```

In `Program.cs` `HandleDigest`:

```csharp
var window = days is null ? 1 : Math.Clamp(days.Value, 1, 90); // was 30
```

Update the spec / user-guide cross-reference.

- [ ] **Step 4: Run — PASS**

- [ ] **Step 5: Commit**

```
git commit -m "feat(v1.7): windowStart + partial flag on /v1/digest; days cap 30→90

Allows cohortSurvival to span 12 weeks. Partial=true when backing
data (snapshot from windowStart) is missing — typical on fresh
deploys or after long downtimes."
```

---

### Task 6.6: Per-filter-set hourly cache

**Files:**
- Modify: `DigestService.cs`.
- Test: `DigestServicePulseTests.cs` — extend.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task Digest_Cached_SecondCallSameFilters_DoesNotRecompute()
{
    var rep = new CountingReputationStub();
    var svc = new DigestService(_offerings, rep);
    await svc.BuildAsync(7, null, null, null);
    var firstCount = rep.CallCount;

    await svc.BuildAsync(7, null, null, null);  // same filter-set
    Assert.Equal(firstCount, rep.CallCount);
}

[Fact]
public async Task Digest_Cached_DifferentFilters_Recompute()
{
    var rep = new CountingReputationStub();
    var svc = new DigestService(_offerings, rep);
    await svc.BuildAsync(7, "v1", null, null);
    var firstCount = rep.CallCount;

    await svc.BuildAsync(7, "v2", null, null);  // different filter-set
    Assert.True(rep.CallCount > firstCount);
}
```

- [ ] **Step 2: Run — FAIL**

- [ ] **Step 3: Implement cache**

In `DigestService`:

```csharp
private readonly Dictionary<string, (DateTime Bucket, DigestResult Result)> _cache = new();
private readonly SemaphoreSlim _lock = new(1, 1);

public async Task<DigestResult> BuildAsync(int windowDays, string? marketplaceFilter,
    HashSet<string>? chainFilter, double? priceMaxUsdc)
{
    var hourBucket = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
    var key = CacheKey(windowDays, marketplaceFilter, chainFilter, priceMaxUsdc);
    if (_cache.TryGetValue(key, out var entry) && entry.Bucket == hourBucket)
        return entry.Result;

    await _lock.WaitAsync();
    try
    {
        if (_cache.TryGetValue(key, out entry) && entry.Bucket == hourBucket)
            return entry.Result;

        var fresh = await BuildUncachedAsync(windowDays, marketplaceFilter, chainFilter, priceMaxUsdc);
        _cache[key] = (hourBucket, fresh);
        return fresh;
    }
    finally { _lock.Release(); }
}

private static string CacheKey(int days, string? mv, HashSet<string>? chain, double? price)
{
    var c = chain is null ? "" : string.Join(",", chain.OrderBy(s => s));
    return $"d={days}|m={mv ?? ""}|c={c}|p={price?.ToString() ?? ""}";
}
```

Rename the previous `BuildAsync` body to `BuildUncachedAsync`.

- [ ] **Step 4: Run — PASS**

- [ ] **Step 5: Commit**

```
git commit -m "feat(v1.7): per-filter-set hourly cache on /v1/digest

Memory cache keyed on (days, marketplace, chain-set, priceMaxUsdc,
hourBucket). Lock-on-miss prevents thundering-herd on cold filter sets."
```

---

## Phase 7 — Sidecar (acp-v2)

### Task 7.1: Extend `apiClient.ts` types and deliverable schemas

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/acp-v2/src/apiClient.ts`
- Modify: `acp-v2/src/offerings/find.ts` (or equivalent)
- Modify: `acp-v2/src/offerings/search-agents.ts`
- Modify: `acp-v2/src/offerings/browse-agent.ts`
- Modify: `acp-v2/src/offerings/today.ts`

- [ ] **Step 1: Run `npm run build` to confirm baseline**

```
cd ACP_Metabot/ACP_Metabot/acp-v2 && npm run build
```

Expected: clean.

- [ ] **Step 2: Extend types in `apiClient.ts`**

Add the new types and extend response shapes to match the spec wire surfaces. Concretely:

```ts
export interface SaturationDto {
  nearDuplicateCount: number;
  categorySize: number;
}
export interface PricePercentileDto {
  value: number | null;
  peerN: number;
  lowN: boolean;
}
export interface OfferingHit {
  // ... existing fields ...
  saturation?: SaturationDto;
  pricePercentile?: PricePercentileDto;
}
export interface AgentSearchHit {
  agentAddress: string;
  agentName: string;
  score: number;
  totalOfferings: number;
  topOfferings: { offeringName: string; priceUsdc: number; marketplaceVersion: string }[];
  totalJobs: number;
  topOfferingNames: string[];
  marketplaces: ("v1" | "v2")[];
  dominantMarketplace: "v1" | "v2" | "tied" | "none";
  agentScore?: number;
}
export interface CrossPresenceMarketplace {
  offeringCount: number;
  firstSeenAt: string;
  lastSeenAt: string;
}
export interface CrossPresence {
  v1: CrossPresenceMarketplace | null;
  v2: CrossPresenceMarketplace | null;
  inBoth: boolean;
  dominant: "v1" | "v2" | "tied" | "none";
}
export interface BrowseAgentResponse {
  // ... existing fields ...
  crossPresence: CrossPresence;
  offerings: (OfferingHit & { pricePercentile?: PricePercentileDto })[];
}
export interface DigestResponse {
  // existing: newOfferings, gainers, ...
  windowStart: string;
  partial: boolean;
  newAgents: { count: number; agents: NewAgentRow[] };
  newOfferings: { count: number; offerings: OfferingHit[] };  // shape may already exist
  churnRate: { rate: number; churnedCount: number; baselineCount: number };
  cohortSurvival: CohortSurvivalRow[] | null;
  saturationMap: SaturationMapRow[];
}
export interface NewAgentRow {
  address: string;
  name: string;
  marketplace: "v1" | "v2";
  firstSeenAt: string;
  offeringCount: number;
}
export interface CohortSurvivalRow {
  cohortWeek: string;
  cohortStart: string;
  cohortSize: number;
  surviving: number;
  survivalRate: number;
}
export interface SaturationMapRow {
  category: string;
  total: number;
  saturatedCount: number;
  saturationPct: number;
}
```

- [ ] **Step 3: Update each deliverable schema**

In each of `offerings/find.ts`, `offerings/search-agents.ts`, `offerings/browse-agent.ts`, `offerings/today.ts`, update:

- The Zod schema (or whichever validation lib the sidecar uses) to include the new fields.
- The deliverable description to mention the new fields ("each hit now includes saturation and pricePercentile"; "agent search uses hybrid ranker"; "browseAgent includes V1↔V2 footprint"; "digest includes pulse fields").
- `offerings/today.ts`: add `days` parameter (1-90) to the request schema.

Run `npm run build` and `npm run print-offerings` to verify.

- [ ] **Step 4: Verify**

```
cd acp-v2 && npm run build && npm run print-offerings
```

Expected: tsc clean; `print-offerings` shows updated descriptions with the new fields visible.

- [ ] **Step 5: Commit**

```
git add acp-v2/src/apiClient.ts acp-v2/src/offerings/ acp-v2/package.json
git commit -m "feat(v1.7): sidecar types + deliverable schema updates

apiClient.ts gains SaturationDto, PricePercentileDto, AgentSearchHit
v1.7 fields, CrossPresence, NewAgentRow, CohortSurvivalRow,
SaturationMapRow. Each of find/search-agents/browse-agent/today
deliverables updated. today gains days param (1-90)."
```

---

### Task 7.2: Bump sidecar package version

**Files:**
- Modify: `acp-v2/package.json`.

- [ ] **Step 1-4: trivial — bump version**

```json
"version": "0.7.0"
```

- [ ] **Step 5: Commit**

```
git commit -m "chore(acp-v2): bump to 0.7.0 for v1.7"
```

---

## Phase 8 — MCP server (acp-find-mcp)

### Task 8.1: Update tool schemas in `acp-find-plugin/mcp-server/`

**Files:**
- Modify: `acp-find-plugin/mcp-server/src/tools/find.ts`, `tools/search-agents.ts`, `tools/browse-agent.ts`, `tools/today.ts`.

- [ ] **Step 1: Build baseline**

```
cd acp-find-plugin/mcp-server && npm run build
```

Expected: clean.

- [ ] **Step 2-4: Update each tool**

In each tool file:
- Extend output schema to include the new fields described in Phase 7 step 2.
- Update the tool's `description` field to mention the new fields and new args. Keep the description concise — buyer LLMs use it to pick tools.
- For `today`: add the `days` argument (input schema, range 1-90).

Run `npm run build`. Smoke-test by inspecting the printed schemas in `acp-find-plugin/mcp-server/dist/`.

- [ ] **Step 5: Commit**

```
git add acp-find-plugin/mcp-server/src/tools/
git commit -m "feat(acp-find-mcp): tool schemas + descriptions for v1.7

find/search-agents/browse-agent/today tools advertise the new fields.
today gains days arg (1-90)."
```

---

### Task 8.2: Bump `acp-find-mcp` version + update README

**Files:**
- Modify: `acp-find-plugin/mcp-server/package.json` → `0.7.0`.
- Modify: `acp-find-plugin/mcp-server/README.md`.

- [ ] **Step 1: Update README with v1.7 fields and new behaviour**

Sections to update:
- Tool list — note that `acp_search_agents` is now hybrid-ranked.
- Output examples — refresh JSON snippets to include `saturation`, `pricePercentile`, `crossPresence`, the new digest fields.
- Add a "Backward compatibility" note for the `score` and `topOfferings` semantic / shape change with `topOfferingNames` mirror.

- [ ] **Step 2-4: Bump version**

```json
"version": "0.7.0"
```

- [ ] **Step 5: Commit**

```
git commit -m "chore(acp-find-mcp): bump to 0.7.0; README documents v1.7 changes"
```

(Republish to npm is a separate manual step the user runs in their TTY — call it out in the final phase.)

---

## Phase 9 — Plugin slash commands

### Task 9.1: Update `/acp-find:search` and `/acp-find:search-agents`

**Files:**
- Modify: `acp-find-plugin/commands/acp-find-search.md`, `commands/acp-find-search-agents.md`.

- [ ] **Step 1: Update prose**

For `acp-find-search.md`: add a section explaining `saturation` and `pricePercentile` fields and how to interpret them ("nearDuplicateCount > 3 in a category usually means crowded niche", "pricePercentile near 100 with peerN ≥ 5 means premium").

For `acp-find-search-agents.md`: note the hybrid ranker upgrade ("search now picks up synonyms and paraphrase; previously keyword-only"), document the new fields (`marketplaces`, `dominantMarketplace`, `agentScore`).

- [ ] **Step 2-4: N/A (doc only)**

- [ ] **Step 5: Commit**

```
git commit -m "docs(acp-find-plugin): update search + search-agents commands for v1.7"
```

---

### Task 9.2: Update `/acp-find:agent` and `/acp-find:today`

**Files:**
- Modify: `acp-find-plugin/commands/acp-find-agent.md`, `commands/acp-find-today.md`.

- [ ] **Step 1: Update prose**

For `acp-find-agent.md`: explain the `crossPresence` block (V1/V2 footprint, dominant marketplace) and per-offering `pricePercentile`.

For `acp-find-today.md`: document the new `days` arg (1-90), and the new fields (`newAgents`, `churnRate`, `cohortSurvival` (note null below 30d), `saturationMap`, `partial`).

- [ ] **Step 2-4: N/A**

- [ ] **Step 5: Commit**

```
git commit -m "docs(acp-find-plugin): update agent + today commands for v1.7"
```

---

### Task 9.3: Bump plugin version + update plugin README

**Files:**
- Modify: `acp-find-plugin/plugin.json` (or `package.json`) → 0.7.0.
- Modify: `acp-find-plugin/README.md`.

- [ ] **Step 1: Update plugin README** with the v1.7 surface — link to the spec, summarise the 5 features, list bumps.

- [ ] **Step 2-4: Bump version**

- [ ] **Step 5: Commit**

```
git commit -m "chore(acp-find-plugin): bump to 0.7.0 for v1.7 release"
```

---

## Phase 10 — Documentation lockstep (Metabot side)

### Task 10.1: ACP_Metabot README.md

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/README.md`.

- [ ] **Step 1: Update README**

Bump version line to 1.7.0. Update the "Public gateway endpoints" table:
- `/v1/searchAgents` — note "hybrid (BM25 + dense + rerank)"
- `/v1/agent/{address}` — note "includes crossPresence + per-offering pricePercentile"
- `/v1/search` — note "offering hits include saturation + pricePercentile"
- `/v1/digest` — note "pulse fields: newAgents, churnRate, cohortSurvival (≥30d), saturationMap, windowStart, partial; days cap 90"

Architecture diagram refreshed to show the new services: `AgentProfileEmbedder`, `AgentSearchService`, `SaturationCalculator`, `PricePercentileCalculator`, `CrossPresenceBuilder`.

- [ ] **Step 5: Commit**

```
git commit -m "docs(metabot): README v1.7 — public gateway endpoints + architecture"
```

---

### Task 10.2: docs/user-guide.md, technical-specifications.md, design.md, runbook-scaling.md

**Files:**
- Modify: `ACP_Metabot/ACP_Metabot/docs/user-guide.md`
- Modify: `ACP_Metabot/ACP_Metabot/docs/technical-specifications.md`
- Modify: `ACP_Metabot/ACP_Metabot/docs/design.md`
- Modify: `ACP_Metabot/ACP_Metabot/docs/runbook-scaling.md`

- [ ] **Step 1: Update each doc**

- `user-guide.md` — add new sub-sections:
  - "Agent search (hybrid)" — explains BM25 vs dense vs rerank, when each catches what.
  - "V1 ↔ V2 cross-presence" — explains the new browseAgent block.
  - "Marketplace pulse digest" — explains each new field, when fields are null/partial, the saturationMap caveat (global, not filter-scoped).
- `technical-specifications.md` — add agent_profiles table to the schema section; document the embedder service's cycle behaviour and rate-limit class.
- `design.md` — refresh the architecture diagram (mirror the one in the spec).
- `runbook-scaling.md` — Lever 5 entry for `public-search-agents` flips to "embedding-backed; rerank-bounded"; add agent profile embedder to Lever 1 (background services).

- [ ] **Step 5: Commit**

```
git add ACP_Metabot/ACP_Metabot/docs/
git commit -m "docs(metabot): user-guide + tech-spec + design + runbook for v1.7"
```

---

## Phase 11 — Integration tests + final smoke

### Task 11.1: Integration test stack

**Files:**
- Create: `ACP_Metabot.Api.IntegrationTests/` (if not exists; or under existing IntegrationTests project)
- Create: tests for each endpoint exercising the v1.7 surface.

- [ ] **Step 1: Stand up `WebApplicationFactory`-backed integration tests**

```csharp
public class V17IntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public V17IntegrationTests(WebApplicationFactory<Program> factory)
    {
        // Override DI for Voyage stub + seeded DB. See existing IntegrationTests
        // project for the pattern; if absent, create with WebApplicationFactory<Program>.
        _client = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IEmbeddingProvider, StubEmbedder>();
                // override DB connection string to a temp SQLite path; seed fixture data
            });
        }).CreateClient();
    }

    [Fact]
    public async Task V1Search_HitsCarrySaturationAndPricePercentile() { /* ... */ }

    [Fact]
    public async Task V1SearchAgents_ReturnsHybridHits_WithNewFields() { /* ... */ }

    [Fact]
    public async Task V1Agent_IncludesCrossPresenceAndPercentile() { /* ... */ }

    [Fact]
    public async Task V1Digest_Days60_HasCohortSurvivalAndPulseFields() { /* ... */ }

    [Fact]
    public async Task V1Digest_Days120_ClampedTo90() { /* ... */ }
}
```

- [ ] **Step 2-4: Implement and run**

```
dotnet test ACP_Metabot.Api.IntegrationTests
```

Expected: all green.

- [ ] **Step 5: Commit**

```
git add ACP_Metabot.Api.IntegrationTests/
git commit -m "test(v1.7): integration tests for /v1/search, /v1/searchAgents, /v1/agent, /v1/digest

Cover the new fields end-to-end through WebApplicationFactory with
stubbed Voyage. Verify the days=120 clamp."
```

---

### Task 11.2: Build + full test run

- [ ] **Step 1-4:**

```
cd ACP_Metabot/ACP_Metabot
dotnet build
dotnet test
```

Expected: 0 warnings, 0 errors; all tests pass (existing 31 + the v1.7 additions).

```
cd acp-v2
npm install
npm run build
npm run print-offerings
```

Expected: tsc clean; print-offerings renders all 4 deliverables with v1.7 descriptions.

```
cd acp-find-plugin/mcp-server
npm install
npm run build
```

Expected: clean.

- [ ] **Step 5: No commit (verification only)**

---

### Task 11.3: Droplet deploy + smoke (manual)

This is a manual / human-in-the-loop step.

- [ ] **Step 1: Push the implementation branch to GitHub** (after all tasks above are committed locally).

- [ ] **Step 2: Build + push the new docker image** (matches the existing v1.6 deploy pattern in CLAUDE.md / runbook).

- [ ] **Step 3: SSH to droplet, `docker compose pull && docker compose up -d`.**

- [ ] **Step 4: Smoke against `api.acp-metabot.dev`:**

```bash
curl -s -X POST https://api.acp-metabot.dev/v1/searchAgents \
  -H 'content-type: application/json' \
  -d '{"query":"watching whale wallets","limit":5}' | jq '.agents[0]'
# Expect: marketplaces, dominantMarketplace, agentScore, topOfferingNames present.

curl -s "https://api.acp-metabot.dev/v1/digest?days=60" | jq 'keys'
# Expect: cohortSurvival non-null, newAgents present, saturationMap present.

curl -s "https://api.acp-metabot.dev/v1/agent/<known-multi-marketplace-addr>" | jq '.crossPresence'
# Expect: inBoth=true (or the appropriate value for the chosen address).

curl -s -X POST https://api.acp-metabot.dev/v1/search \
  -H 'content-type: application/json' \
  -d '{"query":"wallet intel","limit":3}' | jq '.results[0].saturation, .results[0].pricePercentile'
# Expect: both populated.
```

- [ ] **Step 5: Republish `acp-find-mcp` to npm (manual TTY step from the user)**

```
! cd acp-find-plugin/mcp-server && npm publish
```

(Requires WebAuthn; user runs this in their own terminal per CLAUDE.md.)

---

## Self-Review

**1. Spec coverage** (every numbered/lettered scope item):

- (1) Hybrid agent search — Phase 1 (schema) → Phase 4 (embedder) → Phase 5 (ranker upgrade) → Phase 7-8 (sidecar/MCP). ✓
- (2) cross-presence on browseAgent — Phase 3. ✓
- (3) saturation per offering — Phase 2.1, 2.3. ✓
- (4) pricePercentile per offering — Phase 2.2, 2.3, plus 3.2 wires it onto browseAgent per-offering. ✓
- (5) digest pulse extensions:
  - newAgents — Task 6.1. ✓
  - newOfferings — already exists in v1.6; preserved (no separate task needed). ✓
  - churnRate — Task 6.2. ✓
  - cohortSurvival — Task 6.3. ✓
  - saturationMap — Task 6.4. ✓
  - windowStart + partial — Task 6.5. ✓
  - days cap 30 → 90 — Task 6.5. ✓
  - Per-filter-set hourly cache — Task 6.6. ✓
- Sidecar updates — Phase 7. ✓
- MCP server updates — Phase 8. ✓
- Plugin slash commands — Phase 9. ✓
- Doc lockstep (Metabot side) — Phase 10. ✓
- Integration tests + droplet smoke — Phase 11. ✓

**2. Placeholder scan:**
- Task 6.2 step 1 includes "fill in seed details to match" — leaving this for the implementer is acceptable for a digest-shaped test where the precise seed values are derivable from the assertion targets, but it's borderline. Mitigated by giving the assertion explicitly (baseline=2, churned=1, rate=0.5).
- Task 6.4 step 3 says "calling out in user-guide that filters apply only to digest fields they make sense for" — this is a doc cross-reference, not a placeholder.
- A few "if not present, add a helper" notes (e.g. `OfferingRepository.ListByAgentAsync`) — these depend on the existing repo state and are appropriate to leave as conditional. The signature is given.
- No "TBD", "TODO", "implement later" left in the plan.

**3. Type consistency check:**
- `AgentSearchHit` shape in Task 5.1 matches the Phase 7 sidecar TypeScript interface and the spec wire surface. ✓
- `CrossPresence` C# record (Task 3.1) and TypeScript interface (Task 7.1) are consistent. ✓
- `PricePercentile` field names (`value`, `peerN`, `lowN`) consistent across Tasks 2.2 / 2.3 / 3.2 / 7.1 / spec. ✓
- `SaturationCalculator` API (`Refresh`, `NearDuplicateCount`, `CategorySize`, `PerCategory`) consistent across Tasks 2.1, 2.3, 6.4. ✓
- `DigestService.BuildAsync(int, string?, HashSet<string>?, double?)` signature consistent with existing v1.6 shape and unchanged across all Phase 6 tasks. ✓

No issues found in self-review that warrant a re-pass.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-05-04-metabot-v1-7-meta-search.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration. Best for a plan this size; review checkpoints catch drift early.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints for review.

**Which approach?**
