using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class CodexAppServerUsageProvider(
    AgentInstallation installation) :
    IUsageProvider,
    IResetCreditRedeemer
{
    // 마지막으로 사용량 조회에 성공한 CODEX_HOME. 티켓 사용도 같은 계정에서 한다.
    private string? _activeCodexHome;
    private bool _hasActiveCodexHome;

    private const int MaxFetchAttempts = 3;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750)
    ];

    public string ProviderId => installation.ProviderId;
    public string DisplayName => installation.DisplayName;
    public string IconText => installation.IconText;
    public string AccountLabel => installation.AccountLabel;

    public async Task<UsageSnapshot> FetchAsync(
        CancellationToken cancellationToken)
    {
        if (installation.ExecutablePath is null)
        {
            throw new InvalidOperationException("Codex 실행 파일을 찾을 수 없습니다.");
        }

        Exception? lastException = null;
        foreach (var codexHome in GetCodexHomeCandidates())
        {
            try
            {
                var snapshot = await FetchWithRetriesAsync(
                    installation.ExecutablePath,
                    codexHome,
                    cancellationToken);
                _activeCodexHome = codexHome;
                _hasActiveCodexHome = true;
                return snapshot;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastException = exception;
            }
        }

        throw new InvalidOperationException(
            "Codex 사용량 조회를 완료하지 못했습니다.",
            lastException);
    }

    public async Task<ResetRedeemResult> RedeemResetCreditAsync(
        ResetCreditRedeemOption option,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (installation.ExecutablePath is null || !_hasActiveCodexHome)
        {
            return new ResetRedeemResult(
                false,
                "Codex 계정 정보를 아직 읽지 못했습니다. 새로고침 후 다시 시도해 주세요.");
        }

        using var process = StartAppServer(
            installation.ExecutablePath,
            _activeCodexHome);
        try
        {
            await WriteRequestAsync(
                process,
                BuildInitializeRequest(),
                cancellationToken);
            await ReadResultAsync(process, 1, cancellationToken);
            await WriteNotificationAsync(
                process,
                BuildInitializedNotification(),
                cancellationToken);
            await WriteRequestAsync(
                process,
                BuildConsumeResetCreditRequest(option.GrantId, requestId),
                cancellationToken);
            var result = await ReadResultAsync(process, 4, cancellationToken);
            return DescribeConsumeOutcome(result);
        }
        catch (Exception exception) when (
            exception is IOException or
                InvalidOperationException or
                JsonException or
                Win32Exception)
        {
            return new ResetRedeemResult(
                false,
                "리셋 티켓 사용에 실패했습니다. 사용되지 않았을 수 있으니 " +
                $"새로고침 후 확인해 주세요. ({exception.Message})");
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    internal static string BuildConsumeResetCreditRequest(
        string creditId,
        string idempotencyKey) =>
        JsonSerializer.Serialize(
            new
            {
                id = 4,
                method = "account/rateLimitResetCredit/consume",
                @params = new { creditId, idempotencyKey }
            });

    internal static ResetRedeemResult DescribeConsumeOutcome(
        JsonElement result) =>
        (result.TryGetProperty("outcome", out var outcome) &&
         outcome.ValueKind == JsonValueKind.String
            ? outcome.GetString()
            : null) switch
        {
            "reset" => new ResetRedeemResult(
                true,
                "리셋 티켓을 사용했습니다."),
            "nothingToReset" => new ResetRedeemResult(
                false,
                "지금은 초기화할 사용량 한도가 없어 사용되지 않았습니다."),
            "noCredit" => new ResetRedeemResult(
                false,
                "사용할 수 있는 리셋 티켓이 없습니다."),
            "alreadyRedeemed" => new ResetRedeemResult(
                false,
                "이미 사용된 요청입니다. 사용량을 새로고침합니다."),
            _ => new ResetRedeemResult(
                false,
                "알 수 없는 응답입니다. 새로고침 후 확인해 주세요.")
        };

    private async Task<UsageSnapshot> FetchWithRetriesAsync(
        string executablePath,
        string? codexHome,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < MaxFetchAttempts; attempt++)
        {
            try
            {
                return await FetchOnceAsync(
                    executablePath,
                    codexHome,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                attempt + 1 < MaxFetchAttempts &&
                IsRetryable(exception))
            {
                lastException = exception;
                await Task.Delay(
                    RetryDelays[attempt],
                    cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "Codex 사용량 조회를 완료하지 못했습니다.",
            lastException);
    }

    private async Task<UsageSnapshot> FetchOnceAsync(
        string executablePath,
        string? codexHome,
        CancellationToken cancellationToken)
    {
        using var process = StartAppServer(executablePath, codexHome);

        try
        {
            await WriteRequestAsync(
                process,
                BuildInitializeRequest(),
                cancellationToken);
            await ReadResultAsync(process, 1, cancellationToken);

            await WriteNotificationAsync(
                process,
                BuildInitializedNotification(),
                cancellationToken);

            await WriteRequestAsync(
                process,
                BuildAccountReadRequest(),
                cancellationToken);
            await ReadResultAsync(process, 2, cancellationToken);

            await WriteRequestAsync(
                process,
                BuildRateLimitsReadRequest(),
                cancellationToken);
            var result = await ReadResultAsync(process, 3, cancellationToken);

            return ParseSnapshot(result);
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    internal static string BuildInitializeRequest() =>
        """
        {"id":1,"method":"initialize","params":{"clientInfo":{"name":"quota-glass","version":"0.1.0"},"capabilities":{"experimentalApi":true}}}
        """;

    internal static string BuildInitializedNotification() =>
        """{"method":"initialized","params":{}}""";

    internal static string BuildAccountReadRequest() =>
        """{"id":2,"method":"account/read","params":{"refreshToken":true}}""";

    internal static string BuildRateLimitsReadRequest() =>
        """{"id":3,"method":"account/rateLimits/read","params":{}}""";

    internal static IReadOnlyList<string?> GetCodexHomeCandidates()
    {
        var candidates = new List<string?>();
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        AddCredentialHome(
            candidates,
            Path.Combine(localAppData, "OpenAI", "Codex"));
        AddCredentialHome(
            candidates,
            Path.Combine(roamingAppData, "OpenAI", "Codex"));

        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            AddCandidate(candidates, configuredHome);
        }

        AddCredentialHome(
            candidates,
            Path.Combine(userProfile, ".codex"));

        // Keep the original Codex environment fallback for installations that
        // use a keychain or another auth store without auth.json.
        if (candidates.Count == 0)
        {
            candidates.Add(null);
        }

        return candidates;
    }

    private static void AddCredentialHome(
        ICollection<string?> candidates,
        string home)
    {
        if (File.Exists(Path.Combine(home, "auth.json")))
        {
            AddCandidate(candidates, home);
        }
    }

    private static void AddCandidate(
        ICollection<string?> candidates,
        string home)
    {
        if (candidates.Any(candidate =>
                string.Equals(candidate, home, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(home);
    }

    private static Process StartAppServer(
        string executablePath,
        string? codexHome)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            startInfo.Environment["CODEX_HOME"] = codexHome;
        }
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Codex app-server를 시작하지 못했습니다.");
    }

    private static async Task WriteRequestAsync(
        Process process,
        string json,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(
            json.AsMemory(),
            cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task WriteNotificationAsync(
        Process process,
        string json,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(
            json.AsMemory(),
            cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonElement> ReadResultAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(
                cancellationToken);
            if (line is null)
            {
                var error = await process.StandardError.ReadToEndAsync(
                    cancellationToken);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "Codex app-server가 응답 없이 종료되었습니다."
                        : error.Trim());
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number ||
                id.GetInt32() != expectedId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                throw new InvalidOperationException(errorElement.ToString());
            }

            return root.GetProperty("result").Clone();
        }
    }

    private static bool IsRetryable(Exception exception) =>
        exception is IOException or
            InvalidOperationException or
            JsonException or
            Win32Exception;

    private UsageSnapshot ParseSnapshot(JsonElement result)
    {
        var rateLimits = SelectRateLimits(result);
        var meters = new List<UsageMeter>();
        var now = DateTimeOffset.Now;

        AddWindow(meters, rateLimits, "primary", now);
        AddWindow(meters, rateLimits, "secondary", now);
        AddIndividualLimit(meters, rateLimits, now);

        if (meters.Count == 0)
        {
            throw new InvalidOperationException("Codex가 사용량 meter를 반환하지 않았습니다.");
        }

        var planType = rateLimits.TryGetProperty("planType", out var plan) &&
                       plan.ValueKind == JsonValueKind.String
            ? plan.GetString()
            : null;
        var accountLabel = planType == "team"
            ? $"팀 · {DescribeWindows(meters)}"
            : $"{planType ?? "개인"} · {DescribeWindows(meters)}";

        return new UsageSnapshot(
            ProviderId,
            DisplayName,
            IconText,
            accountLabel,
            meters,
            now,
            "Codex app-server",
            ResetCredits: CodexRateLimitResetCreditParser.Parse(result));
    }

    private static JsonElement SelectRateLimits(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object &&
            byId.TryGetProperty("codex", out var codex))
        {
            return codex;
        }

        return result.GetProperty("rateLimits");
    }

    private static void AddWindow(
        ICollection<UsageMeter> meters,
        JsonElement rateLimits,
        string propertyName,
        DateTimeOffset now)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var usedElement))
        {
            return;
        }

        var durationMinutes =
            window.TryGetProperty("windowDurationMins", out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number
                ? durationElement.GetInt64()
                : 0;
        var resetsAt =
            window.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(resetElement.GetInt64())
                : now;
        var duration = durationMinutes > 0
            ? TimeSpan.FromMinutes(durationMinutes)
            : TimeSpan.FromDays(30);
        var label = DescribeWindow(duration);
        var used = Math.Clamp(usedElement.GetInt32(), 0, 100);

        meters.Add(
            new UsageMeter(
                $"{propertyName}-{durationMinutes}",
                label,
                100 - used,
                100,
                "percent",
                resetsAt - duration,
                resetsAt));
    }

    private static void AddIndividualLimit(
        ICollection<UsageMeter> meters,
        JsonElement rateLimits,
        DateTimeOffset now)
    {
        if (!rateLimits.TryGetProperty("individualLimit", out var limit) ||
            limit.ValueKind != JsonValueKind.Object ||
            !limit.TryGetProperty("remainingPercent", out var remainingElement))
        {
            return;
        }

        var resetsAt =
            limit.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(resetElement.GetInt64())
                : now + TimeSpan.FromDays(30);

        meters.Add(
            new UsageMeter(
                "individual-monthly",
                "개인 월간",
                Math.Clamp(remainingElement.GetInt32(), 0, 100),
                100,
                "percent",
                resetsAt - TimeSpan.FromDays(30),
                resetsAt));
    }

    private static string DescribeWindows(IReadOnlyCollection<UsageMeter> meters) =>
        string.Join("/", meters.Select(meter => meter.Label).Distinct());

    private static string DescribeWindow(TimeSpan duration)
    {
        if (duration <= TimeSpan.FromHours(6))
        {
            return $"{Math.Round(duration.TotalHours):0}시간";
        }

        if (duration <= TimeSpan.FromDays(8))
        {
            return "주간";
        }

        if (duration >= TimeSpan.FromDays(20) &&
            duration <= TimeSpan.FromDays(32))
        {
            return "월간";
        }

        return $"{Math.Round(duration.TotalDays):0}일";
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
