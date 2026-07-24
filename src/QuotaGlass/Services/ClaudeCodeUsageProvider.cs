using System.Diagnostics;
using System.IO;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class ClaudeCodeUsageProvider(
    AgentInstallation installation) : IUsageProvider
{
    public string ProviderId => installation.ProviderId;
    public string DisplayName => installation.DisplayName;
    public string IconText => installation.IconText;
    public string AccountLabel => installation.AccountLabel;

    public async Task<UsageSnapshot> FetchAsync(
        CancellationToken cancellationToken)
    {
        if (installation.ExecutablePath is null)
        {
            throw new InvalidOperationException(
                "Claude Code 실행 파일을 찾을 수 없습니다.");
        }

        var sidecar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuotaGlass",
            "claude-rate-limits.json");
        var now = DateTimeOffset.Now;
        IReadOnlyList<UsageMeter> cachedMeters = [];
        string? cachedWorkingDirectory = null;
        if (File.Exists(sidecar))
        {
            var json = await File.ReadAllTextAsync(sidecar, cancellationToken);
            cachedMeters = ClaudeRateLimitParser.Parse(json);
            cachedWorkingDirectory = ReadCachedWorkingDirectory(json);
            if (cachedMeters.Count > 0 &&
                cachedMeters.All(meter => meter.ResetsAt > now))
            {
                return new UsageSnapshot(
                    ProviderId,
                    DisplayName,
                    IconText,
                    "구독 · 5시간/주간 · status line",
                    cachedMeters,
                    File.GetLastWriteTimeUtc(sidecar),
                    "Claude Code status-line cache");
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installation.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("status");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Claude Code 인증 상태 확인을 시작하지 못했습니다.");
        var output = await process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

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
            var usageOutput = await ReadUsageScreenAsync(
                installation.ExecutablePath,
                cachedWorkingDirectory,
                cancellationToken);
            var meters = ClaudeUsageScreenParser.Parse(
                usageOutput,
                now);
            meters = ReconcileExpiredMeters(
                meters,
                cachedMeters,
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
                continue;
            }

            var duration = cached.ResetsAt - cached.WindowStart;
            if (duration <= TimeSpan.Zero)
            {
                continue;
            }

            result[cached.Id] = new UsageMeter(
                cached.Id,
                cached.Label,
                100,
                100,
                cached.Unit,
                now,
                now + duration,
                true);
        }

        return result.Values.ToArray();
    }

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
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add("--ax-screen-reader");
        startInfo.ArgumentList.Add("--safe-mode");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Claude Code /usage 세션을 시작하지 못했습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(
            cancellationToken);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await process.StandardInput.WriteAsync("/usage");
            await process.StandardInput.WriteAsync("\r");
            await process.StandardInput.FlushAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);

            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
        }

        var output = await outputTask;
        _ = await errorTask;
        return output;
    }
}
