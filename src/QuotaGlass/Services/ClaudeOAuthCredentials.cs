namespace QuotaGlass.Services;

internal static class ClaudeOAuthConstants
{
    public const string ClientId =
        "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string OAuthBetaHeader = "oauth-2025-04-20";
}

internal sealed record ClaudeOAuthCredentials(
    string AccessToken,
    string? RefreshToken,
    long ExpiresAt,
    long RefreshTokenExpiresAt)
{
    private static readonly TimeSpan AccessTokenSafetyWindow =
        TimeSpan.FromMinutes(2);

    public bool HasUsableAccessToken(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(AccessToken) &&
        (ExpiresAt <= 0 ||
         ExpiresAt > now.Add(AccessTokenSafetyWindow)
             .ToUnixTimeMilliseconds());

    public bool IsRefreshTokenExpired(DateTimeOffset now) =>
        RefreshTokenExpiresAt > 0 &&
        RefreshTokenExpiresAt <= now.ToUnixTimeMilliseconds();
}

internal interface IClaudeCredentialStore
{
    Task<ClaudeOAuthCredentials?> ReadAsync(
        CancellationToken cancellationToken);

    Task<ClaudeOAuthCredentials?> SaveRefreshedAsync(
        ClaudeOAuthCredentials previous,
        ClaudeOAuthCredentials updated,
        CancellationToken cancellationToken);
}
