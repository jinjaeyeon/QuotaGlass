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
