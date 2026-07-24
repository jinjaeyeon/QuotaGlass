using System.Diagnostics;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class CodexAppServerUsageProvider(
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
            throw new InvalidOperationException("Codex 실행 파일을 찾을 수 없습니다.");
        }

        using var process = StartAppServer(installation.ExecutablePath);

        try
        {
            await WriteRequestAsync(
                process,
                """
                {"id":1,"method":"initialize","params":{"clientInfo":{"name":"quota-glass","version":"0.1.0"},"capabilities":{"experimentalApi":true}}}
                """,
                cancellationToken);
            await ReadResultAsync(process, 1, cancellationToken);

            await WriteRequestAsync(
                process,
                """{"id":2,"method":"account/rateLimits/read","params":{}}""",
                cancellationToken);
            var result = await ReadResultAsync(process, 2, cancellationToken);

            return ParseSnapshot(result);
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    private static Process StartAppServer(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
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
            "Codex app-server");
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
