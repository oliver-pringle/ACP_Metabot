using System.Globalization;
using ACP_Metabot.Api.Models;
using Microsoft.Data.Sqlite;

namespace ACP_Metabot.Api.Data;

// ---------- response DTOs (operator-only, returned as JSON) ----------
public record MetricsSummary(
    int Window,
    long TotalRequests,
    Dictionary<string, long> BySource,
    Dictionary<string, long> ByStatusClass,
    long VoyageErrors,
    long ClaudeErrors,
    double ErrorRate);

public record MetricsTimeseriesPoint(
    string Bucket,
    long Count,
    long Errors,
    long VoyageErrors,
    long ClaudeErrors);

public record MetricsEndpointRow(
    string Endpoint,
    long Count,
    int P50Ms,
    int P95Ms,
    long Count2xx,
    long Count4xx,
    long Count5xx,
    long Count429,
    Dictionary<string, long> BySource);

public record MetricsTopRow(string Value, long Count);

public record MetricsErrorRow(
    string Ts,
    string Endpoint,
    string Method,
    int StatusCode,
    int DurationMs,
    string Source,
    string? ProviderError,
    string? QueryText,
    string? AgentAddress,
    string? RemoteIp);
// ---------------------------------------------------------------------

public class RequestMetricsRepository
{
    private readonly Db _db;

    public RequestMetricsRepository(Db db) => _db = db;

    // ===== WRITE SIDE (called from MetricsWriterService) =====

    public async Task InsertManyAsync(IReadOnlyList<RequestMetricEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;

        await using var conn = _db.OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO request_log (
                ts, endpoint, method, status_code, duration_ms, source,
                user_agent, caller_id, remote_ip, query_text, agent_address, provider_error)
            VALUES (
                $ts, $endpoint, $method, $status, $dur, $source,
                $ua, $caller, $ip, $query, $agent, $perr);";
        var pTs       = cmd.Parameters.Add("$ts",       SqliteType.Text);
        var pEndpoint = cmd.Parameters.Add("$endpoint", SqliteType.Text);
        var pMethod   = cmd.Parameters.Add("$method",   SqliteType.Text);
        var pStatus   = cmd.Parameters.Add("$status",   SqliteType.Integer);
        var pDur      = cmd.Parameters.Add("$dur",      SqliteType.Integer);
        var pSource   = cmd.Parameters.Add("$source",   SqliteType.Text);
        var pUa       = cmd.Parameters.Add("$ua",       SqliteType.Text);
        var pCaller   = cmd.Parameters.Add("$caller",   SqliteType.Text);
        var pIp       = cmd.Parameters.Add("$ip",       SqliteType.Text);
        var pQuery    = cmd.Parameters.Add("$query",    SqliteType.Text);
        var pAgent    = cmd.Parameters.Add("$agent",    SqliteType.Text);
        var pPerr     = cmd.Parameters.Add("$perr",     SqliteType.Text);

        foreach (var e in events)
        {
            pTs.Value       = e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture);
            pEndpoint.Value = e.Endpoint;
            pMethod.Value   = e.Method;
            pStatus.Value   = e.StatusCode;
            pDur.Value      = e.DurationMs;
            pSource.Value   = e.Source;
            pUa.Value       = (object?)e.UserAgent    ?? DBNull.Value;
            pCaller.Value   = (object?)e.CallerId     ?? DBNull.Value;
            pIp.Value       = (object?)e.RemoteIp     ?? DBNull.Value;
            pQuery.Value    = (object?)e.QueryText    ?? DBNull.Value;
            pAgent.Value    = (object?)e.AgentAddress ?? DBNull.Value;
            pPerr.Value     = (object?)e.ProviderError ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    // Aggregate a one-hour window of request_log into request_rollup_hourly.
    // Idempotent: re-running for the same bucket overwrites the existing row.
    public async Task RolloverHourlyAsync(DateTime hourStartUtc, CancellationToken ct)
    {
        var hourEnd = hourStartUtc.AddHours(1);
        var bucketKey = hourStartUtc.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture);
        await RolloverInternalAsync(
            bucketKey, hourStartUtc, hourEnd, "request_rollup_hourly", "bucket_hour", ct);
    }

    public async Task RolloverDailyAsync(DateOnly dayUtc, CancellationToken ct)
    {
        var dayStart = dayUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dayEnd   = dayStart.AddDays(1);
        var bucketKey = dayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await RolloverInternalAsync(
            bucketKey, dayStart, dayEnd, "request_rollup_daily", "bucket_date", ct);
    }

    private async Task RolloverInternalAsync(
        string bucketKey, DateTime startInclusive, DateTime endExclusive,
        string targetTable, string bucketCol, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();

        // Wipe existing rows for the bucket (keeps re-runs idempotent).
        await using (var del = conn.CreateCommand())
        {
            del.CommandText = $"DELETE FROM {targetTable} WHERE {bucketCol} = $b;";
            del.Parameters.AddWithValue("$b", bucketKey);
            await del.ExecuteNonQueryAsync(ct);
        }

        // Aggregate raw rows in-place.
        await using var ins = conn.CreateCommand();
        ins.CommandText = $@"
            INSERT INTO {targetTable}
                ({bucketCol}, endpoint, source, status_class,
                 count, sum_duration_ms, voyage_errors, claude_errors)
            SELECT
                $b,
                endpoint,
                source,
                CASE
                    WHEN status_code = 429 THEN '429'
                    WHEN status_code BETWEEN 500 AND 599 THEN '5xx'
                    WHEN status_code BETWEEN 400 AND 499 THEN '4xx'
                    WHEN status_code BETWEEN 300 AND 399 THEN '3xx'
                    WHEN status_code BETWEEN 200 AND 299 THEN '2xx'
                    ELSE 'other'
                END AS status_class,
                COUNT(*),
                SUM(duration_ms),
                SUM(CASE WHEN provider_error LIKE 'voyage_%' THEN 1 ELSE 0 END),
                SUM(CASE WHEN provider_error LIKE 'claude_%' THEN 1 ELSE 0 END)
            FROM request_log
            WHERE ts >= $start AND ts < $end
            GROUP BY endpoint, source, status_class;";
        ins.Parameters.AddWithValue("$b",     bucketKey);
        ins.Parameters.AddWithValue("$start", startInclusive.ToString("O", CultureInfo.InvariantCulture));
        ins.Parameters.AddWithValue("$end",   endExclusive.ToString("O", CultureInfo.InvariantCulture));
        await ins.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneRawOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM request_log WHERE ts < $c;";
        cmd.Parameters.AddWithValue("$c", cutoffUtc.ToString("O", CultureInfo.InvariantCulture));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneHourlyRollupOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM request_rollup_hourly WHERE bucket_hour < $c;";
        cmd.Parameters.AddWithValue("$c", cutoffUtc.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ===== READ SIDE (called from /metrics/* endpoints) =====

    public async Task<MetricsSummary> SummaryAsync(int days)
    {
        days = Math.Clamp(days, 1, 365);
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await using var conn = _db.OpenConnection();

        var bySource     = new Dictionary<string, long>(StringComparer.Ordinal);
        var byStatusClass = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0, voyage = 0, claude = 0, errors = 0;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT source, status_class, SUM(count), SUM(voyage_errors), SUM(claude_errors)
            FROM request_rollup_daily
            WHERE bucket_date >= $cutoff
            GROUP BY source, status_class;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var src      = reader.GetString(0);
            var statusCl = reader.GetString(1);
            var count    = reader.GetInt64(2);
            var ve       = reader.GetInt64(3);
            var ce       = reader.GetInt64(4);

            total += count;
            voyage += ve;
            claude += ce;
            bySource[src]      = bySource.GetValueOrDefault(src) + count;
            byStatusClass[statusCl] = byStatusClass.GetValueOrDefault(statusCl) + count;
            if (statusCl == "4xx" || statusCl == "5xx" || statusCl == "429") errors += count;
        }

        var rate = total == 0 ? 0.0 : (double)errors / total;
        return new MetricsSummary(days, total, bySource, byStatusClass, voyage, claude, rate);
    }

    public async Task<List<MetricsTimeseriesPoint>> TimeseriesAsync(int days, string granularity)
    {
        granularity = (granularity ?? "hour").ToLowerInvariant();
        if (granularity != "hour" && granularity != "day")
            throw new ArgumentException("granularity must be 'hour' or 'day'");

        var (table, bucketCol, cutoff) = granularity == "hour"
            ? ("request_rollup_hourly", "bucket_hour",
               DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 7)).ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture))
            : ("request_rollup_daily",  "bucket_date",
               DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 90)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                {bucketCol},
                SUM(count),
                SUM(CASE WHEN status_class IN ('4xx','5xx','429') THEN count ELSE 0 END),
                SUM(voyage_errors),
                SUM(claude_errors)
            FROM {table}
            WHERE {bucketCol} >= $c
            GROUP BY {bucketCol}
            ORDER BY {bucketCol} ASC;";
        cmd.Parameters.AddWithValue("$c", cutoff);
        await using var reader = await cmd.ExecuteReaderAsync();

        var result = new List<MetricsTimeseriesPoint>();
        while (await reader.ReadAsync())
        {
            result.Add(new MetricsTimeseriesPoint(
                Bucket:        reader.GetString(0),
                Count:         reader.GetInt64(1),
                Errors:        reader.GetInt64(2),
                VoyageErrors:  reader.GetInt64(3),
                ClaudeErrors:  reader.GetInt64(4)));
        }
        return result;
    }

    // For windows <= 14d we have raw rows; compute true p50/p95.
    // For longer windows, fall back to sum/count averages from the daily rollup.
    public async Task<List<MetricsEndpointRow>> EndpointsAsync(int days)
    {
        days = Math.Clamp(days, 1, 90);
        await using var conn = _db.OpenConnection();

        if (days <= 14)
            return await EndpointsFromRawAsync(conn, days);

        return await EndpointsFromRollupAsync(conn, days);
    }

    private static async Task<List<MetricsEndpointRow>> EndpointsFromRawAsync(SqliteConnection conn, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("O", CultureInfo.InvariantCulture);

        var rows = new Dictionary<string, EndpointAccumulator>(StringComparer.Ordinal);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT endpoint, source, status_code, duration_ms
            FROM request_log
            WHERE ts >= $c;";
        cmd.Parameters.AddWithValue("$c", cutoff);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var endpoint = reader.GetString(0);
            var source   = reader.GetString(1);
            var status   = reader.GetInt32(2);
            var dur      = reader.GetInt32(3);

            if (!rows.TryGetValue(endpoint, out var acc))
            {
                acc = new EndpointAccumulator();
                rows[endpoint] = acc;
            }
            acc.Push(source, status, dur);
        }

        return rows
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => kv.Value.ToRow(kv.Key))
            .ToList();
    }

    private static async Task<List<MetricsEndpointRow>> EndpointsFromRollupAsync(SqliteConnection conn, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Without raw rows we can't compute true percentiles. Approximate p50/p95
        // as the average and 1.5x average respectively — labelled as approximate
        // by the response shape (caller should know days > 14 means estimate).
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                endpoint,
                source,
                status_class,
                SUM(count),
                SUM(sum_duration_ms)
            FROM request_rollup_daily
            WHERE bucket_date >= $c
            GROUP BY endpoint, source, status_class;";
        cmd.Parameters.AddWithValue("$c", cutoff);

        var byEndpoint = new Dictionary<string, EndpointApproximator>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var endpoint = reader.GetString(0);
            var source   = reader.GetString(1);
            var statusCl = reader.GetString(2);
            var count    = reader.GetInt64(3);
            var sumDur   = reader.GetInt64(4);

            if (!byEndpoint.TryGetValue(endpoint, out var acc))
            {
                acc = new EndpointApproximator();
                byEndpoint[endpoint] = acc;
            }
            acc.Push(source, statusCl, count, sumDur);
        }

        return byEndpoint
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => kv.Value.ToRow(kv.Key))
            .ToList();
    }

    public async Task<List<MetricsTopRow>> TopAsync(string dim, int days, int limit)
    {
        if (dim != "query" && dim != "agent")
            throw new ArgumentException("dim must be 'query' or 'agent'");

        days  = Math.Clamp(days, 1, 14); // bounded by raw retention
        limit = Math.Clamp(limit, 1, 200);

        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("O", CultureInfo.InvariantCulture);
        var (col, condition) = dim == "query"
            ? ("query_text",    "query_text IS NOT NULL AND query_text <> ''")
            : ("agent_address", "agent_address IS NOT NULL AND agent_address <> ''");

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT LOWER({col}) AS v, COUNT(*) AS c
            FROM request_log
            WHERE ts >= $c AND {condition}
            GROUP BY LOWER({col})
            ORDER BY c DESC
            LIMIT $lim;";
        cmd.Parameters.AddWithValue("$c", cutoff);
        cmd.Parameters.AddWithValue("$lim", limit);
        await using var reader = await cmd.ExecuteReaderAsync();

        var result = new List<MetricsTopRow>();
        while (await reader.ReadAsync())
        {
            result.Add(new MetricsTopRow(reader.GetString(0), reader.GetInt64(1)));
        }
        return result;
    }

    public async Task<List<MetricsErrorRow>> RecentErrorsAsync(int days, int limit)
    {
        days  = Math.Clamp(days, 1, 14);
        limit = Math.Clamp(limit, 1, 1000);
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("O", CultureInfo.InvariantCulture);

        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ts, endpoint, method, status_code, duration_ms, source,
                   provider_error, query_text, agent_address, remote_ip
            FROM request_log
            WHERE ts >= $c AND (status_code >= 400 OR provider_error IS NOT NULL)
            ORDER BY ts DESC
            LIMIT $lim;";
        cmd.Parameters.AddWithValue("$c", cutoff);
        cmd.Parameters.AddWithValue("$lim", limit);
        await using var reader = await cmd.ExecuteReaderAsync();

        var result = new List<MetricsErrorRow>();
        while (await reader.ReadAsync())
        {
            result.Add(new MetricsErrorRow(
                Ts:           reader.GetString(0),
                Endpoint:     reader.GetString(1),
                Method:       reader.GetString(2),
                StatusCode:   reader.GetInt32(3),
                DurationMs:   reader.GetInt32(4),
                Source:       reader.GetString(5),
                ProviderError: reader.IsDBNull(6) ? null : reader.GetString(6),
                QueryText:    reader.IsDBNull(7) ? null : reader.GetString(7),
                AgentAddress: reader.IsDBNull(8) ? null : reader.GetString(8),
                RemoteIp:     reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return result;
    }

    // ----- internal helper accumulators -----

    private sealed class EndpointAccumulator
    {
        public long Count;
        public long Count2xx, Count4xx, Count5xx, Count429;
        public List<int> Durations = new();
        public Dictionary<string, long> BySource = new(StringComparer.Ordinal);

        public void Push(string source, int status, int dur)
        {
            Count++;
            Durations.Add(dur);
            BySource[source] = BySource.GetValueOrDefault(source) + 1;
            if (status == 429) Count429++;
            else if (status >= 500 && status <= 599) Count5xx++;
            else if (status >= 400 && status <= 499) Count4xx++;
            else if (status >= 200 && status <= 299) Count2xx++;
        }

        public MetricsEndpointRow ToRow(string endpoint)
        {
            int p50 = 0, p95 = 0;
            if (Durations.Count > 0)
            {
                Durations.Sort();
                p50 = Durations[Math.Min(Durations.Count - 1, (int)(Durations.Count * 0.5))];
                p95 = Durations[Math.Min(Durations.Count - 1, (int)(Durations.Count * 0.95))];
            }
            return new MetricsEndpointRow(endpoint, Count, p50, p95,
                Count2xx, Count4xx, Count5xx, Count429, BySource);
        }
    }

    private sealed class EndpointApproximator
    {
        public long Count;
        public long Count2xx, Count4xx, Count5xx, Count429;
        public long SumDuration;
        public Dictionary<string, long> BySource = new(StringComparer.Ordinal);

        public void Push(string source, string statusCl, long count, long sumDur)
        {
            Count       += count;
            SumDuration += sumDur;
            BySource[source] = BySource.GetValueOrDefault(source) + count;
            switch (statusCl)
            {
                case "2xx": Count2xx += count; break;
                case "4xx": Count4xx += count; break;
                case "5xx": Count5xx += count; break;
                case "429": Count429 += count; break;
            }
        }

        public MetricsEndpointRow ToRow(string endpoint)
        {
            var avg = Count == 0 ? 0 : (int)(SumDuration / Count);
            return new MetricsEndpointRow(endpoint, Count, avg, (int)(avg * 1.5),
                Count2xx, Count4xx, Count5xx, Count429, BySource);
        }
    }
}
