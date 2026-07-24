namespace QuotaGlass.Models;

public sealed record UsageSnapshot(
    string Provider,
    string DisplayName,
    string IconText,
    string AccountLabel,
    IReadOnlyList<UsageMeter> Meters,
    DateTimeOffset ObservedAt,
    string Source,
    UsageSnapshotState State = UsageSnapshotState.Available,
    string? StatusMessage = null);

public enum UsageSnapshotState
{
    Available,
    AdapterPending,
    Error
}
