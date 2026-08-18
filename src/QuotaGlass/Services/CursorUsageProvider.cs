using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class CursorUsageProvider(
    AgentInstallation installation) : IUsageProvider
{
    private const string DashboardServiceUrl =
        "https://api2.cursor.sh/aiserver.v1.DashboardService/";
    private const string RefreshTokenUrl =
        "https://api2.cursor.sh/oauth/token";
    private const string CursorClientId =
        "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";

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

        var credentials = await ReadCredentialsAsync(
            installation.UsageStatePath,
            cancellationToken);
        var accessToken = credentials.AccessToken;
        var refreshed = false;

        if (string.IsNullOrWhiteSpace(accessToken) &&
            !string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            accessToken = await RefreshAccessTokenAsync(
                credentials.RefreshToken,
                cancellationToken);
            await TryPersistAccessTokenAsync(
                credentials.SourcePath,
                accessToken,
                cancellationToken);
            refreshed = true;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "Cursor CLI에 로그인되어 있지 않습니다.");
        }

        if (!string.IsNullOrWhiteSpace(credentials.RefreshToken) &&
            IsTokenExpiringSoon(accessToken))
        {
            try
            {
                accessToken = await RefreshAccessTokenAsync(
                    credentials.RefreshToken,
                    cancellationToken);
                await TryPersistAccessTokenAsync(
                    credentials.SourcePath,
                    accessToken,
                    cancellationToken);
                refreshed = true;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                    InvalidOperationException or
                    JsonException)
            {
                // Keep the current token as a best effort; the API call below
                // can still succeed if the token is accepted by the server.
            }
        }

        try
        {
            return await FetchWithTokenAsync(
                accessToken,
                refreshed,
                cancellationToken);
        }
        catch (CursorUnauthorizedException) when (
            !string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            accessToken = await RefreshAccessTokenAsync(
                credentials.RefreshToken,
                cancellationToken);
            await TryPersistAccessTokenAsync(
                credentials.SourcePath,
                accessToken,
                cancellationToken);
            return await FetchWithTokenAsync(
                accessToken,
                true,
                cancellationToken);
        }
    }

    private async Task<UsageSnapshot> FetchWithTokenAsync(
        string accessToken,
        bool refreshed,
        CancellationToken cancellationToken)
    {
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
            refreshed
                ? "Cursor Dashboard API · access token refreshed"
                : "Cursor Dashboard API");
    }

    private static async Task<CursorCredentials> ReadCredentialsAsync(
        string preferredPath,
        CancellationToken cancellationToken)
    {
        foreach (var authPath in GetCredentialPaths(preferredPath))
        {
            try
            {
                var credentials = authPath.EndsWith(
                        ".vscdb",
                        StringComparison.OrdinalIgnoreCase)
                    ? await ReadDesktopCredentialsAsync(
                        authPath,
                        cancellationToken)
                    : await ReadCliCredentialsAsync(
                        authPath,
                        cancellationToken);
                if (credentials is not null &&
                    (!string.IsNullOrWhiteSpace(credentials.AccessToken) ||
                     !string.IsNullOrWhiteSpace(credentials.RefreshToken)))
                {
                    return credentials with { SourcePath = authPath };
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    JsonException or
                    DllNotFoundException or
                    EntryPointNotFoundException or
                    InvalidOperationException)
            {
                // Try the next credential store, normally the CLI auth file.
            }
        }

        throw new InvalidOperationException(
            "Cursor 로그인 상태 파일을 찾을 수 없습니다.");
    }

    private static IReadOnlyList<string> GetCredentialPaths(
        string preferredPath)
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        var desktopPath = Path.Combine(
            appData,
            "Cursor",
            "User",
            "globalStorage",
            "state.vscdb");
        var cliPath = Path.Combine(appData, "Cursor", "auth.json");

        return new[] { desktopPath, preferredPath, cliPath }
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<CursorCredentials?> ReadCliCredentialsAsync(
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
        var root = document.RootElement;
        return new CursorCredentials(
            ReadString(root, "accessToken"),
            ReadString(root, "refreshToken"),
            authPath);
    }

    private static Task<CursorCredentials?> ReadDesktopCredentialsAsync(
        string authPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = WindowsSqlite.ReadCredentialValues(authPath);
        return Task.FromResult<CursorCredentials?>(
            new CursorCredentials(
                values.AccessToken,
                values.RefreshToken,
                authPath));
    }

    private static async Task<string> RefreshAccessTokenAsync(
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException(
                "Cursor refresh token을 찾을 수 없습니다.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            RefreshTokenUrl);
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new
                {
                    grant_type = "refresh_token",
                    client_id = CursorClientId,
                    refresh_token = refreshToken
                }),
            Encoding.UTF8,
            "application/json");

        using var response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Cursor access token 갱신이 실패했습니다. HTTP {(int)response.StatusCode}");
        }

        await using var content = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            content,
            cancellationToken: cancellationToken);

        var accessToken = ParseRefreshAccessToken(document.RootElement);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            var shouldLogout = document.RootElement.TryGetProperty(
                    "shouldLogout",
                    out var logoutElement) &&
                logoutElement.ValueKind == JsonValueKind.True;
            throw new InvalidOperationException(
                shouldLogout
                    ? "Cursor refresh token이 만료되었습니다. Cursor Agent에서 다시 로그인하세요."
                    : "Cursor access token 갱신 응답이 올바르지 않습니다.");
        }

        return accessToken;
    }

    internal static string? ParseRefreshAccessToken(JsonElement root)
    {
        return ReadString(root, "access_token") ??
            ReadString(root, "accessToken");
    }

    private static async Task TryPersistAccessTokenAsync(
        string authPath,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (authPath.EndsWith(
                ".vscdb",
                StringComparison.OrdinalIgnoreCase))
        {
            await TryPersistDesktopAccessTokenAsync(
                authPath,
                accessToken,
                cancellationToken);
            return;
        }

        var temporaryPath = $"{authPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = await File.ReadAllTextAsync(
                authPath,
                cancellationToken);
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return;
            }

            root["accessToken"] = accessToken;
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                }),
                cancellationToken);
            File.Move(temporaryPath, authPath, true);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                DllNotFoundException or
                EntryPointNotFoundException or
                InvalidOperationException)
        {
            // Usage data is still useful even when the local credential file
            // cannot be updated.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task TryPersistDesktopAccessTokenAsync(
        string authPath,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsSqlite.WriteAccessToken(authPath, accessToken);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                DllNotFoundException or
                EntryPointNotFoundException or
                InvalidOperationException)
        {
            // The in-memory refreshed token is still used for this request.
        }
    }

    private static bool IsTokenExpiringSoon(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(
                payload.Length + (4 - payload.Length % 4) % 4,
                '=');
            using var document = JsonDocument.Parse(
                Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty(
                    "exp",
                    out var expElement) ||
                !expElement.TryGetInt64(out var expiresAt))
            {
                return false;
            }

            return expiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() <
                120;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(
        JsonElement root,
        string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
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
            throw new CursorUnauthorizedException();
        }

        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        return await JsonDocument.ParseAsync(
            content,
            cancellationToken: cancellationToken);
    }

    private sealed record CursorCredentials(
        string? AccessToken,
        string? RefreshToken,
        string SourcePath);

    private sealed class CursorUnauthorizedException : Exception
    {
    }

    private static class WindowsSqlite
    {
        private const int SqliteOk = 0;
        private const int SqliteRow = 100;
        private const int SqliteDone = 101;
        private const int OpenReadOnly = 1;
        private const int OpenReadWrite = 2;
        private const int OpenCreate = 4;
        private static readonly IntPtr SqliteTransient = new(-1);

        internal static (string? AccessToken, string? RefreshToken)
            ReadCredentialValues(string path)
        {
            var database = Open(path, OpenReadOnly);
            IntPtr statement = IntPtr.Zero;
            try
            {
                statement = Prepare(
                    database,
                    "SELECT key, value FROM ItemTable " +
                    "WHERE key IN ('cursorAuth/accessToken', 'cursorAuth/refreshToken')");
                string? accessToken = null;
                string? refreshToken = null;
                while (true)
                {
                    var result = sqlite3_step(statement);
                    if (result == SqliteDone)
                    {
                        break;
                    }

                    if (result != SqliteRow)
                    {
                        throw CreateSqliteException(database, result);
                    }

                    var key = ReadColumnText(statement, 0);
                    var value = ReadColumnText(statement, 1);
                    if (key == "cursorAuth/accessToken")
                    {
                        accessToken = value;
                    }
                    else if (key == "cursorAuth/refreshToken")
                    {
                        refreshToken = value;
                    }
                }

                return (accessToken, refreshToken);
            }
            finally
            {
                Finalize(statement);
                Close(database);
            }
        }

        internal static void WriteAccessToken(
            string path,
            string accessToken)
        {
            var database = Open(path, OpenReadWrite | OpenCreate);
            try
            {
                var updated = Execute(
                    database,
                    "UPDATE ItemTable SET value = ? " +
                    "WHERE key = 'cursorAuth/accessToken'",
                    accessToken);
                if (updated == 0)
                {
                    _ = Execute(
                        database,
                        "INSERT INTO ItemTable(key, value) " +
                        "VALUES ('cursorAuth/accessToken', ?)",
                        accessToken);
                }
            }
            finally
            {
                Close(database);
            }
        }

        private static IntPtr Open(string path, int flags)
        {
            var result = sqlite3_open_v2(
                path,
                out var database,
                flags,
                null);
            if (result != SqliteOk)
            {
                var exception = CreateSqliteException(database, result);
                Close(database);
                throw exception;
            }

            return database;
        }

        private static IntPtr Prepare(
            IntPtr database,
            string sql)
        {
            var result = sqlite3_prepare_v2(
                database,
                sql,
                -1,
                out var statement,
                IntPtr.Zero);
            return result == SqliteOk
                ? statement
                : throw CreateSqliteException(database, result);
        }

        private static int Execute(
            IntPtr database,
            string sql,
            string value)
        {
            var statement = Prepare(database, sql);
            try
            {
                var bindResult = sqlite3_bind_text(
                    statement,
                    1,
                    value,
                    -1,
                    SqliteTransient);
                if (bindResult != SqliteOk)
                {
                    throw CreateSqliteException(database, bindResult);
                }

                var result = sqlite3_step(statement);
                if (result != SqliteDone)
                {
                    throw CreateSqliteException(database, result);
                }

                return sqlite3_changes(database);
            }
            finally
            {
                Finalize(statement);
            }
        }

        private static string? ReadColumnText(
            IntPtr statement,
            int column)
        {
            var value = sqlite3_column_text(statement, column);
            return value == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUTF8(value);
        }

        private static InvalidOperationException CreateSqliteException(
            IntPtr database,
            int result) =>
            new(
                $"Cursor Desktop 인증 DB를 읽지 못했습니다. SQLite 오류 {result}: " +
                (database == IntPtr.Zero
                    ? "unknown"
                    : Marshal.PtrToStringUTF8(sqlite3_errmsg(database))));

        private static void Finalize(IntPtr statement)
        {
            if (statement != IntPtr.Zero)
            {
                _ = sqlite3_finalize(statement);
            }
        }

        private static void Close(IntPtr database)
        {
            if (database != IntPtr.Zero)
            {
                _ = sqlite3_close(database);
            }
        }

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_open_v2")]
        private static extern int sqlite3_open_v2(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            out IntPtr database,
            int flags,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? vfs);

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_prepare_v2")]
        private static extern int sqlite3_prepare_v2(
            IntPtr database,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            int length,
            out IntPtr statement,
            IntPtr tail);

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_step")]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_bind_text")]
        private static extern int sqlite3_bind_text(
            IntPtr statement,
            int index,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
            int length,
            IntPtr destructor);

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_column_text")]
        private static extern IntPtr sqlite3_column_text(
            IntPtr statement,
            int column);

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_errmsg")]
        private static extern IntPtr sqlite3_errmsg(IntPtr database);

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_changes")]
        private static extern int sqlite3_changes(IntPtr database);

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_finalize")]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(
            "winsqlite3.dll",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_close")]
        private static extern int sqlite3_close(IntPtr database);
    }
}
