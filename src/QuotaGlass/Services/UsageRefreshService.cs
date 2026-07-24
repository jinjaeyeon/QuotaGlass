using System.Runtime.CompilerServices;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class UsageRefreshService(IReadOnlyList<IUsageProvider> providers)
{
    public int ProviderCount => providers.Count;

    public async Task<IReadOnlyList<UsageSnapshot>> RefreshAsync(
        CancellationToken cancellationToken)
    {
        var tasks = providers.Select(provider =>
            FetchSafelyAsync(provider, cancellationToken));

        return await Task.WhenAll(tasks);
    }

    public async IAsyncEnumerable<UsageRefreshResult> RefreshAsCompletedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = providers
            .Select((provider, index) => FetchIndexedSafelyAsync(
                provider,
                index,
                cancellationToken))
            .ToList();

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            yield return await completed;
        }
    }

    private static async Task<UsageRefreshResult> FetchIndexedSafelyAsync(
        IUsageProvider provider,
        int providerIndex,
        CancellationToken cancellationToken)
    {
        var snapshot = await FetchSafelyAsync(provider, cancellationToken);
        return new UsageRefreshResult(providerIndex, snapshot);
    }

    private static async Task<UsageSnapshot> FetchSafelyAsync(
        IUsageProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.FetchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new UsageSnapshot(
                provider.ProviderId,
                provider.DisplayName,
                provider.IconText,
                provider.AccountLabel,
                [],
                DateTimeOffset.Now,
                "수집 오류",
                UsageSnapshotState.Error,
                $"사용량을 읽지 못했습니다 · {exception.Message}");
        }
    }
}

public sealed record UsageRefreshResult(
    int ProviderIndex,
    UsageSnapshot Snapshot);
