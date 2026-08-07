using System.Diagnostics;
using QuotaGlass.Services;

namespace QuotaGlass.ViewModels;

public sealed class ManagedCliOptionViewModel : ObservableObject
{
    private readonly ManagedCliDefinition _definition;
    private readonly ManagedCliInstaller _installer;
    private readonly Func<Task> _reloadProviders;
    private string? _executablePath;
    private string _status;

    public ManagedCliOptionViewModel(
        ManagedCliDefinition definition,
        ManagedCliInstaller installer,
        string? executablePath,
        Func<Task> reloadProviders)
    {
        _definition = definition;
        _installer = installer;
        _executablePath = executablePath;
        _reloadProviders = reloadProviders;
        _status = executablePath is null
            ? "시스템 CLI 없음 · 필요할 때만 앱 전용으로 설치"
            : "QuotaGlass 전용 CLI 설치됨";
        ActionCommand = new AsyncRelayCommand(ExecuteActionAsync);
    }

    public string ProviderId => _definition.ProviderId;
    public string DisplayName => _definition.DisplayName;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsInstalled => _executablePath is not null;
    public string ActionText => IsInstalled ? "로그인/설정" : "전용 CLI 설치";
    public AsyncRelayCommand ActionCommand { get; }

    private async Task ExecuteActionAsync()
    {
        if (_executablePath is not null)
        {
            try
            {
                LaunchInteractive(_executablePath, _definition.ProviderId);
                Status = _definition.ProviderId == "github-copilot"
                    ? "열린 창에서 /login을 입력하세요"
                    : "로그인/설정 창을 열었습니다";
            }
            catch (Exception exception)
            {
                Status = $"실행 실패 · {exception.Message}";
            }

            return;
        }

        Status = "공식 릴리스 확인 중…";
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(15));
            var result = await _installer.InstallAsync(
                _definition.ProviderId,
                timeout.Token);
            _executablePath = result.ExecutablePath;
            Status = $"{result.Version} 설치 완료";
            RaisePropertyChanged(nameof(IsInstalled));
            RaisePropertyChanged(nameof(ActionText));
            await _reloadProviders();
        }
        catch (OperationCanceledException)
        {
            Status = "설치 시간이 초과되었습니다";
        }
        catch (Exception exception)
        {
            Status = $"설치 실패 · {exception.Message}";
        }
    }

    private static void LaunchInteractive(
        string executablePath,
        string providerId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            UseShellExecute = false
        };

        switch (providerId)
        {
            case "codex":
                startInfo.ArgumentList.Add("login");
                break;
            case "claude-code":
                startInfo.ArgumentList.Add("auth");
                startInfo.ArgumentList.Add("login");
                startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
                break;
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "CLI 로그인/설정 창을 열지 못했습니다.");
    }
}
