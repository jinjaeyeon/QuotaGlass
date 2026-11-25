namespace QuotaGlass.Models;

public sealed record ResetCreditSummary(
    long AvailableCount,
    DateTimeOffset? EarliestExpiresAt,
    ResetCreditRedeemOption? Redeem = null);

/// <summary>
/// 지금 사용 대상이 되는 리셋 티켓 한 종류. 사용 버튼은 이 정보로 만든다.
/// </summary>
public sealed record ResetCreditRedeemOption(
    string GrantId,
    string Label,
    string? Kind,
    long ResetsLeft,
    DateTimeOffset? ExpiresAt,
    bool UsableNow,
    string? UnavailableReason);
