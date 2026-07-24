using QuotaGlass.Models;

namespace QuotaGlass.Services;

public interface IUsageProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    string IconText { get; }
    string AccountLabel { get; }

    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken);
}
