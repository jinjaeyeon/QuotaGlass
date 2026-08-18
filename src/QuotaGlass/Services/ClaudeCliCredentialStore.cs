using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGlass.Services;

internal sealed class ClaudeCliCredentialStore(
    string credentialsPath) : IClaudeCredentialStore
{
    public async Task<ClaudeOAuthCredentials?> ReadAsync(
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
            return new ClaudeOAuthCredentials(
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

    public async Task<ClaudeOAuthCredentials?> SaveRefreshedAsync(
        ClaudeOAuthCredentials previous,
        ClaudeOAuthCredentials updated,
        CancellationToken cancellationToken)
    {
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
                return await ReadAsync(cancellationToken);
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
}
