using System.Collections.ObjectModel;
using System.Windows.Threading;
using QuotaGlass.Models;
using QuotaGlass.Services;

namespace QuotaGlass.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private UsageRefreshService _refreshService;
    private readonly ManagedCliInstaller _managedCliInstaller = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, int> _providerOrder = new(
        StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedProviderIds;
    private readonly HashSet<string> _taskbarWidgetProviderIds;
    private CancellationTokenSource? _refreshCancellation;
    private bool _isRefreshing;
    private string _updateSummary = "불러오는 중…";
    private bool _showDetails = true;

    public MainViewModel()
    {
        _collapsedProviderIds = CollapsedProviderStore.Load();
        _taskbarWidgetProviderIds = TaskbarWidgetProviderStore.LoadOrDefault();
        _refreshService = new UsageRefreshService([]);
        ReloadProviders();

        RefreshCommand = new AsyncRelayCommand(
            RefreshAsync,
            () => !IsRefreshing);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    public ObservableCollection<ProviderUsageViewModel> Providers { get; } = [];
    public ObservableCollection<ProviderUsageViewModel> VisibleProviders { get; } = [];
    public ObservableCollection<ProviderUsageViewModel> CollapsedProviders { get; } = [];
    public ObservableCollection<ManagedCliOptionViewModel> ManagedCliOptions { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public event EventHandler? ProvidersUpdated;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RaisePropertyChanged(nameof(RefreshGlyph));
            }
        }
    }

    public string RefreshGlyph => IsRefreshing ? "…" : "↻";

    public string UpdateSummary
    {
        get => _updateSummary;
        private set => SetProperty(ref _updateSummary, value);
    }

    public bool ShowDetails
    {
        get => _showDetails;
        set => SetProperty(ref _showDetails, value);
    }

    public void CollapseProvider(string providerId)
    {
        if (_collapsedProviderIds.Add(providerId))
        {
            CollapsedProviderStore.Save(_collapsedProviderIds);
            UpdateProviderVisibility();
        }
    }

    public void ExpandProvider(string providerId)
    {
        if (_collapsedProviderIds.Remove(providerId))
        {
            var wasExcluded = !_taskbarWidgetProviderIds.Contains(providerId);
            CollapsedProviderStore.Save(_collapsedProviderIds);
            UpdateProviderVisibility();
            if (wasExcluded)
            {
                _ = RefreshAsync();
            }
        }
    }

    public void SetTaskbarWidgetProviderVisibility(
        string providerId,
        bool isVisible)
    {
        var wasExcluded = !ShouldRefreshProvider(
            providerId,
            _collapsedProviderIds,
            _taskbarWidgetProviderIds);
        if (isVisible)
        {
            _taskbarWidgetProviderIds.Add(providerId);
        }
        else
        {
            _taskbarWidgetProviderIds.Remove(providerId);
        }

        if (wasExcluded && isVisible)
        {
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        IsRefreshing = true;
        UpdateSummary = "사용량 갱신 중…";

        try
        {
            var refreshedProviderCount = 0;
            var refreshableProviderCount = _refreshService.CountProviders(
                ShouldRefreshProvider);
            await foreach (var result in _refreshService.RefreshAsCompletedAsync(
                               ShouldRefreshProvider,
                               _refreshCancellation.Token))
            {
                _providerOrder[result.Snapshot.Provider] = result.ProviderIndex;
                ApplySnapshot(
                    result.Snapshot,
                    result.ProviderIndex,
                    DateTimeOffset.Now);

                refreshedProviderCount++;
                ProvidersUpdated?.Invoke(this, EventArgs.Empty);
                UpdateSummary =
                    $"사용량 갱신 중… {refreshedProviderCount}/{refreshableProviderCount}";
            }

            ScheduleNextRefresh(DateTimeOffset.Now);
            UpdateSummary = Providers.Count == 0
                ? "지원하는 AI 에이전트를 찾지 못했습니다"
                : $"방금 갱신 · 설치된 에이전트 {Providers.Count}개 · 다음 자동 갱신 5분";
        }
        catch (OperationCanceledException)
        {
            UpdateSummary = "갱신 시간이 초과되었습니다";
        }
        catch (Exception exception)
        {
            UpdateSummary = $"갱신 실패 · {exception.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ReloadProviders()
    {
        var installations = new AgentInstallationDetector().Detect();
        _refreshService = new UsageRefreshService(
            UsageProviderFactory.CreateProviders(installations));
        Providers.Clear();
        VisibleProviders.Clear();
        CollapsedProviders.Clear();
        var now = DateTimeOffset.Now;
        for (var index = 0; index < installations.Count; index++)
        {
            var installation = installations[index];
            _providerOrder[installation.ProviderId] = index;
            Providers.Add(new ProviderUsageViewModel(
                new UsageSnapshot(
                    installation.ProviderId,
                    installation.DisplayName,
                    installation.IconText,
                    installation.AccountLabel,
                    [],
                    now,
                    "갱신 대기",
                    UsageSnapshotState.AdapterPending,
                    ShouldRefreshProvider(installation.ProviderId)
                        ? "사용량 확인 중…"
                        : "메인 창과 위젯에서 숨겨져 갱신하지 않음"),
                now));
        }
        UpdateProviderVisibility();
        ReloadManagedCliOptions(installations);
    }

    private Task ReloadProvidersAndRefreshAsync()
    {
        ReloadProviders();
        return RefreshAsync();
    }

    private void ReloadManagedCliOptions(
        IReadOnlyList<AgentInstallation> installations)
    {
        ManagedCliOptions.Clear();
        foreach (var definition in ManagedCliCatalog.All)
        {
            var managedPath = ManagedCliStore.FindExecutable(
                definition.ProviderId);
            var installation = installations.FirstOrDefault(candidate =>
                candidate.ProviderId == definition.ProviderId);
            var hasSystemCli = installation?.ExecutablePath is not null &&
                               !string.Equals(
                                   installation.ExecutablePath,
                                   managedPath,
                                   StringComparison.OrdinalIgnoreCase);
            if (hasSystemCli)
            {
                continue;
            }

            ManagedCliOptions.Add(
                new ManagedCliOptionViewModel(
                    definition,
                    _managedCliInstaller,
                    managedPath,
                    ReloadProvidersAndRefreshAsync));
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e) =>
        await RefreshAsync();

    private void ApplySnapshot(
        Models.UsageSnapshot snapshot,
        int providerIndex,
        DateTimeOffset now)
    {
        var updatedProvider = new ProviderUsageViewModel(snapshot, now);

        for (var index = 0; index < Providers.Count; index++)
        {
            if (Providers[index].Provider == snapshot.Provider)
            {
                Providers[index] = updatedProvider;
                UpdateProviderVisibility();
                return;
            }
        }

        var insertionIndex = 0;
        while (insertionIndex < Providers.Count
               && _providerOrder.TryGetValue(
                   Providers[insertionIndex].Provider,
                   out var existingProviderIndex)
               && existingProviderIndex < providerIndex)
        {
            insertionIndex++;
        }

        Providers.Insert(insertionIndex, updatedProvider);
        UpdateProviderVisibility();
    }

    private void UpdateProviderVisibility()
    {
        ReplaceContents(
            VisibleProviders,
            Providers.Where(provider =>
                !_collapsedProviderIds.Contains(provider.Provider)));
        ReplaceContents(
            CollapsedProviders,
            Providers.Where(provider =>
                _collapsedProviderIds.Contains(provider.Provider)));
    }

    private static void ReplaceContents(
        ObservableCollection<ProviderUsageViewModel> destination,
        IEnumerable<ProviderUsageViewModel> source)
    {
        destination.Clear();
        foreach (var provider in source)
        {
            destination.Add(provider);
        }
    }

    private void ScheduleNextRefresh(DateTimeOffset now)
    {
        var nearestReset = Providers
            .Where(provider => ShouldRefreshProvider(provider.Provider))
            .SelectMany(provider => provider.Meters)
            .Where(meter => !meter.IsReset && meter.ResetsAt > now)
            .Select(meter => (DateTimeOffset?)meter.ResetsAt)
            .Min();
        var interval = TimeSpan.FromMinutes(5);

        if (nearestReset is { } reset)
        {
            var untilReset = reset - now + TimeSpan.FromSeconds(2);
            if (untilReset < interval)
            {
                interval = untilReset;
            }
        }

        _refreshTimer.Interval = interval < TimeSpan.FromSeconds(10)
            ? TimeSpan.FromSeconds(10)
            : interval;
    }

    private bool ShouldRefreshProvider(string providerId) =>
        ShouldRefreshProvider(
            providerId,
            _collapsedProviderIds,
            _taskbarWidgetProviderIds);

    internal static bool ShouldRefreshProvider(
        string providerId,
        IReadOnlySet<string> collapsedProviderIds,
        IReadOnlySet<string> taskbarWidgetProviderIds) =>
        !collapsedProviderIds.Contains(providerId) ||
        taskbarWidgetProviderIds.Contains(providerId);
}
