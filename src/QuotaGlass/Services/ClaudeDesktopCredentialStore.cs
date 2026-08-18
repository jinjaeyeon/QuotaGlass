using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaGlass.Services;

internal sealed class ClaudeDesktopCredentialStore : IClaudeCredentialStore
{
    private const string CurrentCacheProperty = "oauth:tokenCacheV2";
    private const string LegacyCacheProperty = "oauth:tokenCache";
    private const string SafeStorageVersion10 = "v10";
    private const string SafeStorageVersion11 = "v11";
    private const string DpapiPrefix = "DPAPI";

    private readonly string configPath;
    private readonly string localStatePath;
    private ClaudeOAuthCredentials? cachedCredentials;
    private DateTime lastCacheWriteUtc;

    public ClaudeDesktopCredentialStore()
        : this(GetConfigPath(), GetLocalStatePath())
    {
    }

    internal ClaudeDesktopCredentialStore(
        string configPath,
        string localStatePath)
    {
        this.configPath = configPath;
        this.localStatePath = localStatePath;
    }

    internal static bool HasCredentialFiles()
    {
        return File.Exists(GetConfigPath()) &&
               File.Exists(GetLocalStatePath());
    }

    public async Task<ClaudeOAuthCredentials?> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var cacheWriteUtc = File.GetLastWriteTimeUtc(configPath);
            var now = DateTimeOffset.UtcNow;
            if (cachedCredentials is not null &&
                cacheWriteUtc == lastCacheWriteUtc &&
                !cachedCredentials.IsRefreshTokenExpired(now))
            {
                return cachedCredentials;
            }

            var configJson = await File.ReadAllTextAsync(
                configPath,
                cancellationToken);
            var localStateJson = await File.ReadAllTextAsync(
                localStatePath,
                cancellationToken);
            using var config = JsonDocument.Parse(configJson);
            using var localState = JsonDocument.Parse(localStateJson);

            if (!TryReadSafeStorageKey(
                    localState.RootElement,
                    out var safeStorageKey))
            {
                return null;
            }

            foreach (var cacheProperty in new[]
                     {
                         CurrentCacheProperty,
                         LegacyCacheProperty
                     })
            {
                if (!config.RootElement.TryGetProperty(
                        cacheProperty,
                        out var cacheElement) ||
                    cacheElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var cache = DecryptSafeStorageValue(
                    cacheElement.GetString(),
                    safeStorageKey);
                var credentials = FindCredentials(cache);
                if (credentials is not null)
                {
                    cachedCredentials = credentials;
                    lastCacheWriteUtc = cacheWriteUtc;
                    return credentials;
                }
            }

            return null;
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
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public Task<ClaudeOAuthCredentials?> SaveRefreshedAsync(
        ClaudeOAuthCredentials previous,
        ClaudeOAuthCredentials updated,
        CancellationToken cancellationToken)
    {
        // Do not rewrite Claude Desktop's encrypted store. The refreshed
        // token remains in memory and the Desktop app owns persistence.
        CacheRefreshedCredentials(updated);
        return Task.FromResult<ClaudeOAuthCredentials?>(updated);
    }

    private void CacheRefreshedCredentials(ClaudeOAuthCredentials updated)
    {
        cachedCredentials = updated;
        lastCacheWriteUtc = File.GetLastWriteTimeUtc(configPath);
    }

    private static ClaudeOAuthCredentials? FindCredentials(
        byte[]? decryptedCache)
    {
        if (decryptedCache is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(decryptedCache);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (!entry.Name.StartsWith(
                    $"{ClaudeOAuthConstants.ClientId}:",
                    StringComparison.OrdinalIgnoreCase) ||
                !entry.Name.Contains(
                    ":user:inference",
                    StringComparison.OrdinalIgnoreCase) ||
                entry.Value.ValueKind != JsonValueKind.Object ||
                !TryReadString(entry.Value, "token", out var accessToken))
            {
                continue;
            }

            var refreshToken = TryReadString(
                entry.Value,
                "refreshToken",
                out var value)
                ? value
                : null;
            var expiresAt = TryReadLong(
                entry.Value,
                "expiresAt",
                out var expires)
                ? expires
                : 0;
            return new ClaudeOAuthCredentials(
                accessToken,
                refreshToken,
                expiresAt,
                0);
        }

        return null;
    }

    private static byte[]? DecryptSafeStorageValue(
        string? encodedValue,
        byte[] safeStorageKey)
    {
        if (string.IsNullOrWhiteSpace(encodedValue))
        {
            return null;
        }

        var encrypted = Convert.FromBase64String(encodedValue);
        if (encrypted.Length < 3 + 12 + 16 ||
            !IsSafeStorageVersion(encrypted))
        {
            return null;
        }

        var payload = encrypted[3..];
        var nonce = payload[..12];
        var tag = payload[^16..];
        var ciphertext = payload[12..^16];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(safeStorageKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static bool TryReadSafeStorageKey(
        JsonElement localState,
        out byte[] key)
    {
        key = [];
        if (!localState.TryGetProperty("os_crypt", out var osCrypt) ||
            osCrypt.ValueKind != JsonValueKind.Object ||
            !TryReadString(osCrypt, "encrypted_key", out var encodedKey))
        {
            return false;
        }

        var encryptedKey = Convert.FromBase64String(encodedKey);
        var prefix = Encoding.ASCII.GetBytes(DpapiPrefix);
        if (encryptedKey.Length <= prefix.Length ||
            !encryptedKey.AsSpan(0, prefix.Length).SequenceEqual(prefix))
        {
            return false;
        }

        return TryUnprotectDpapi(
            encryptedKey[prefix.Length..],
            out key);
    }

    private static bool IsSafeStorageVersion(byte[] value) =>
        (value[0] == 'v' && value[1] == '1' &&
         (value[2] == '0' || value[2] == '1'));

    private static bool TryUnprotectDpapi(
        byte[] encrypted,
        out byte[] plaintext)
    {
        plaintext = [];
        var inputPointer = IntPtr.Zero;
        var outputPointer = IntPtr.Zero;
        try
        {
            inputPointer = Marshal.AllocHGlobal(encrypted.Length);
            Marshal.Copy(encrypted, 0, inputPointer, encrypted.Length);
            var input = new DataBlob
            {
                Size = encrypted.Length,
                Data = inputPointer
            };
            var output = new DataBlob();
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    ref output))
            {
                return false;
            }

            outputPointer = output.Data;
            plaintext = new byte[output.Size];
            Marshal.Copy(output.Data, plaintext, 0, output.Size);
            return true;
        }
        finally
        {
            if (outputPointer != IntPtr.Zero)
            {
                LocalFree(outputPointer);
            }

            if (inputPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPointer);
            }
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

    private static string GetConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude",
            "config.json");

    private static string GetLocalStatePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude",
            "Local State");

    [DllImport("Crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        ref DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }
}
