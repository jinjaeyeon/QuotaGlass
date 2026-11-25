namespace QuotaGlass.Services;

internal static class ClaudeResetMessages
{
    public static ResetRedeemResult Describe(ResetClaimResult result) =>
        result.Outcome switch
        {
            ResetClaimOutcome.Success => new ResetRedeemResult(
                true,
                result.ResetsLeft is { } left
                    ? $"리셋 티켓을 사용했습니다. 남은 티켓 {left}장"
                    : "리셋 티켓을 사용했습니다."),
            ResetClaimOutcome.AlreadyUsed => new ResetRedeemResult(
                false,
                "이미 사용된 요청입니다. 사용량을 새로고침합니다."),
            ResetClaimOutcome.NotLimited => new ResetRedeemResult(
                false,
                "아직 한도에 도달하지 않아 지금은 사용할 수 없습니다."),
            ResetClaimOutcome.Cooldown => new ResetRedeemResult(
                false,
                "쿨다운 중이라 잠시 후 다시 시도해야 합니다."),
            ResetClaimOutcome.Ineligible => new ResetRedeemResult(
                false,
                "이 계정은 리셋 티켓을 사용할 수 없습니다."),
            ResetClaimOutcome.Unavailable => new ResetRedeemResult(
                false,
                "리셋 티켓을 사용할 수 없는 상태입니다."),
            ResetClaimOutcome.NotUsable => new ResetRedeemResult(
                false,
                DescribeUnavailable(result.Reason)),
            ResetClaimOutcome.RateLimited => new ResetRedeemResult(
                false,
                "요청이 너무 많습니다. 잠시 후 다시 시도해 주세요."),
            ResetClaimOutcome.AuthError => new ResetRedeemResult(
                false,
                "Claude 인증을 확인하지 못했습니다. 다시 로그인해 주세요."),
            _ => new ResetRedeemResult(
                false,
                "리셋 티켓 사용에 실패했습니다. 사용되지 않았을 수 있으니 " +
                "새로고침 후 확인해 주세요.")
        };

    public static string DescribeUnavailable(string? reason) =>
        reason switch
        {
            "needs_limit" => "한도에 도달했을 때만 사용할 수 있습니다.",
            "cooldown" => "쿨다운 중이라 지금은 사용할 수 없습니다.",
            "paused" => "일시 중지된 티켓입니다.",
            "expired" => "기한이 지난 티켓입니다.",
            null or "" => "지금은 사용할 수 없습니다.",
            _ => $"지금은 사용할 수 없습니다. ({reason})"
        };
}
