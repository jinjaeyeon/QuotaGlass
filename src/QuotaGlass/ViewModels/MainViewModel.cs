using System.Collections.ObjectModel;
using System.Windows.Threading;
using QuotaGlass.Services;

namespace QuotaGlass.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly UsageRefreshService _refreshService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, int> _providerOrder = new(
        StringComparer.Ordinal);
    private CancellationTokenSource? _refreshCancellation;
    private bool _isRefreshing;
    private string _updateSummary = "불러오는 중…";
    private bool _showDetails = true;

    public MainViewModel()
    {
        _refreshService = new UsageRefreshService(
            UsageProviderFactory.CreateInstalledProviders());

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
            await foreach (var result in _refreshService.RefreshAsCompletedAsync(
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
                    $"사용량 갱신 중… {refreshedProviderCount}/{_refreshService.ProviderCount}";
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
    }

    private void ScheduleNextRefresh(DateTimeOffset now)
    {
        var nearestReset = Providers
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
}
