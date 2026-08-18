using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class AntigravityUsageProvider(
    AgentInstallation installation) : IUsageProvider
{
    private const string QuotaSummaryPath =
        "/exa.language_server_pb.LanguageServerService/" +
        "RetrieveUserQuotaSummary";

    private static readonly HttpClient LocalClient = new(
        new HttpClientHandler
        {
            UseProxy = false
        })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly HttpClient DesktopClient = new(
        new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback =
                (_, _, _, _) => true
        })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public string ProviderId => installation.ProviderId;
    public string DisplayName => installation.DisplayName;
    public string IconText => installation.IconText;
    public string AccountLabel => installation.AccountLabel;

    public async Task<UsageSnapshot> FetchAsync(
        CancellationToken cancellationToken)
    {
        var desktopJson = await TryReadDesktopQuotaSummaryAsync(
            installation.UsageStatePath,
            cancellationToken);
        if (desktopJson is not null)
        {
            return CreateSnapshot(
                desktopJson,
                "Antigravity Desktop local RPC");
        }

        var cliPath = installation.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(cliPath) && File.Exists(cliPath))
        {
            try
            {
                var json = await ReadQuotaSummaryAsync(
                    cliPath,
                    cancellationToken);
                return CreateSnapshot(
                    json,
                    "Antigravity CLI RetrieveUserQuotaSummary");
            }
            catch (Exception exception) when (
                exception is IOException or
                    JsonException or
                    InvalidOperationException or
                    HttpRequestException or
                    TaskCanceledException)
            {
                // A stale or initializing CLI must not hide a usable cache.
            }
        }

        var cachedJson = await TryReadCachedQuotaSummaryAsync(
            cancellationToken);
        if (cachedJson is not null)
        {
            return CreateSnapshot(
                cachedJson,
                "Antigravity quota cache");
        }

        return new UsageSnapshot(
            ProviderId,
            DisplayName,
            IconText,
            AccountLabel,
            [],
            DateTimeOffset.Now,
            "Antigravity 설치 상태",
            UsageSnapshotState.AdapterPending,
            cliPath is null
                ? "Antigravity IDE quota RPC에 연결하지 못했습니다."
                : "Antigravity quota 응답을 확인하지 못했습니다.");
    }

    private UsageSnapshot CreateSnapshot(
        string json,
        string source)
    {
        var meters = AntigravityQuotaParser.Parse(json);
        return meters.Count > 0
            ? new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                "Google AI · 모델 그룹별",
                meters,
                DateTimeOffset.Now,
                source)
            : new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                AccountLabel,
                [],
                DateTimeOffset.Now,
                source,
                UsageSnapshotState.AdapterPending,
                "로그인됨 · quota 응답에 사용 가능한 bucket이 없음");
    }

    private static async Task<string?> TryReadDesktopQuotaSummaryAsync(
        string? statePath,
        CancellationToken cancellationToken)
    {
        var mainLogPath = ResolveDesktopMainLogPath(statePath);
        var languageServerLogPath = Path.Combine(
            Path.GetDirectoryName(mainLogPath) ?? string.Empty,
            "language_server.log");
        var endpoint = TryReadDesktopEndpoint(
            mainLogPath,
            languageServerLogPath);
        if (endpoint is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://127.0.0.1:{endpoint.Value.Port}{QuotaSummaryPath}");
            request.Headers.TryAddWithoutValidation(
                "X-Codeium-Csrf-Token",
                endpoint.Value.CsrfToken);
            request.Headers.TryAddWithoutValidation(
                "Connect-Protocol-Version",
                "1");
            request.Content = new StringContent(
                "{}",
                Encoding.UTF8,
                "application/json");

            using var response = await DesktopClient.SendAsync(
                request,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(
                timeout.Token);
            return AntigravityQuotaParser.Parse(content).Count > 0
                ? content
                : null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                JsonException or
                TaskCanceledException or
                InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<string?> TryReadCachedQuotaSummaryAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "QuotaGlass",
            "antigravity-last-response.json");
        if (!File.Exists(path) ||
            DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >
                TimeSpan.FromHours(1))
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(
                path,
                cancellationToken);
            return AntigravityQuotaParser.Parse(content).Count > 0
                ? content
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or
                JsonException or
                UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolveDesktopMainLogPath(string? statePath)
    {
        if (!string.IsNullOrWhiteSpace(statePath))
        {
            return statePath;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Antigravity",
            "logs",
            "main.log");
    }

    private static DesktopEndpoint? TryReadDesktopEndpoint(
        string mainLogPath,
        string languageServerLogPath)
    {
        try
        {
            if (!File.Exists(mainLogPath) ||
                !File.Exists(languageServerLogPath))
            {
                return null;
            }

            var mainLog = ReadSharedText(mainLogPath);
            var languageLog = ReadSharedText(languageServerLogPath);
            var csrfMatches = Regex.Matches(
                mainLog,
                @"--csrf_token\s+(?<token>[0-9a-f-]{20,})",
                RegexOptions.IgnoreCase);
            var portMatches = Regex.Matches(
                languageLog,
                @"listening on random port at (?<port>\d+) for HTTPS",
                RegexOptions.IgnoreCase);
            if (csrfMatches.Count == 0 || portMatches.Count == 0)
            {
                return null;
            }

            var token = csrfMatches[^1].Groups["token"].Value;
            return int.TryParse(
                    portMatches[^1].Groups["port"].Value,
                    out var port) &&
                port is > 0 and <= 65535 &&
                !string.IsNullOrWhiteSpace(token)
                ? new DesktopEndpoint(port, token)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private readonly record struct DesktopEndpoint(
        int Port,
        string CsrfToken);

    private static async Task<string> ReadQuotaSummaryAsync(
        string cliPath,
        CancellationToken cancellationToken)
    {
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "QuotaGlass");
        Directory.CreateDirectory(stateDirectory);
        var logPath = Path.Combine(
            stateDirectory,
            $"antigravity-{Guid.NewGuid():N}.log");

        var startInfo = new ProcessStartInfo
        {
            FileName = "conhost.exe",
            WorkingDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add(cliPath);
        startInfo.ArgumentList.Add("--log-file");
        startInfo.ArgumentList.Add(logPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Antigravity quota reader를 시작하지 못했습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(
            cancellationToken);

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
            Exception? lastError = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        "Antigravity quota reader가 조기에 종료되었습니다.");
                }

                var port = TryReadHttpPort(logPath);
                if (port is not null)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(
                            HttpMethod.Post,
                            $"http://127.0.0.1:{port}{QuotaSummaryPath}");
                        request.Headers.Add("Connect-Protocol-Version", "1");
                        request.Content = new StringContent(
                            "{}",
                            Encoding.UTF8,
                            "application/json");
                        using var response = await LocalClient.SendAsync(
                            request,
                            cancellationToken);
                        var content = await response.Content.ReadAsStringAsync(
                            cancellationToken);
                        if (response.IsSuccessStatusCode)
                        {
                            await File.WriteAllTextAsync(
                                Path.Combine(
                                    stateDirectory,
                                    "antigravity-last-response.json"),
                                content,
                                cancellationToken);
                        }

                        if (response.IsSuccessStatusCode &&
                            AntigravityQuotaParser.Parse(content).Count > 0)
                        {
                            return content;
                        }

                        lastError = new InvalidOperationException(
                            $"quota RPC가 {(int)response.StatusCode}을 반환했습니다.");
                    }
                    catch (Exception exception) when (
                        exception is HttpRequestException or
                            JsonException or
                            TaskCanceledException or
                            InvalidOperationException)
                    {
                        lastError = exception;
                    }
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }

            throw new InvalidOperationException(
                "Antigravity quota 응답 시간이 초과되었습니다." +
                (lastError is null
                    ? string.Empty
                    : $" 마지막 오류: {lastError.Message}"),
                lastError);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            try
            {
                _ = await outputTask;
                _ = await errorTask;
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                File.Delete(logPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static int? TryReadHttpPort(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var log = reader.ReadToEnd();
            if (!log.Contains(
                    "doRefreshQuota: starting reload",
                    StringComparison.Ordinal))
            {
                return null;
            }

            var match = Regex.Match(
                log,
                @"listening on random port at (?<port>\d+) for HTTP(?:\r?\n|$)");
            return match.Success &&
                   int.TryParse(match.Groups["port"].Value, out var port)
                ? port
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
