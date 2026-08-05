using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class GitHubCopilotUsageProvider(
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
                "GitHub Copilot CLI 실행 파일을 찾을 수 없습니다.");
        }

        using var process = StartServer(installation.ExecutablePath);
        try
        {
            await SendRequestAsync(process, 1, "connect", new { }, cancellationToken);
            await ReadResultAsync(process, 1, cancellationToken);

            await SendRequestAsync(
                process,
                2,
                "account.getCurrentAuth",
                null,
                cancellationToken);
            var auth = await ReadResultAsync(process, 2, cancellationToken);
            var accountLabel = ReadAccountLabel(auth);

            await SendRequestAsync(
                process,
                3,
                "account.getQuota",
                null,
                cancellationToken);
            var quota = await ReadResultAsync(process, 3, cancellationToken);
            var now = DateTimeOffset.Now;
            var meters = GitHubCopilotQuotaParser.Parse(quota, now);
            if (meters.Count == 0)
            {
                throw new InvalidOperationException(
                    "Copilot이 표시 가능한 quota를 반환하지 않았습니다.");
            }

            return new UsageSnapshot(
                ProviderId,
                DisplayName,
                IconText,
                accountLabel,
                meters,
                now,
                "GitHub Copilot CLI account.getQuota");
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    private static Process StartServer(string executablePath)
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
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--no-auto-update");
        startInfo.ArgumentList.Add("--log-level");
        startInfo.ArgumentList.Add("none");
        startInfo.ArgumentList.Add("--stdio");

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "GitHub Copilot CLI server를 시작하지 못했습니다.");
    }

    private static async Task SendRequestAsync(
        Process process,
        int id,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new { jsonrpc = "2.0", id, method, @params = parameters });
        var header = Encoding.ASCII.GetBytes(
            $"Content-Length: {body.Length}\r\n\r\n");
        var stream = process.StandardInput.BaseStream;
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<JsonElement> ReadResultAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var contentLength = await ReadContentLengthAsync(
                process.StandardOutput.BaseStream,
                cancellationToken);
            var body = new byte[contentLength];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(
                body,
                cancellationToken);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                idElement.GetInt32() != expectedId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(error.ToString());
            }

            return root.GetProperty("result").Clone();
        }
    }

    private static async Task<int> ReadContentLengthAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var oneByte = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            var count = await stream.ReadAsync(oneByte, cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException(
                    "GitHub Copilot CLI server가 응답 없이 종료되었습니다.");
            }

            bytes.Add(oneByte[0]);
            var length = bytes.Count;
            if (length >= 4 &&
                bytes[length - 4] == '\r' &&
                bytes[length - 3] == '\n' &&
                bytes[length - 2] == '\r' &&
                bytes[length - 1] == '\n')
            {
                var headers = Encoding.ASCII.GetString(
                    bytes.ToArray(),
                    0,
                    length - 4);
                foreach (var line in headers.Split("\r\n"))
                {
                    var separator = line.IndexOf(':');
                    if (separator < 0 ||
                        !line[..separator].Equals(
                            "Content-Length",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (int.TryParse(
                            line[(separator + 1)..].Trim(),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var contentLength) &&
                        contentLength >= 0)
                    {
                        return contentLength;
                    }
                }

                throw new InvalidDataException(
                    "GitHub Copilot CLI 응답에 Content-Length가 없습니다.");
            }
        }

        throw new InvalidDataException(
            "GitHub Copilot CLI 응답 헤더가 너무 큽니다.");
    }

    private static string ReadAccountLabel(JsonElement auth)
    {
        if (!auth.TryGetProperty("authInfo", out var authInfo) ||
            authInfo.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "GitHub Copilot CLI에 로그인되어 있지 않습니다.");
        }

        var login = authInfo.TryGetProperty("login", out var loginElement) &&
                    loginElement.ValueKind == JsonValueKind.String
            ? loginElement.GetString()
            : null;
        var plan = authInfo.TryGetProperty("copilotUser", out var user) &&
                   user.ValueKind == JsonValueKind.Object &&
                   user.TryGetProperty("copilot_plan", out var planElement) &&
                   planElement.ValueKind == JsonValueKind.String
            ? planElement.GetString()
            : null;

        return string.Join(
            " · ",
            new[] { login, DescribePlan(plan) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? DescribePlan(string? plan) => plan switch
    {
        "individual" => "개인",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "pro" => "Pro",
        "pro_plus" => "Pro+",
        "free" => "Free",
        _ => plan
    };

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
