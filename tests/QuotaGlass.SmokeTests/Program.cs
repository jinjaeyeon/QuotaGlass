using System.IO;
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

var refreshService = new UsageRefreshService(MockUsageProvider.CreateDefaults());
var snapshots = await refreshService.RefreshAsync(CancellationToken.None);
var streamedResults = new List<UsageRefreshResult>();
await foreach (var result in refreshService.RefreshAsCompletedAsync(
                   CancellationToken.None))
{
    streamedResults.Add(result);
}

Require(snapshots.Count == 5, "기본 provider 개수");
Require(streamedResults.Count == 5, "provider 완료 순서 갱신 개수");
Require(
    streamedResults.Select(result => result.ProviderIndex).Distinct().Count() == 5,
    "provider 완료 순서 갱신 인덱스");
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
    reconciledClaudeMeters.Any(item =>
        item.Label == "5시간" &&
        item.IsReset &&
        Approximately(item.RemainingRatio, 1)),
    "Claude 초기화 완료 meter를 100%로 전환");

RunStatusLineInstallerTests();

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
}

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

    try
    {
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_CLAUDE_SETTINGS_PATH",
            settingsPath);
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_STATE_DIRECTORY",
            stateDirectory);

        var bridgeScriptPath = Path.Combine(
            stateDirectory,
            "claude-statusline-bridge.ps1");
        var bridgeConfigurationPath = Path.Combine(
            stateDirectory,
            "claude-statusline-bridge.json");

        File.WriteAllText(settingsPath, """{ "theme": "dark" }""");
        ClaudeStatusLineInstaller.EnsureInstalled();

        var settings = ReadJsonObject(settingsPath);
        Require(
            File.Exists(bridgeScriptPath),
            "status line 미설정 시 bridge 스크립트 설치");
        Require(
            ReadCommand(settings)?.Contains(
                bridgeScriptPath,
                StringComparison.Ordinal) == true,
            "status line 미설정 시 bridge 명령 등록");
        Require(
            ReadCommand(settings)?.Contains(
                "-WindowStyle Hidden",
                StringComparison.OrdinalIgnoreCase) == true,
            "status line bridge 터미널 창 숨김");
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
                bridgeScriptPath,
                StringComparison.Ordinal) == true,
            "기존 status line 래핑");
        Require(
            ReadJsonObject(bridgeConfigurationPath)["originalCommand"]
                ?.GetValue<string>() == "my-status.exe",
            "기존 status line 명령 보존");

        File.Delete(bridgeConfigurationPath);
        var statusLineOutput = RunBridgeScript(
            bridgeScriptPath,
            stateDirectory,
            """
            {
              "model": { "display_name": "Opus 5" },
              "workspace": { "current_dir": "D:\\work\\QuotaGlass" },
              "rate_limits": {
                "five_hour": { "used_percentage": 12.5, "resets_at": 1784876400 }
              }
            }
            """);
        var cachePath = Path.Combine(stateDirectory, "claude-rate-limits.json");
        Require(
            File.Exists(cachePath),
            "bridge 스크립트가 rate limit 캐시 기록");
        Require(
            ClaudeRateLimitParser.Parse(File.ReadAllText(cachePath)).Count == 1,
            "캐시된 rate limit 파싱");
        Require(
            statusLineOutput.Contains("Opus 5", StringComparison.Ordinal) &&
            statusLineOutput.Contains("QuotaGlass", StringComparison.Ordinal),
            "원본 명령이 없을 때 기본 status line 출력");
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_CLAUDE_SETTINGS_PATH",
            previousSettingsPath);
        Environment.SetEnvironmentVariable(
            "QUOTAGLASS_STATE_DIRECTORY",
            previousStateDirectory);
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

    static string RunBridgeScript(
        string scriptPath,
        string stateDirectory,
        string payload)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["QUOTAGLASS_STATE_DIRECTORY"] = stateDirectory;

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "bridge 스크립트를 실행하지 못했습니다.");
        process.StandardInput.Write(payload);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
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
