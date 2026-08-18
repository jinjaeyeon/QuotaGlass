using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

internal sealed class ClaudeUsageApiClient
{
    private const string UsageEndpoint =
        "https://api.anthropic.com/api/oauth/usage";
    private const string TokenEndpoint =
        "https://platform.claude.com/v1/oauth/token";
    private const string OAuthClientId =
        "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string OAuthBetaHeader = "oauth-2025-04-20";
    private static readonly TimeSpan AccessTokenSafetyWindow =
        TimeSpan.FromMinutes(2);
    private static readonly HttpClient DefaultClient = CreateHttpClient();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    private readonly HttpClient client;
    private readonly string credentialsPath;

    public ClaudeUsageApiClient()
        : this(DefaultClient, GetCredentialsPath())
    {
    }

    internal ClaudeUsageApiClient(
        HttpClient client,
        string credentialsPath)
    {
        this.client = client;
        this.credentialsPath = credentialsPath;
    }

    public async Task<IReadOnlyList<UsageMeter>> FetchAsync(
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        var credentials = await ReadCredentialsAsync(cancellationToken);
        if (credentials is null)
        {
            return [];
        }

        if (!credentials.HasUsableAccessToken(DateTimeOffset.UtcNow))
        {
            credentials = await RefreshCredentialsAsync(
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
            OAuthBetaHeader);
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

    private async Task<OAuthCredentials?> RefreshCredentialsAsync(
        OAuthCredentials current,
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
            var latest = await ReadCredentialsAsync(cancellationToken);
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
                        client_id = OAuthClientId
                    })
            };
            request.Headers.TryAddWithoutValidation(
                "anthropic-beta",
                OAuthBetaHeader);
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

    private async Task<OAuthCredentials?> SaveRefreshedCredentialsAsync(
        OAuthCredentials previous,
        string responseBody,
        CancellationToken cancellationToken)
    {
        using var responseDocument = JsonDocument.Parse(responseBody);
        var responseRoot = responseDocument.RootElement;
        string accessToken;
        if (!TryReadString(responseRoot, "access_token", out accessToken) ||
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

        try
        {
            var originalJson = await File.ReadAllTextAsync(
                credentialsPath,
                cancellationToken);
            var root = JsonNode.Parse(originalJson)?.AsObject();
            var oauth = root?["claudeAiOauth"] as JsonObject;
            if (root is null || oauth is null)
            {
                return null;
            }

            if (oauth["accessToken"]?.GetValue<string>() is { } currentAccessToken &&
                !string.Equals(
                    currentAccessToken,
                    previous.AccessToken,
                    StringComparison.Ordinal))
            {
                return await ReadCredentialsAsync(cancellationToken);
            }

            oauth["accessToken"] = updated.AccessToken;
            if (!string.IsNullOrWhiteSpace(updated.RefreshToken))
            {
                oauth["refreshToken"] = updated.RefreshToken;
            }

            oauth["expiresAt"] = updated.ExpiresAt;
            if (updated.RefreshTokenExpiresAt > 0)
            {
                oauth["refreshTokenExpiresAt"] = updated.RefreshTokenExpiresAt;
            }

            var temporaryPath = $"{credentialsPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    root.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                    cancellationToken);
                File.Move(temporaryPath, credentialsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return updated;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<OAuthCredentials?> ReadCredentialsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(credentialsPath);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty(
                    "claudeAiOauth",
                    out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object ||
                !TryReadString(oauth, "accessToken", out var accessToken))
            {
                return null;
            }

            var refreshToken = TryReadString(
                oauth,
                "refreshToken",
                out var value)
                ? value
                : null;
            var expiresAt = TryReadLong(oauth, "expiresAt", out var expires)
                ? expires
                : 0;
            var refreshTokenExpiresAt = TryReadLong(
                oauth,
                "refreshTokenExpiresAt",
                out var refreshExpires)
                ? refreshExpires
                : 0;
            return new OAuthCredentials(
                accessToken,
                refreshToken,
                expiresAt,
                refreshTokenExpiresAt);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private sealed record OAuthCredentials(
        string AccessToken,
        string? RefreshToken,
        long ExpiresAt,
        long RefreshTokenExpiresAt)
    {
        public bool HasUsableAccessToken(DateTimeOffset now) =>
            !string.IsNullOrWhiteSpace(AccessToken) &&
            (ExpiresAt <= 0 ||
             ExpiresAt > now.Add(AccessTokenSafetyWindow)
                 .ToUnixTimeMilliseconds());

        public bool IsRefreshTokenExpired(DateTimeOffset now) =>
            RefreshTokenExpiresAt > 0 &&
            RefreshTokenExpiresAt <= now.ToUnixTimeMilliseconds();
    }
}
