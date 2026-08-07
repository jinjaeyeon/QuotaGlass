using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class AntigravityUsageProvider(
    AgentInstallation installation) : IUsageProvider
{
    private static readonly HttpClient LocalClient = new(
        new HttpClientHandler
        {
            UseProxy = false
        })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public string ProviderId => installation.ProviderId;
    public string DisplayName => installation.DisplayName;
    public string IconText => installation.IconText;
    public string AccountLabel => installation.AccountLabel;

    public async Task<UsageSnapshot> FetchAsync(
        CancellationToken cancellationToken)
    {
        var cliPath = installation.ExecutablePath;
        if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
        {
            return new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                AccountLabel,
                [],
                DateTimeOffset.Now,
                "Antigravity 설치 상태",
                UsageSnapshotState.AdapterPending,
                "IDE 설치됨 · Antigravity CLI는 아직 설치되지 않음");
        }

        var json = await ReadQuotaSummaryAsync(cliPath, cancellationToken);
        var meters = AntigravityQuotaParser.Parse(json);
        return meters.Count > 0
            ? new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                "Google AI · 모델 그룹별",
                meters,
                DateTimeOffset.Now,
                "Antigravity RetrieveUserQuotaSummary")
            : new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                AccountLabel,
                [],
                DateTimeOffset.Now,
                "Antigravity RetrieveUserQuotaSummary",
                UsageSnapshotState.AdapterPending,
                "로그인됨 · quota 응답에 사용 가능한 bucket이 없음");
    }

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
                            $"http://127.0.0.1:{port}/" +
                            "exa.language_server_pb.LanguageServerService/" +
                            "RetrieveUserQuotaSummary");
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
