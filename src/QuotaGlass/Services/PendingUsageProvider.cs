using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class PendingUsageProvider(AgentInstallation installation)
    : IUsageProvider
{
    public string ProviderId => installation.ProviderId;
    public string DisplayName => installation.DisplayName;
    public string IconText => installation.IconText;
    public string AccountLabel => installation.AccountLabel;

    public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var version = string.IsNullOrWhiteSpace(installation.Version)
            ? null
            : $" · {installation.Version}";

        return Task.FromResult(
            new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                AccountLabel,
                [],
                DateTimeOffset.Now,
                "설치 감지",
                UsageSnapshotState.AdapterPending,
                $"설치됨{version} · 사용량 어댑터 준비 중"));
    }
}
