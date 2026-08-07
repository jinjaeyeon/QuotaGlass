namespace QuotaGlass.Models;

public sealed record ResetCreditSummary(
    long AvailableCount,
    DateTimeOffset? EarliestExpiresAt);
