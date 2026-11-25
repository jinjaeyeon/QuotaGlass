using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

internal sealed class ClaudeUsageApiClient
{
    private const string UsageEndpoint =
        "https://api.anthropic.com/api/oauth/usage";
    private const string ResetCreditUsageEndpoint =
        "https://api.anthropic.com/api/oauth/usage" +
        "?cedar_ember=1&skip_spend=1";
    private const string ProfileEndpoint =
        "https://api.anthropic.com/api/oauth/profile";
    private const string ResetProgram = "cedar_ember";
    private const string TokenEndpoint =
        "https://platform.claude.com/v1/oauth/token";
    private static readonly HttpClient DefaultClient = CreateHttpClient();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    private readonly HttpClient client;
    private readonly IReadOnlyList<IClaudeCredentialStore> credentialStores;

    public ClaudeUsageApiClient()
        : this(
            DefaultClient,
            [
                new ClaudeDesktopCredentialStore(),
                new ClaudeCliCredentialStore(GetCredentialsPath())
            ])
    {
    }

    internal ClaudeUsageApiClient(
        HttpClient client,
        string credentialsPath)
        : this(
            client,
            [new ClaudeCliCredentialStore(credentialsPath)])
    {
    }

    internal ClaudeUsageApiClient(
        HttpClient client,
        IReadOnlyList<IClaudeCredentialStore> credentialStores)
    {
        this.client = client;
        this.credentialStores = credentialStores;
    }

    public async Task<IReadOnlyList<UsageMeter>> FetchAsync(
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        foreach (var credentialStore in credentialStores)
        {
            var meters = await TryFetchFromStoreAsync(
                credentialStore,
                cliVersion,
                cancellationToken);
            if (meters.Count > 0)
            {
                return meters;
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<UsageMeter>> TryFetchFromStoreAsync(
        IClaudeCredentialStore credentialStore,
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        var credentials = await credentialStore.ReadAsync(cancellationToken);
        if (credentials is null)
        {
            return [];
        }

        if (!credentials.HasUsableAccessToken(DateTimeOffset.UtcNow))
        {
            credentials = await RefreshCredentialsAsync(
                credentialStore,
                credentials,
                forceRefresh: false,
                cliVersion,
                cancellationToken);
            if (credentials is null)
            {
                return [];
            }
        }

        var response = await GetUsageAsync(
            credentials.AccessToken,
            cliVersion,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            credentials = await RefreshCredentialsAsync(
                credentialStore,
                credentials,
                forceRefresh: true,
                cliVersion,
                cancellationToken);
            if (credentials is null)
            {
                return [];
            }

            response = await GetUsageAsync(
                credentials.AccessToken,
                cliVersion,
                cancellationToken);
        }

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
        {
            return [];
        }

        try
        {
            return ClaudeRateLimitParser.Parse(response.Body);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// 보유 중인 리셋 티켓 요약을 읽는다. meter 조회와 분리되어 있어 실패해도
    /// 사용량 표시에는 영향이 없다. 대상 계정이 아니면 null.
    /// </summary>
    public async Task<ResetCreditSummary?> FetchResetCreditsAsync(
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        foreach (var credentialStore in credentialStores)
        {
            var status = await TryReadResetStatusAsync(
                credentialStore,
                cliVersion,
                cancellationToken);
            if (status is not null)
            {
                return status.Value.Status.ToSummary(DateTimeOffset.UtcNow);
            }
        }

        return null;
    }

    /// <summary>
    /// 리셋 티켓 1장을 사용한다. 되돌릴 수 없는 호출이므로 호출 직전에 상태를
    /// 다시 조회해 같은 계정의 사용 가능한 티켓인지 확인한다.
    /// requestId는 클릭 한 번당 하나만 만들어 재시도에도 재사용한다.
    /// </summary>
    public async Task<ResetClaimResult> ClaimResetAsync(
        string grantId,
        string requestId,
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        foreach (var credentialStore in credentialStores)
        {
            var status = await TryReadResetStatusAsync(
                credentialStore,
                cliVersion,
                cancellationToken);
            var grant = status?.Status.Grants.FirstOrDefault(item =>
                item.Id == grantId);
            if (status is null || grant is null)
            {
                continue;
            }

            if (!status.Value.Status.IsUsable(grant, DateTimeOffset.UtcNow) ||
                !grant.UsableNow)
            {
                return new ResetClaimResult(
                    ResetClaimOutcome.NotUsable,
                    grant.UnavailableReason,
                    null);
            }

            var token = status.Value.Credentials.AccessToken;
            var organizationId = await ReadOrganizationIdAsync(
                token,
                cliVersion,
                cancellationToken);
            if (organizationId is null)
            {
                return new ResetClaimResult(
                    ResetClaimOutcome.AuthError,
                    "organization",
                    null);
            }

            var response = await SendAsync(
                HttpMethod.Post,
                "https://api.anthropic.com/api/organizations/" +
                $"{Uri.EscapeDataString(organizationId)}/reset_rate_limits",
                token,
                cliVersion,
                JsonContent.Create(
                    new
                    {
                        program = ResetProgram,
                        grant_id = grantId,
                        request_id = requestId
                    }),
                readBodyOnFailure: true,
                cancellationToken);
            return ParseClaimResponse(response.StatusCode, response.Body);
        }

        return new ResetClaimResult(
            ResetClaimOutcome.Unavailable,
            null,
            null);
    }

    internal static ResetClaimResult ParseClaimResponse(
        HttpStatusCode statusCode,
        string? body)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return new ResetClaimResult(
                ResetClaimOutcome.RateLimited,
                null,
                null);
        }

        if (statusCode is
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ResetClaimResult(
                ResetClaimOutcome.AuthError,
                null,
                null);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new ResetClaimResult(ResetClaimOutcome.Error, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ResetClaimResult(
                    ResetClaimOutcome.Error,
                    null,
                    null);
            }

            var result = root.TryGetProperty("result", out var resultElement) &&
                         resultElement.ValueKind == JsonValueKind.String
                ? resultElement.GetString()
                : null;
            var reason = root.TryGetProperty("reason", out var reasonElement) &&
                         reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
            long? resetsLeft =
                root.TryGetProperty("resets_left", out var leftElement) &&
                leftElement.ValueKind == JsonValueKind.Number &&
                leftElement.TryGetInt64(out var left)
                    ? left
                    : null;
            var outcome = result switch
            {
                "reset" => ResetClaimOutcome.Success,
                "already_used" => ResetClaimOutcome.AlreadyUsed,
                "not_limited" => ResetClaimOutcome.NotLimited,
                "cooldown" => ResetClaimOutcome.Cooldown,
                "ineligible" => ResetClaimOutcome.Ineligible,
                "unavailable" => ResetClaimOutcome.Unavailable,
                _ => ResetClaimOutcome.Error
            };
            return new ResetClaimResult(outcome, reason, resetsLeft);
        }
        catch (JsonException)
        {
            return new ResetClaimResult(ResetClaimOutcome.Error, null, null);
        }
    }

    private async Task<(ClaudeResetStatus Status,
        ClaudeOAuthCredentials Credentials)?> TryReadResetStatusAsync(
        IClaudeCredentialStore credentialStore,
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        var result = await SendWithRefreshAsync(
            credentialStore,
            cliVersion,
            token => SendAsync(
                HttpMethod.Get,
                ResetCreditUsageEndpoint,
                token,
                cliVersion,
                content: null,
                readBodyOnFailure: false,
                cancellationToken),
            cancellationToken);
        if (result is null ||
            !result.Value.Response.IsSuccess ||
            string.IsNullOrWhiteSpace(result.Value.Response.Body))
        {
            return null;
        }

        try
        {
            return ClaudeResetCreditParser.Parse(
                    result.Value.Response.Body) is { } status
                ? (status, result.Value.Credentials)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ReadOrganizationIdAsync(
        string accessToken,
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Get,
            ProfileEndpoint,
            accessToken,
            cliVersion,
            content: null,
            readBodyOnFailure: false,
            cancellationToken);
        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return document.RootElement.TryGetProperty(
                       "organization",
                       out var organization) &&
                   organization.ValueKind == JsonValueKind.Object &&
                   TryReadString(organization, "uuid", out var uuid)
                ? uuid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(ApiResponse Response,
        ClaudeOAuthCredentials Credentials)?> SendWithRefreshAsync(
        IClaudeCredentialStore credentialStore,
        string? cliVersion,
        Func<string, Task<ApiResponse>> send,
        CancellationToken cancellationToken)
    {
        var credentials = await credentialStore.ReadAsync(cancellationToken);
        if (credentials is null)
        {
            return null;
        }

        if (!credentials.HasUsableAccessToken(DateTimeOffset.UtcNow))
        {
            credentials = await RefreshCredentialsAsync(
                credentialStore,
                credentials,
                forceRefresh: false,
                cliVersion,
                cancellationToken);
            if (credentials is null)
            {
                return null;
            }
        }

        var response = await send(credentials.AccessToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            credentials = await RefreshCredentialsAsync(
                credentialStore,
                credentials,
                forceRefresh: true,
                cliVersion,
                cancellationToken);
            if (credentials is null)
            {
                return null;
            }

            response = await send(credentials.AccessToken);
        }

        return (response, credentials);
    }

    private async Task<ApiResponse> SendAsync(
        HttpMethod method,
        string url,
        string accessToken,
        string? cliVersion,
        HttpContent? content,
        bool readBodyOnFailure,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken);
        request.Headers.TryAddWithoutValidation(
            "anthropic-beta",
            ClaudeOAuthConstants.OAuthBetaHeader);
        request.Headers.UserAgent.ParseAdd(BuildUserAgent(cliVersion));

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = response.IsSuccessStatusCode || readBodyOnFailure
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return new ApiResponse(response.StatusCode, body);
        }
        catch (HttpRequestException)
        {
            return new ApiResponse(HttpStatusCode.ServiceUnavailable, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiResponse(HttpStatusCode.RequestTimeout, null);
        }
    }

    private async Task<ApiResponse> GetUsageAsync(
        string accessToken,
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken);
        request.Headers.TryAddWithoutValidation(
            "anthropic-beta",
            ClaudeOAuthConstants.OAuthBetaHeader);
        request.Headers.UserAgent.ParseAdd(BuildUserAgent(cliVersion));

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return new ApiResponse(response.StatusCode, body);
        }
        catch (HttpRequestException)
        {
            return new ApiResponse(HttpStatusCode.ServiceUnavailable, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiResponse(HttpStatusCode.RequestTimeout, null);
        }
    }

    private async Task<ClaudeOAuthCredentials?> RefreshCredentialsAsync(
        IClaudeCredentialStore credentialStore,
        ClaudeOAuthCredentials current,
        bool forceRefresh,
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            return null;
        }

        await RefreshGate.WaitAsync(cancellationToken);
        try
        {
            var latest = await credentialStore.ReadAsync(cancellationToken);
            if (latest is null ||
                string.IsNullOrWhiteSpace(latest.RefreshToken) ||
                latest.IsRefreshTokenExpired(DateTimeOffset.UtcNow))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if ((!forceRefresh && latest.HasUsableAccessToken(now)) ||
                (forceRefresh &&
                 !string.Equals(
                     latest.AccessToken,
                     current.AccessToken,
                     StringComparison.Ordinal) &&
                 latest.HasUsableAccessToken(now)))
            {
                return latest;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                TokenEndpoint)
            {
                Content = JsonContent.Create(
                    new
                    {
                        grant_type = "refresh_token",
                        refresh_token = latest.RefreshToken,
                        client_id = ClaudeOAuthConstants.ClientId
                    })
            };
            request.Headers.TryAddWithoutValidation(
                "anthropic-beta",
                ClaudeOAuthConstants.OAuthBetaHeader);
            request.Headers.UserAgent.ParseAdd(BuildUserAgent(cliVersion));

            try
            {
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                return await SaveRefreshedCredentialsAsync(
                    credentialStore,
                    latest,
                    body,
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task<ClaudeOAuthCredentials?>
        SaveRefreshedCredentialsAsync(
            IClaudeCredentialStore credentialStore,
            ClaudeOAuthCredentials previous,
            string responseBody,
            CancellationToken cancellationToken)
    {
        using var responseDocument = JsonDocument.Parse(responseBody);
        var responseRoot = responseDocument.RootElement;
        if (!TryReadString(
                responseRoot,
                "access_token",
                out var accessToken) ||
            !TryReadLong(responseRoot, "expires_in", out var expiresIn))
        {
            return null;
        }

        var refreshToken = TryReadString(
            responseRoot,
            "refresh_token",
            out var rotatedRefreshToken)
            ? rotatedRefreshToken
            : previous.RefreshToken;
        var refreshTokenExpiresAt = TryReadLong(
            responseRoot,
            "refresh_token_expires_in",
            out var refreshTokenExpiresIn)
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
              (refreshTokenExpiresIn * 1000)
            : previous.RefreshTokenExpiresAt;
        var updated = previous with
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                        (expiresIn * 1000),
            RefreshTokenExpiresAt = refreshTokenExpiresAt
        };

        return await credentialStore.SaveRefreshedAsync(
            previous,
            updated,
            cancellationToken);
    }

    private static bool TryReadString(
        JsonElement objectElement,
        string propertyName,
        out string value)
    {
        if (objectElement.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadLong(
        JsonElement objectElement,
        string propertyName,
        out long value)
    {
        if (objectElement.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt64(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String &&
                long.TryParse(property.GetString(), out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static string GetCredentialsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json");

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

    private static string BuildUserAgent(string? cliVersion)
    {
        var version = new string((cliVersion ?? string.Empty)
            .Where(character =>
                char.IsLetterOrDigit(character) ||
                character is '.' or '-' or '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(version)
            ? "claude-code"
            : $"claude-code/{version}";
    }

    private readonly record struct ApiResponse(
        HttpStatusCode StatusCode,
        string? Body)
    {
        public bool IsSuccess =>
            (int)StatusCode >= 200 &&
            (int)StatusCode <= 299;
    }
}

internal enum ResetClaimOutcome
{
    Success,
    AlreadyUsed,
    NotLimited,
    Cooldown,
    Ineligible,
    Unavailable,
    NotUsable,
    RateLimited,
    AuthError,
    Error
}

internal sealed record ResetClaimResult(
    ResetClaimOutcome Outcome,
    string? Reason,
    long? ResetsLeft);
