using QuotaGlass.Models;

namespace QuotaGlass.Services;

/// <summary>
/// 리셋 티켓을 실제로 사용할 수 있는 provider. 호출은 되돌릴 수 없다.
/// </summary>
public interface IResetCreditRedeemer
{
    Task<ResetRedeemResult> RedeemResetCreditAsync(
        ResetCreditRedeemOption option,
        string requestId,
        CancellationToken cancellationToken);
}

public sealed record ResetRedeemResult(bool Succeeded, string Message);
