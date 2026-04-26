namespace ACP_Metabot.Api.Models;

public record Watch(
    string Id,
    long JobId,
    string BuyerAddress,
    string Query,
    string WebhookUrl,
    int DurationDays,
    int IntervalHours,
    double? MinScore,
    double? PriceMaxUsdc,
    int MaxAlerts,
    int AlertsDelivered,
    int WebhookConsecutiveFailures,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? LastPolledAt);
