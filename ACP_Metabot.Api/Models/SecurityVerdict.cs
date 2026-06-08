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

/// <summary>
/// One immutable security_scan_history row — the FULL result of a single scan,
/// retained append-only (never overwritten). Mirrors the latest-cache
/// SecurityVerdict but adds the raw findings JSON + raw verdict discriminator and
/// an autoincrement Id. Written by SecurityScanWorker every tick, alongside the
/// latest-cache upsert. findings_json / last_error are server-side only and are
/// NEVER surfaced on the public digest (P9 / P10).
/// </summary>
public sealed record ScanHistoryRow(
    long Id,
    string AgentAddress,          // lower-cased
    string ScannedAt,             // ISO-8601 "O"
    string Status,                // SecurityStatus.* — scanned | not_auditable | error (never 'pending')
    int? Score,
    string? Grade,
    int? ObservableCount,
    int? FindingCount,
    string? SeverityCountsJson,
    string? Verdict,              // raw SecurityBot verdict discriminator (PASS / NOT_AUDITABLE / ...)
    string? CorpusVersion,
    string? FindingsJson,         // full raw findings[] array verbatim
    string? LastError);
