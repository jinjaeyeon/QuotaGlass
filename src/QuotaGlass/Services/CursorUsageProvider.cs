using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class CursorUsageProvider(
    AgentInstallation installation) : IUsageProvider
{
    private const string DashboardServiceUrl =
        "https://api2.cursor.sh/aiserver.v1.DashboardService/";

    private static readonly HttpClient Client = new();

    public string ProviderId => installation.ProviderId;
    public string DisplayName => installation.DisplayName;
    public string IconText => installation.IconText;
    public string AccountLabel => installation.AccountLabel;

    public async Task<UsageSnapshot> FetchAsync(
        CancellationToken cancellationToken)
    {
        if (installation.UsageStatePath is null)
        {
            throw new InvalidOperationException(
                "Cursor 로그인 상태 파일을 찾을 수 없습니다.");
        }

        var accessToken = await ReadAccessTokenAsync(
            installation.UsageStatePath,
            cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var usageTask = PostAsync(
            "GetCurrentPeriodUsage",
            accessToken,
            timeout.Token);
        var planTask = PostAsync(
            "GetPlanInfo",
            accessToken,
            timeout.Token);
        await Task.WhenAll(usageTask, planTask);

        using var usage = await usageTask;
        using var plan = await planTask;
        var now = DateTimeOffset.Now;
        var meters = CursorUsageParser.Parse(usage.RootElement, now);
        if (meters.Count == 0)
        {
            throw new InvalidOperationException(
                "Cursor가 표시 가능한 사용량을 반환하지 않았습니다.");
        }

        return new UsageSnapshot(
            ProviderId,
            DisplayName,
            IconText,
            CursorUsageParser.ReadPlanName(plan.RootElement) ?? "로그인됨",
            meters,
            now,
            "Cursor CLI GetCurrentPeriodUsage");
    }

    private static async Task<string> ReadAccessTokenAsync(
        string authPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            authPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty(
                "accessToken",
                out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new InvalidOperationException(
                "Cursor CLI에 로그인되어 있지 않습니다.");
        }

        return tokenElement.GetString()!;
    }

    private static async Task<JsonDocument> PostAsync(
        string method,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            DashboardServiceUrl + method);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken);
        request.Headers.TryAddWithoutValidation(
            "Connect-Protocol-Version",
            "1");
        request.Headers.TryAddWithoutValidation(
            "x-cursor-client-type",
            "cli");
        request.Content = new StringContent(
            "{}",
            Encoding.UTF8,
            "application/json");

        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                "Cursor 로그인이 만료되었습니다. Cursor Agent에서 다시 로그인하세요.");
        }

        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        return await JsonDocument.ParseAsync(
            content,
            cancellationToken: cancellationToken);
    }
}
