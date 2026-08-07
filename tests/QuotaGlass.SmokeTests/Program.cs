using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaGlass.Models;
using QuotaGlass.Services;
using QuotaGlass.ViewModels;

var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.FromHours(9));
var meter = new UsageMeter(
    "test",
    "테스트 제한",
    30,
    100,
    "percent",
    now - TimeSpan.FromHours(4),
    now + TimeSpan.FromHours(6));

Require(Approximately(meter.RemainingRatio, 0.30), "남은 사용량 비율 계산");
Require(Approximately(meter.RemainingTimeRatio(now), 0.60), "남은 시간 비율 계산");
Require(Approximately(meter.PaceDelta(now), -0.30), "소진 속도 차이 계산");

var viewModel = new MeterUsageViewModel(meter, now);
Require(viewModel.IsWarning, "5%p 초과 소진 경고");
Require(viewModel.StatusText.Contains("30％p", StringComparison.Ordinal), "경고 차이 표시");
Require(
    viewModel.RemainingWithResetText == "30% · 6시간 0분 후 초기화",
    "보조 meter 잔여량과 초기화까지 남은 시간 표시");

var refreshService = new UsageRefreshService(MockUsageProvider.CreateDefaults());
var snapshots = await refreshService.RefreshAsync(CancellationToken.None);
var streamedResults = new List<UsageRefreshResult>();
await foreach (var result in refreshService.RefreshAsCompletedAsync(
                   CancellationToken.None))
{
    streamedResults.Add(result);
}

Require(snapshots.Count == 6, "기본 provider 개수");
Require(streamedResults.Count == 6, "provider 완료 순서 갱신 개수");
var filteredResults = new List<UsageRefreshResult>();
await foreach (var result in refreshService.RefreshAsCompletedAsync(
                   providerId => providerId != "claude-code",
                   CancellationToken.None))
{
    filteredResults.Add(result);
}
Require(
    filteredResults.Count == 5 &&
    filteredResults.All(result => result.Snapshot.Provider != "claude-code"),
    "숨긴 provider 갱신 제외");
Require(
    streamedResults.Select(result => result.ProviderIndex).Distinct().Count() == 6,
    "provider 완료 순서 갱신 인덱스");
Require(
    !MainViewModel.ShouldRefreshProvider(
        "claude-code",
        new HashSet<string>(["claude-code"]),
        new HashSet<string>()),
    "메인 창과 위젯에서 모두 숨긴 provider 갱신 생략");
Require(
    MainViewModel.ShouldRefreshProvider(
        "claude-code",
        new HashSet<string>(["claude-code"]),
        new HashSet<string>(["claude-code"])),
    "메인 창에서 접어도 위젯 표시 provider 갱신");
Require(
    MainViewModel.ShouldRefreshProvider(
        "claude-code",
        new HashSet<string>(),
        new HashSet<string>()),
    "위젯에서 숨겨도 메인 창 표시 provider 갱신");
Require(snapshots.All(snapshot => snapshot.Meters.Count > 0), "provider별 meter 생성");
var providerViewModels = snapshots
    .Select(snapshot => new ProviderUsageViewModel(snapshot, now))
    .ToArray();
Require(
    providerViewModels.All(provider => provider.HasVectorIcon),
    "제공된 에이전트 SVG geometry 연결");
Require(
    snapshots.Single(snapshot => snapshot.Provider == "claude-code").Meters.Count == 2,
    "Claude Code 5시간/주간 meter");
var codex = snapshots.Single(snapshot => snapshot.Provider == "codex");
Require(codex.AccountLabel.Contains("팀", StringComparison.Ordinal), "Codex 팀 계정");
Require(
    codex.Meters is [{ Id: "monthly" }],
    "팀 Codex 월간 meter");

const string codexResetCreditsFixture =
    """
    {
      "rateLimitResetCredits": {
        "availableCount": 3,
        "credits": [
          {
            "id": "later",
            "status": "available",
            "expiresAt": 1785438000
          },
          {
            "id": "redeemed",
            "status": "redeemed",
            "expiresAt": 1784000000
          },
          {
            "id": "earlier",
            "status": "available",
            "expiresAt": 1784876400
          }
        ]
      }
    }
    """;
using (var codexResetCreditsDocument = JsonDocument.Parse(
           codexResetCreditsFixture))
{
    var resetCredits = CodexRateLimitResetCreditParser.Parse(
        codexResetCreditsDocument.RootElement);
    Require(resetCredits?.AvailableCount == 3, "Codex 리셋 티켓 잔여 개수");
    Require(
        resetCredits?.EarliestExpiresAt ==
        DateTimeOffset.FromUnixTimeSeconds(1784876400),
        "Codex 가장 이른 리셋 티켓 기한");

    var resetCreditsViewModel = new ProviderUsageViewModel(
        codex with { ResetCredits = resetCredits },
        now);
    Require(
        resetCreditsViewModel.ResetCreditSummaryText?.Contains(
            "리셋 티켓 3장",
            StringComparison.Ordinal) == true,
        "Codex 리셋 티켓 카드 문구");
}

const string copilotQuotaFixture =
    """
    {
      "quotaSnapshots": {
        "chat": {
          "isUnlimitedEntitlement": false,
          "entitlementRequests": 200,
          "usedRequests": 35,
          "remainingPercentage": 82.5,
          "resetDate": "2026-08-01T00:00:00Z"
        },
        "completions": {
          "isUnlimitedEntitlement": false,
          "entitlementRequests": 2000,
          "usedRequests": 500,
          "remainingPercentage": 75,
          "resetDate": "2026-08-01T00:00:00Z"
        },
        "premium_interactions": {
          "isUnlimitedEntitlement": false,
          "entitlementRequests": 0,
          "usedRequests": 0,
          "remainingPercentage": 0
        }
      }
    }
    """;
using (var copilotDocument = JsonDocument.Parse(copilotQuotaFixture))
{
    var copilotMeters = GitHubCopilotQuotaParser.Parse(
        copilotDocument.RootElement,
        now);
    Require(copilotMeters.Count == 2, "GitHub Copilot quota meter 개수");
    Require(
        Approximately(
            copilotMeters.Single(item => item.Id == "chat").RemainingRatio,
            0.825),
        "GitHub Copilot Chat 잔량");
}

const string cursorUsageFixture =
    """
    {
      "billingCycleStart": "1783786404220",
      "billingCycleEnd": "1786464804220",
      "planUsage": {
        "autoPercentUsed": 8,
        "apiPercentUsed": 17,
        "totalPercentUsed": 25
      }
    }
    """;
using (var cursorDocument = JsonDocument.Parse(cursorUsageFixture))
{
    var cursorMeters = CursorUsageParser.Parse(
        cursorDocument.RootElement,
        now);
    Require(cursorMeters.Count == 1, "Cursor 월간 meter 개수");
    Require(
        Approximately(cursorMeters[0].RemainingRatio, 0.75),
        "Cursor 월간 포함량 잔량");
    Require(
        cursorMeters[0].ResetsAt > cursorMeters[0].WindowStart,
        "Cursor 결제 주기 시각");
}

var installations = new AgentInstallationDetector().Detect();
Require(
    installations.Select(item => item.ProviderId).Distinct().Count() ==
    installations.Count,
    "설치 감지 결과 중복 없음");
Require(
    FullscreenWindowDetector.CoversMonitor(
        0, 0, 2560, 1440,
        0, 0, 2560, 1440),
    "전체 화면 창 감지");
Require(
    FullscreenWindowDetector.CoversMonitor(
        -1, -1, 2561, 1441,
        0, 0, 2560, 1440),
    "모니터보다 큰 전체 화면 창 감지");
Require(
    FullscreenWindowDetector.CoversMonitor(
        1, 1, 2559, 1439,
        0, 0, 2560, 1440),
    "전체 화면 경계 오차 허용");
Require(
    !FullscreenWindowDetector.CoversMonitor(
        0, 0, 2560, 1400,
        0, 0, 2560, 1440),
    "작업표시줄을 제외한 최대화 창은 전체 화면에서 제외");
Require(
    TaskbarVisibilityDetector.HasVisibleThickness(
        0, 1400, 2560, 1440,
        0, 0, 2560, 1440),
    "자동 숨김 작업표시줄 노출 감지");
Require(
    !TaskbarVisibilityDetector.HasVisibleThickness(
        0, 1438, 2560, 1478,
        0, 0, 2560, 1440),
    "자동 숨김 작업표시줄 숨김 감지");
Require(
    TaskbarVisibilityDetector.HasVisibleThickness(
        0, 0, 40, 1440,
        0, 0, 2560, 1440),
    "세로 작업표시줄 노출 감지");
Require(
    !TaskbarVisibilityDetector.HasVisibleThickness(
        -38, 0, 2, 1440,
        0, 0, 2560, 1440),
    "세로 작업표시줄 숨김 감지");

const string claudeStatusLineFixture =
    """
    {
      "rate_limits": {
        "five_hour": {
          "used_percentage": 23.5,
          "resets_at": 1784876400
        },
        "seven_day": {
          "used_percentage": 41.2,
          "resets_at": 1785438000
        }
      }
    }
    """;
var claudeMeters = ClaudeRateLimitParser.Parse(claudeStatusLineFixture);
var cachedWorkingDirectoryFixture = System.Text.Json.JsonSerializer.Serialize(
    new { cwd = Environment.CurrentDirectory });
Require(
    string.Equals(
        ClaudeCodeUsageProvider.ReadCachedWorkingDirectory(
            cachedWorkingDirectoryFixture),
        Environment.CurrentDirectory,
        StringComparison.OrdinalIgnoreCase),
    "Claude 캐시에 기록된 신뢰 작업 폴더 사용");
Require(claudeMeters.Count == 2, "Claude status-line rate limit 개수");
Require(
    Approximately(
        claudeMeters.Single(item => item.Label == "5시간").RemainingRatio,
        0.765),
    "Claude 5시간 잔량");
Require(
    Approximately(
        claudeMeters.Single(item => item.Label == "주간").RemainingRatio,
        0.588),
    "Claude 주간 잔량");
Require(
    ClaudeCodeUsageProvider.IsStatusLineCacheFresh(
        now.AddMinutes(-9),
        now),
    "Claude status-line 최신 캐시 허용");
Require(
    !ClaudeCodeUsageProvider.IsStatusLineCacheFresh(
        now.AddMinutes(-11),
        now),
    "Claude status-line 오래된 캐시 거부");
Require(
    !ClaudeCodeUsageProvider.IsStatusLineCacheFresh(
        now.AddMinutes(1),
        now),
    "Claude status-line 미래 시각 캐시 거부");

const string claudeUsageScreenFixture =
    """
    Current session
    81% 81% used
    Resets 2:59pm (Asia/Seoul)
    Current week (all models)
    32% 32% used
    Resets Jul 26, 10:59am (Asia/Seoul)
    """;
var claudeScreenMeters = ClaudeUsageScreenParser.Parse(
    claudeUsageScreenFixture,
    now);
Require(claudeScreenMeters.Count == 2, "Claude /usage 화면 meter 개수");
Require(
    ClaudeCodeUsageProvider.HasCompleteUsageScreen(
        claudeUsageScreenFixture,
        now),
    "Claude /usage 화면 완료 즉시 감지");
Require(
    !ClaudeCodeUsageProvider.HasCompleteUsageScreen(
        "Current session\n81% used\nResets 2:59pm (Asia/Seoul)",
        now),
    "Claude /usage 부분 화면을 완료로 오인하지 않음");
Require(
    Approximately(
        claudeScreenMeters.Single(item => item.Label == "5시간")
            .RemainingRatio,
        0.19),
    "Claude /usage 5시간 잔량");

var expiredClaudeScreenMeters = ClaudeUsageScreenParser.Parse(
    claudeUsageScreenFixture,
    now.AddHours(3));
Require(
    expiredClaudeScreenMeters
        .Single(item => item.Label == "5시간")
        .ResetsAt < now.AddHours(3),
    "Claude /usage 지난 5시간 reset을 다음 날로 오인하지 않음");

var reconciledClaudeMeters =
    ClaudeCodeUsageProvider.ReconcileExpiredMeters(
        expiredClaudeScreenMeters,
        claudeMeters,
        now.AddDays(30));
Require(
    reconciledClaudeMeters.Count == 0,
    "Claude 만료 meter를 100%로 추정하지 않음");

var partialClaudeMeters =
    ClaudeCodeUsageProvider.ReconcileExpiredMeters(
        claudeScreenMeters.Where(item => item.Label == "5시간").ToArray(),
        claudeMeters,
        now);
Require(
    partialClaudeMeters.Count == 2 &&
    partialClaudeMeters.Any(item => item.Label == "주간"),
    "Claude 최신 캐시로 일시적인 부분 파싱 보완");

RunStatusLineInstallerTests();
RunCollapsedProviderStoreTests();
RunManagedCliTests();

const string antigravityQuotaFixture =
    """
    {
      "response": {
        "groups": [
          {
            "displayName": "Gemini Models",
            "buckets": [
              {
                "bucketId": "gemini-weekly",
                "window": "weekly",
                "remainingFraction": 0.75,
                "resetTime": "2026-07-31T05:44:03Z"
              },
              {
                "bucketId": "gemini-5h",
                "window": "5h",
                "remainingFraction": 0.40,
                "resetTime": "2026-07-24T10:44:03Z"
              }
            ]
          }
        ]
      }
    }
    """;
var antigravityMeters = AntigravityQuotaParser.Parse(
    antigravityQuotaFixture);
Require(antigravityMeters.Count == 2, "Antigravity quota bucket 개수");
Require(
    Approximately(
        antigravityMeters.Single(item => item.Id == "gemini-5h")
            .RemainingRatio,
        0.40),
    "Antigravity 5시간 잔량");

if (args.Contains("--integration", StringComparer.Ordinal))
{
    var installedProviders = UsageProviderFactory.CreateInstalledProviders();
    var actualSnapshots = await new UsageRefreshService(installedProviders)
        .RefreshAsync(CancellationToken.None);

    foreach (var snapshot in actualSnapshots)
    {
        Console.WriteLine(
            $"{snapshot.DisplayName}: {snapshot.State} " +
            $"({snapshot.Meters.Count} meter)" +
            (snapshot.Meters.Count == 0
                ? string.Empty
                : $" · {string.Join(
                    ", ",
                    snapshot.Meters.Select(meter =>
                        $"{meter.Label} {meter.RemainingRatio:P0}"))}") +
            (string.IsNullOrWhiteSpace(snapshot.StatusMessage)
                ? string.Empty
                : $" · {snapshot.StatusMessage}"));
    }

    var actualCodex = actualSnapshots.Single(snapshot =>
        snapshot.Provider == "codex");
    Require(
        actualCodex.State == UsageSnapshotState.Available,
        "Codex app-server 실제 사용량");
    Require(
        actualCodex.Meters.Any(meter => meter.Label == "월간"),
        "팀 Codex 월간 window");

    var actualJetBrains = actualSnapshots.Single(snapshot =>
        snapshot.Provider == "jetbrains");
    Require(
        actualJetBrains.State == UsageSnapshotState.Available,
        "JetBrains AI 로컬 quota");
    Require(
        actualJetBrains.Meters.Any(meter => meter.Unit == "credits"),
        "JetBrains AI credits meter");

    var actualClaude = actualSnapshots.Single(snapshot =>
        snapshot.Provider == "claude-code");
    Require(
        actualClaude.State == UsageSnapshotState.Available &&
        actualClaude.Meters.Count == 2,
        "Claude Team /usage 실제 사용량");
    Require(
        actualClaude.Meters.Select(meter => meter.Label).Distinct().Count() ==
        actualClaude.Meters.Count,
        "Claude status-line과 /usage 제한 중복 없음");

    var actualAntigravity = actualSnapshots.Single(snapshot =>
        snapshot.Provider == "antigravity");
    Require(
        actualAntigravity.State == UsageSnapshotState.Available &&
        actualAntigravity.Meters.Count >= 2,
        "Antigravity 모델 그룹별 실제 quota");

    var actualCopilot = actualSnapshots.Single(snapshot =>
        snapshot.Provider == "github-copilot");
    Require(
        actualCopilot.State == UsageSnapshotState.Available &&
        actualCopilot.Meters.Count > 0,
        "GitHub Copilot CLI 실제 quota");

    var actualCursor = actualSnapshots.Single(snapshot =>
        snapshot.Provider == "cursor");
    Require(
        actualCursor.State == UsageSnapshotState.Available &&
        actualCursor.Meters.Count > 0,
        "Cursor CLI 실제 사용량");
}

var uiTestRoot = Path.Combine(
    Path.GetTempPath(),
    $"QuotaGlass.App.{Guid.NewGuid():N}");
var previousUiSettingsPath = Environment.GetEnvironmentVariable(
    "QUOTAGLASS_CLAUDE_SETTINGS_PATH");
var previousUiStateDirectory = Environment.GetEnvironmentVariable(
    "QUOTAGLASS_STATE_DIRECTORY");
try
{
    Environment.SetEnvironmentVariable(
        "QUOTAGLASS_CLAUDE_SETTINGS_PATH",
        Path.Combine(uiTestRoot, ".claude", "settings.json"));
    Environment.SetEnvironmentVariable(
        "QUOTAGLASS_STATE_DIRECTORY",
        Path.Combine(uiTestRoot, "state"));

    var exitCompleted = false;
    Exception? exitFailure = null;
    var exitThread = new Thread(() =>
    {
        try
        {
            var application = new QuotaGlass.App();
            application.EnforceSingleInstance = false;
            application.ForceProcessExitOnShutdown = false;
            application.InitializeComponent();
            application.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () =>
                {
                    var mainWindowField = typeof(QuotaGlass.App).GetField(
                        "_mainWindow",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic);
                    var widget = application.MainWindow;
                    Require(
                        mainWindowField?.GetValue(application) is null &&
                        widget is QuotaGlass.TaskbarWidgetWindow
                        {
                            IsVisible: true
                        },
                        "앱 시작 시 메인 창 지연 생성 및 위젯 표시");
                    var openMenuMethod = typeof(
                        QuotaGlass.TaskbarWidgetWindow).GetMethod(
                        "OpenFullWindowMenuItem_Click",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic);
                    openMenuMethod?.Invoke(
                        widget,
                        [widget, new System.Windows.RoutedEventArgs()]);
                    Require(
                        mainWindowField?.GetValue(application) is
                            QuotaGlass.MainWindow
                        {
                            IsVisible: true
                        },
                        "위젯 클릭 시 메인 창 지연 생성");

                    var dismissMethod = typeof(QuotaGlass.App).GetMethod(
                        "DismissFullWindow",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic);
                    dismissMethod?.Invoke(application, null);
                    Require(
                        mainWindowField?.GetValue(application) is null &&
                        widget.IsVisible,
                        "메인 창 닫을 때 객체 해제 및 위젯 유지");

                    var exitMenuMethod = typeof(
                        QuotaGlass.TaskbarWidgetWindow).GetMethod(
                        "ExitMenuItem_Click",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic);
                    exitMenuMethod?.Invoke(
                        widget,
                        [widget, new System.Windows.RoutedEventArgs()]);
                });
            application.Run();
            exitCompleted = true;
        }
        catch (Exception exception)
        {
            exitFailure = exception;
        }
    });
    exitThread.SetApartmentState(ApartmentState.STA);
    exitThread.Start();
    Require(
        exitThread.Join(TimeSpan.FromSeconds(10)) &&
        exitCompleted &&
        exitFailure is null,
        $"작업표시줄 위젯 종료 시 WPF 메시지 루프 종료{(
            exitFailure is null ? string.Empty : $" · {exitFailure.Message}")}");
}
finally
{
    Environment.SetEnvironmentVariable(
        "QUOTAGLASS_CLAUDE_SETTINGS_PATH",
        previousUiSettingsPath);
    Environment.SetEnvironmentVariable(
        "QUOTAGLASS_STATE_DIRECTORY",
        previousUiStateDirectory);
    if (Directory.Exists(uiTestRoot))
    {
        Directory.Delete(uiTestRoot, recursive: true);
    }
}

Console.WriteLine("QuotaGlass smoke tests passed.");
return;

void RunStatusLineInstallerTests()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        $"QuotaGlass.StatusLine.{Guid.NewGuid():N}");
    var stateDirectory = Path.Combine(root, "state");
    var settingsPath = Path.Combine(root, ".claude", "settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
    var previousSettingsPath = Environment.GetEnvironmentVariable(
        "QUOTAGLASS_CLAUDE_SETTINGS_PATH");
    var previousStateDirectory = Environment.GetEnvironmentVariable(
        "QUOTAGLASS_STATE_DIRECTORY");
    var previousExecutablePath = Environment.GetEnvironmentVariable(
        "QUOTAGLASS_EXECUTABLE_PATH");

    try
    {
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_CLAUDE_SETTINGS_PATH",
            settingsPath);
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_STATE_DIRECTORY",
            stateDirectory);
        var executablePath = Path.Combine(root, "QuotaGlass.exe");
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_EXECUTABLE_PATH",
            executablePath);

        var bridgeConfigurationPath = Path.Combine(
            stateDirectory,
            "claude-statusline-bridge.json");

        File.WriteAllText(settingsPath, """{ "theme": "dark" }""");
        ClaudeStatusLineInstaller.EnsureInstalled();

        var settings = ReadJsonObject(settingsPath);
        Require(
            ReadCommand(settings)?.Contains(
                executablePath,
                StringComparison.Ordinal) == true,
            "status line 미설정 시 bridge 명령 등록");
        Require(
            ReadCommand(settings)?.Contains(
                ClaudeStatusLineInstaller.BridgeMarker,
                StringComparison.OrdinalIgnoreCase) == true,
            "status line bridge WinExe 내부 명령 등록");
        Require(
            ReadCommand(settings)?.Contains(
                "powershell",
                StringComparison.OrdinalIgnoreCase) == false,
            "status line bridge PowerShell 콘솔 제거");
        Require(
            settings["theme"]?.GetValue<string>() == "dark",
            "설치 시 기존 설정 보존");
        Require(
            !File.Exists(bridgeConfigurationPath),
            "신규 설치 시 원본 명령 없음");

        var installedCommand = ReadCommand(settings);
        ClaudeStatusLineInstaller.EnsureInstalled();
        Require(
            ReadCommand(ReadJsonObject(settingsPath)) == installedCommand,
            "재실행 시 bridge 중첩 없음");

        File.WriteAllText(
            settingsPath,
            """{ "statusLine": { "type": "command", "command": "my-status.exe" } }""");
        ClaudeStatusLineInstaller.EnsureInstalled();
        Require(
            ReadCommand(ReadJsonObject(settingsPath))?.Contains(
                ClaudeStatusLineInstaller.BridgeMarker,
                StringComparison.Ordinal) == true,
            "기존 status line 래핑");
        Require(
            ReadJsonObject(bridgeConfigurationPath)["originalCommand"]
                ?.GetValue<string>() == "my-status.exe",
            "기존 status line 명령 보존");

        File.Delete(bridgeConfigurationPath);
        const string payload =
            """
            {
              "model": { "display_name": "Opus 5" },
              "workspace": { "current_dir": "D:\\work\\QuotaGlass" },
              "rate_limits": {
                "five_hour": { "used_percentage": 12.5, "resets_at": 1784876400 }
              }
            }
            """;
        using var output = new StringWriter();
        var bridgeExitCode = ClaudeStatusLineBridge.Run(
            new StringReader(payload),
            output,
            stateDirectory);
        var statusLineOutput = output.ToString();
        var cachePath = Path.Combine(stateDirectory, "claude-rate-limits.json");
        Require(bridgeExitCode == 0, "WinExe bridge 정상 종료");
        Require(
            File.Exists(cachePath),
            "WinExe bridge가 rate limit 캐시 기록");
        Require(
            ClaudeRateLimitParser.Parse(File.ReadAllText(cachePath)).Count == 1,
            "캐시된 rate limit 파싱");
        Require(
            statusLineOutput.Contains("Opus 5", StringComparison.Ordinal) &&
            statusLineOutput.Contains("QuotaGlass", StringComparison.Ordinal),
            "원본 명령이 없을 때 기본 status line 출력");

        var bridgeStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "QuotaGlass.exe"),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        bridgeStartInfo.ArgumentList.Add(
            ClaudeStatusLineInstaller.BridgeMarker);
        bridgeStartInfo.Environment["QUOTAGLASS_STATE_DIRECTORY"] =
            stateDirectory;
        using var bridgeProcess = System.Diagnostics.Process.Start(
            bridgeStartInfo)
            ?? throw new InvalidOperationException(
                "WinExe status-line bridge를 시작하지 못했습니다.");
        var bridgeErrorTask = bridgeProcess.StandardError.ReadToEndAsync();
        bridgeProcess.StandardInput.Write(payload);
        bridgeProcess.StandardInput.Close();
        var processOutput = bridgeProcess.StandardOutput.ReadToEnd();
        bridgeProcess.WaitForExit();
        var processError = bridgeErrorTask.GetAwaiter().GetResult();
        Require(
            bridgeProcess.ExitCode == 0 &&
            processOutput.Contains("Opus 5", StringComparison.Ordinal),
            $"WinExe bridge 표준 입출력 연결{(
                string.IsNullOrWhiteSpace(processError)
                    ? string.Empty
                    : $" · {processError.Trim()}")}");

        var shellStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC")
                       ?? "cmd.exe",
            Arguments = $"/d /s /c \"\"{Path.Combine(AppContext.BaseDirectory, "QuotaGlass.exe")}\" {ClaudeStatusLineInstaller.BridgeMarker}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        shellStartInfo.Environment["QUOTAGLASS_STATE_DIRECTORY"] =
            stateDirectory;
        using var shellProcess = System.Diagnostics.Process.Start(
            shellStartInfo)
            ?? throw new InvalidOperationException(
                "쉘 경유 WinExe status-line bridge를 시작하지 못했습니다.");
        var shellErrorTask = shellProcess.StandardError.ReadToEndAsync();
        shellProcess.StandardInput.Write(payload);
        shellProcess.StandardInput.Close();
        var shellOutput = shellProcess.StandardOutput.ReadToEnd();
        shellProcess.WaitForExit();
        var shellError = shellErrorTask.GetAwaiter().GetResult();
        Require(
            shellProcess.ExitCode == 0 &&
            shellOutput.Contains("Opus 5", StringComparison.Ordinal),
            $"Claude 쉘 호출 방식의 bridge 표준 입출력 연결{(
                string.IsNullOrWhiteSpace(shellError)
                    ? string.Empty
                    : $" · {shellError.Trim()}")}");
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_CLAUDE_SETTINGS_PATH",
            previousSettingsPath);
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_STATE_DIRECTORY",
            previousStateDirectory);
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_EXECUTABLE_PATH",
            previousExecutablePath);
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리는 실패해도 테스트 결과에 영향을 주지 않는다.
        }
    }

    static JsonObject ReadJsonObject(string path) =>
        JsonNode.Parse(File.ReadAllText(path))
            ?.AsObject()
        ?? throw new InvalidOperationException($"JSON 객체 아님: {path}");

    static string? ReadCommand(JsonObject settings) =>
        settings["statusLine"]?["command"]?.GetValue<string>();

}

void RunCollapsedProviderStoreTests()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        $"QuotaGlass.CollapsedProviders.{Guid.NewGuid():N}");
    var settingsPath = Path.Combine(root, "collapsed-providers.json");

    try
    {
        CollapsedProviderStore.Save(
            settingsPath,
            ["cursor", "codex", "cursor", ""]);
        var restoredProviderIds = CollapsedProviderStore.Load(settingsPath);

        Require(
            restoredProviderIds.SetEquals(["codex", "cursor"]),
            "접은 에이전트 상태 저장 및 복원");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

void RunManagedCliTests()
{
    const string githubReleaseFixture =
        """
        {
          "tag_name": "v1.2.3",
          "assets": [
            {
              "name": "tool-win32-x64.zip",
              "browser_download_url": "https://github.com/example/tool.zip",
              "digest": "sha256:0123456789abcdef"
            }
          ]
        }
        """;
    var githubRelease = ManagedCliInstaller.ParseGitHubRelease(
        githubReleaseFixture,
        "tool-win32-x64.zip");
    Require(
        githubRelease.Version == "v1.2.3" && githubRelease.IsZip &&
        githubRelease.ExpectedHash == "0123456789abcdef",
        "GitHub 관리 CLI 릴리스 파싱");

    const string claudeManifestFixture =
        """
        {
          "platforms": {
            "win32-x64": { "checksum": "aabbcc" }
          }
        }
        """;
    var claudeRelease = ManagedCliInstaller.ParseClaudeRelease(
        "2.3.4",
        claudeManifestFixture,
        "win32-x64",
        "https://downloads.example/releases");
    Require(
        claudeRelease.DownloadUrl.AbsoluteUri.EndsWith(
            "/2.3.4/win32-x64/claude.exe",
            StringComparison.Ordinal) &&
        claudeRelease.ExpectedHash == "aabbcc",
        "Claude 관리 CLI 릴리스 파싱");

    const string antigravityManifestFixture =
        """
        {
          "version": "1.4.0",
          "url": "https://downloads.example/agy.exe",
          "sha512": "ddeeff"
        }
        """;
    var antigravityRelease = ManagedCliInstaller.ParseAntigravityRelease(
        antigravityManifestFixture);
    Require(
        antigravityRelease.Version == "1.4.0" &&
        antigravityRelease.ExpectedHash == "ddeeff",
        "Antigravity 관리 CLI 릴리스 파싱");

    var root = Path.Combine(
        Path.GetTempPath(),
        $"QuotaGlass.ManagedCli.{Guid.NewGuid():N}");
    try
    {
        var executablePath = Path.Combine(
            root,
            "codex",
            "v1",
            "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, [0x4d, 0x5a]);
        ManagedCliStore.Activate(
            root,
            "codex",
            "v1",
            executablePath);
        Require(
            ManagedCliStore.FindExecutable(root, "codex") == executablePath,
            "관리 CLI 활성 버전 복원");

        var previousToolsDirectory = Environment.GetEnvironmentVariable(
            "QUOTAGLASS_TOOLS_DIRECTORY");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "QUOTAGLASS_TOOLS_DIRECTORY",
                root);
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var managedCodex = new AgentInstallationDetector()
                .Detect()
                .Single(installation => installation.ProviderId == "codex");
            Require(
                managedCodex.ExecutablePath == executablePath,
                "시스템 PATH 부재 시 관리 CLI fallback");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "QUOTAGLASS_TOOLS_DIRECTORY",
                previousToolsDirectory);
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }

        var archivePath = Path.Combine(root, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("../escape.exe");
        }

        var rejectedUnsafeArchive = false;
        try
        {
            ManagedCliInstaller.ExtractZipSafely(
                archivePath,
                Path.Combine(root, "extract"));
        }
        catch (InvalidDataException)
        {
            rejectedUnsafeArchive = true;
        }

        Require(rejectedUnsafeArchive, "관리 CLI Zip Slip 차단");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static bool Approximately(double left, double right) =>
    Math.Abs(left - right) < 0.000_001;

static void Require(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException($"실패: {label}");
    }
}
