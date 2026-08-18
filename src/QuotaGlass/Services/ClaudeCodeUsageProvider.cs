using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class ClaudeCodeUsageProvider : IUsageProvider
{
    private static readonly TimeSpan UsageStartupTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UsageMinimumWarmup =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UsageScreenTimeout =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusLineCacheMaxAge =
        TimeSpan.FromMinutes(10);

    private readonly AgentInstallation installation;
    private readonly ClaudeUsageApiClient usageApiClient;

    public ClaudeCodeUsageProvider(AgentInstallation installation)
        : this(installation, new ClaudeUsageApiClient())
    {
    }

    internal ClaudeCodeUsageProvider(
        AgentInstallation installation,
        ClaudeUsageApiClient usageApiClient)
    {
        this.installation = installation;
        this.usageApiClient = usageApiClient;
    }

    public string ProviderId => installation.ProviderId;
    public string DisplayName => installation.DisplayName;
    public string IconText => installation.IconText;
    public string AccountLabel => installation.AccountLabel;

    public async Task<UsageSnapshot> FetchAsync(
        CancellationToken cancellationToken)
    {
        var sidecar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuotaGlass",
            "claude-rate-limits.json");
        var now = DateTimeOffset.Now;
        IReadOnlyList<UsageMeter> cachedMeters = [];
        string? cachedWorkingDirectory = null;
        var cacheObservedAt = DateTimeOffset.MinValue;
        var isStatusLineCacheFresh = false;
        if (File.Exists(sidecar))
        {
            var json = await File.ReadAllTextAsync(sidecar, cancellationToken);
            try
            {
                cachedMeters = ClaudeRateLimitParser.Parse(json);
            }
            catch (JsonException)
            {
                // A partial/old sidecar must not prevent the direct API path.
                cachedMeters = [];
            }

            cachedWorkingDirectory = ReadCachedWorkingDirectory(json);
            cacheObservedAt = File.GetLastWriteTimeUtc(sidecar);
            isStatusLineCacheFresh = IsStatusLineCacheFresh(
                cacheObservedAt,
                now);
        }

        // The API chain is intentionally attempted before launching Claude
        // Code. It tries Claude Desktop's protected OAuth cache first, then
        // the CLI credential file inside ClaudeUsageApiClient.
        var apiMeters = await usageApiClient.FetchAsync(
            installation.Version,
            cancellationToken);
        if (apiMeters.Count > 0)
        {
            return new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                "Claude · 5시간/주간",
                apiMeters,
                DateTimeOffset.Now,
                "Claude Desktop/CLI usage API");
        }

        if (installation.ExecutablePath is null)
        {
            return new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                "Claude Desktop",
                [],
                DateTimeOffset.Now,
                "Claude Desktop auth",
                UsageSnapshotState.AdapterPending,
                "Claude Desktop 인증은 확인했지만 usage API에 연결되지 않음");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installation.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("status");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Claude Code 인증 상태 확인을 시작하지 못했습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        string output;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            output = await outputTask;
            _ = await errorTask;
        }
        finally
        {
            await StopProcessAsync(process);
            await DrainProcessOutputAsync(outputTask, errorTask);
        }

        using var document = JsonDocument.Parse(output);
        var loggedIn =
            document.RootElement.TryGetProperty("loggedIn", out var loggedInValue) &&
            loggedInValue.ValueKind == JsonValueKind.True;
        var subscriptionType =
            document.RootElement.TryGetProperty(
                "subscriptionType",
                out var subscriptionTypeValue)
                ? subscriptionTypeValue.GetString()
                : null;
        var organization =
            document.RootElement.TryGetProperty("orgName", out var organizationValue)
                ? organizationValue.GetString()
                : null;
        var isSubscription =
            loggedIn &&
            !string.IsNullOrWhiteSpace(subscriptionType);
        var accountLabel = isSubscription
            ? string.Join(
                " · ",
                new[] { subscriptionType, organization, "5시간/주간" }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
            : loggedIn
                ? "Claude 인증됨"
                : "구독 인증 없음";

        if (isSubscription)
        {
            if (isStatusLineCacheFresh &&
                cachedMeters.Count > 0 &&
                cachedMeters.All(meter => meter.ResetsAt > now))
            {
                return new UsageSnapshot(
                    ProviderId,
                    DisplayName,
                    IconText,
                    accountLabel,
                    cachedMeters,
                    cacheObservedAt,
                    "Claude Code status-line cache");
            }

            var usageOutput = await ReadUsageScreenAsync(
                installation.ExecutablePath,
                cachedWorkingDirectory,
                now,
                cancellationToken);
            var meters = ClaudeUsageScreenParser.Parse(
                usageOutput,
                now);
            meters = ReconcileExpiredMeters(
                meters,
                isStatusLineCacheFresh ? cachedMeters : [],
                now);
            if (meters.Count > 0)
            {
                return new UsageSnapshot(
                    ProviderId,
                    DisplayName,
                    IconText,
                    accountLabel,
                    meters,
                    DateTimeOffset.Now,
                    "Claude Code /usage");
            }
        }
        return new UsageSnapshot(
            ProviderId,
            DisplayName,
            IconText,
            accountLabel,
            [],
            DateTimeOffset.Now,
            "Claude Code auth status",
            UsageSnapshotState.AdapterPending,
            isSubscription
                ? "구독 로그인 확인 · Claude Code를 사용하면 사용량이 동기화됨"
                : loggedIn
                    ? "인증됨 · 구독 제한 정보 없음"
                    : "구독 인증 없음 · 사용량 확인 불가");
    }

    public static IReadOnlyList<UsageMeter> ReconcileExpiredMeters(
        IReadOnlyList<UsageMeter> freshMeters,
        IReadOnlyList<UsageMeter> cachedMeters,
        DateTimeOffset now)
    {
        var result = freshMeters
            .Where(meter => meter.ResetsAt > now)
            .ToDictionary(meter => meter.Id, StringComparer.Ordinal);

        foreach (var cached in cachedMeters)
        {
            if (result.ContainsKey(cached.Id) ||
                result.Values.Any(meter =>
                    string.Equals(
                        meter.Label,
                        cached.Label,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (cached.ResetsAt > now)
            {
                result[cached.Id] = cached;
            }
        }

        return result.Values.ToArray();
    }

    public static bool IsStatusLineCacheFresh(
        DateTimeOffset cacheObservedAt,
        DateTimeOffset now) =>
        cacheObservedAt <= now &&
        now - cacheObservedAt <= StatusLineCacheMaxAge;

    public static string? ReadCachedWorkingDirectory(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(
                    "cwd",
                    out var cwdElement) ||
                cwdElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var path = cwdElement.GetString();
            return !string.IsNullOrWhiteSpace(path) &&
                   Directory.Exists(path)
                ? path
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadUsageScreenAsync(
        string executablePath,
        string? cachedWorkingDirectory,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "conhost.exe",
            WorkingDirectory = cachedWorkingDirectory ??
                               Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["DISABLE_AUTOUPDATER"] = "1";
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add("--ax-screen-reader");
        startInfo.ArgumentList.Add("--safe-mode");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Claude Code /usage 세션을 시작하지 못했습니다.");
        var output = new StringBuilder();
        var outputStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var usageReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var outputTask = ReadUsageOutputAsync(
            process.StandardOutput,
            output,
            outputStarted,
            usageReady,
            observedAt,
            cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await Task.WhenAll(
                Task.Delay(UsageMinimumWarmup, cancellationToken),
                Task.WhenAny(
                    outputStarted.Task,
                    Task.Delay(UsageStartupTimeout, cancellationToken)));
            await process.StandardInput.WriteAsync("/usage");
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync(cancellationToken);

            _ = await Task.WhenAny(
                usageReady.Task,
                Task.Delay(UsageScreenTimeout, cancellationToken));

            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            await StopProcessAsync(process);
            await DrainProcessOutputAsync(outputTask, errorTask);
        }

        await outputTask;
        _ = await errorTask;
        return output.ToString();
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process may have exited while the process tree was killed.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The process was already disposed or exited before waiting.
        }
    }

    private static async Task DrainProcessOutputAsync(
        Task outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (Exception) when (
            outputTask.IsCanceled ||
            outputTask.IsFaulted ||
            errorTask.IsCanceled ||
            errorTask.IsFaulted)
        {
            // The original process/cancellation exception is reported by the
            // caller after the pipes have been drained.
        }
    }

    private static async Task ReadUsageOutputAsync(
        StreamReader reader,
        StringBuilder output,
        TaskCompletionSource outputStarted,
        TaskCompletionSource usageReady,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (count == 0)
            {
                return;
            }

            output.Append(buffer, 0, count);
            outputStarted.TrySetResult();
            if (HasFinishedUsageRefresh(output.ToString(), observedAt))
            {
                usageReady.TrySetResult();
            }
        }
    }

    internal static bool HasCompleteUsageScreen(
        string terminalOutput,
        DateTimeOffset observedAt) =>
        ClaudeUsageScreenParser.Parse(terminalOutput, observedAt).Count >= 2;

    internal static bool HasFinishedUsageRefresh(
        string terminalOutput,
        DateTimeOffset observedAt)
    {
        if (!HasCompleteUsageScreen(terminalOutput, observedAt))
        {
            return false;
        }

        var text = TerminalText.StripControlSequences(terminalOutput);
        var refreshIndex = text.LastIndexOf(
            "Refreshing",
            StringComparison.OrdinalIgnoreCase);
        if (refreshIndex < 0)
        {
            return false;
        }

        if (text.Contains("could not refresh", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rate limited", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var refreshedOutput = text[(refreshIndex + "Refreshing".Length)..];
        return HasCompleteUsageScreen(refreshedOutput, observedAt);
    }
}
