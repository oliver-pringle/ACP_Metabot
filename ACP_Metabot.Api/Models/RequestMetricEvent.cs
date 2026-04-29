namespace ACP_Metabot.Api.Models;

// Single per-request telemetry row, emitted by RequestMetricsMiddleware
// onto MetricsChannel. MetricsWriterService drains the channel and inserts
// into the request_log SQLite table. Schema mirrored 1:1 with the table.
public record RequestMetricEvent(
    DateTime TimestampUtc,
    string   Endpoint,
    string   Method,
    int      StatusCode,
    int      DurationMs,
    string   Source,
    string?  UserAgent,
    string?  CallerId,
    string?  RemoteIp,
    string?  QueryText,
    string?  AgentAddress,
    string?  ProviderError);
