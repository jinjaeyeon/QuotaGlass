using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuotaGlass.Services;

public sealed class ManagedCliInstaller
{
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private readonly string _rootDirectory;

    public ManagedCliInstaller()
        : this(ManagedCliStore.RootDirectory)
    {
    }

    internal ManagedCliInstaller(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
    }

    public async Task<ManagedCliInstallResult> InstallAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var definition = ManagedCliCatalog.Find(providerId);
        var release = definition.Source switch
        {
            ManagedCliSource.GitHubRelease =>
                await ReadGitHubReleaseAsync(definition, cancellationToken),
            ManagedCliSource.ClaudeRelease =>
                await ReadClaudeReleaseAsync(definition, cancellationToken),
            ManagedCliSource.AntigravityRelease =>
                await ReadAntigravityReleaseAsync(definition, cancellationToken),
            _ => throw new InvalidOperationException(
                "알 수 없는 관리 CLI 배포 형식입니다.")
        };

        var safeVersion = SanitizeVersion(release.Version);
        var providerDirectory = Path.Combine(
            _rootDirectory,
            definition.ProviderId);
        var versionDirectory = Path.Combine(providerDirectory, safeVersion);
        var finalExecutable = Path.Combine(
            versionDirectory,
            definition.InstalledExecutableName);
        if (File.Exists(finalExecutable))
        {
            ManagedCliStore.Activate(
                _rootDirectory,
                definition.ProviderId,
                release.Version,
                finalExecutable);
            return new ManagedCliInstallResult(
                definition.ProviderId,
                release.Version,
                finalExecutable);
        }

        Directory.CreateDirectory(providerDirectory);
        var stagingDirectory = Path.Combine(
            providerDirectory,
            $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var payloadPath = Path.Combine(stagingDirectory, "payload");
            await DownloadAndVerifyAsync(
                release,
                payloadPath,
                cancellationToken);

            var stagedExecutable = Path.Combine(
                stagingDirectory,
                definition.InstalledExecutableName);
            if (release.IsZip)
            {
                var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
                ExtractZipSafely(payloadPath, extractedDirectory);
                var packagedExecutable = Directory.EnumerateFiles(
                        extractedDirectory,
                        definition.ExecutableInPackage,
                        SearchOption.AllDirectories)
                    .SingleOrDefault()
                    ?? throw new InvalidDataException(
                        $"압축 파일에 {definition.ExecutableInPackage}가 없습니다.");
                File.Copy(packagedExecutable, stagedExecutable);
            }
            else
            {
                File.Move(payloadPath, stagedExecutable);
            }

            Directory.CreateDirectory(versionDirectory);
            File.Move(stagedExecutable, finalExecutable, overwrite: true);
            ManagedCliStore.Activate(
                _rootDirectory,
                definition.ProviderId,
                release.Version,
                finalExecutable);

            return new ManagedCliInstallResult(
                definition.ProviderId,
                release.Version,
                finalExecutable);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static ManagedCliRelease ParseGitHubRelease(
        string json,
        string assetName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = root.GetProperty("tag_name").GetString();
        var asset = root.GetProperty("assets")
            .EnumerateArray()
            .SingleOrDefault(candidate =>
                candidate.GetProperty("name").GetString() == assetName);
        if (string.IsNullOrWhiteSpace(version) ||
            asset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                $"공식 릴리스에서 {assetName}을 찾지 못했습니다.");
        }

        var url = asset.GetProperty("browser_download_url").GetString();
        var digest = asset.TryGetProperty("digest", out var digestValue)
            ? digestValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url) ||
            string.IsNullOrWhiteSpace(digest) ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "공식 릴리스 자산에 SHA-256 검증 정보가 없습니다.");
        }

        var downloadUrl = new Uri(url);
        RequireHttps(downloadUrl);
        return new ManagedCliRelease(
            version,
            downloadUrl,
            HashAlgorithmName.SHA256,
            digest["sha256:".Length..],
            assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    internal static ManagedCliRelease ParseClaudeRelease(
        string version,
        string manifestJson,
        string platform,
        string baseUrl)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var checksum = document.RootElement
            .GetProperty("platforms")
            .GetProperty(platform)
            .GetProperty("checksum")
            .GetString();
        if (string.IsNullOrWhiteSpace(checksum))
        {
            throw new InvalidDataException(
                $"Claude {platform} 체크섬이 없습니다.");
        }

        return new ManagedCliRelease(
            version,
            new Uri($"{baseUrl.TrimEnd('/')}/{version}/{platform}/claude.exe"),
            HashAlgorithmName.SHA256,
            checksum,
            false);
    }

    internal static ManagedCliRelease ParseAntigravityRelease(
        string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetString();
        var url = root.GetProperty("url").GetString();
        var checksum = root.GetProperty("sha512").GetString();
        if (string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(url) ||
            string.IsNullOrWhiteSpace(checksum))
        {
            throw new InvalidDataException(
                "Antigravity 릴리스 정보가 완전하지 않습니다.");
        }

        var downloadUrl = new Uri(url);
        RequireHttps(downloadUrl);
        return new ManagedCliRelease(
            version,
            downloadUrl,
            HashAlgorithmName.SHA512,
            checksum,
            false);
    }

    internal static void ExtractZipSafely(
        string archivePath,
        string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(
                Path.Combine(destinationDirectory, entry.FullName));
            if (!destinationPath.StartsWith(
                    destinationRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "압축 파일에 대상 디렉터리를 벗어나는 경로가 있습니다.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static async Task<ManagedCliRelease> ReadGitHubReleaseAsync(
        ManagedCliDefinition definition,
        CancellationToken cancellationToken)
    {
        var json = await Client.GetStringAsync(
            definition.MetadataUrl,
            cancellationToken);
        return ParseGitHubRelease(json, definition.AssetName);
    }

    private static async Task<ManagedCliRelease> ReadClaudeReleaseAsync(
        ManagedCliDefinition definition,
        CancellationToken cancellationToken)
    {
        var baseUrl = definition.MetadataUrl.TrimEnd('/');
        var version = (await Client.GetStringAsync(
                $"{baseUrl}/latest",
                cancellationToken))
            .Trim();
        if (!Version.TryParse(version.Split('-')[0], out _))
        {
            throw new InvalidDataException(
                "Claude 최신 버전 응답이 올바르지 않습니다.");
        }

        var manifestJson = await Client.GetStringAsync(
            $"{baseUrl}/{version}/manifest.json",
            cancellationToken);
        return ParseClaudeRelease(
            version,
            manifestJson,
            definition.AssetName,
            baseUrl);
    }

    private static async Task<ManagedCliRelease> ReadAntigravityReleaseAsync(
        ManagedCliDefinition definition,
        CancellationToken cancellationToken)
    {
        var json = await Client.GetStringAsync(
            $"{definition.MetadataUrl.TrimEnd('/')}/manifests/" +
            $"{definition.AssetName}.json",
            cancellationToken);
        return ParseAntigravityRelease(json);
    }

    private static async Task DownloadAndVerifyAsync(
        ManagedCliRelease release,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(
            release.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
        {
            throw new InvalidDataException(
                "CLI 다운로드 파일이 허용 크기를 초과했습니다.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(release.HashAlgorithm);
        var buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > MaximumDownloadBytes)
            {
                throw new InvalidDataException(
                    "CLI 다운로드 파일이 허용 크기를 초과했습니다.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(
                release.ExpectedHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "CLI 다운로드 파일의 체크섬이 공식 릴리스와 다릅니다.");
        }
    }

    private static string SanitizeVersion(string version)
    {
        var sanitized = string.Concat(version.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ||
            character is '/' or '\\'
                ? '_'
                : character));
        return string.IsNullOrWhiteSpace(sanitized)
            ? throw new InvalidDataException("CLI 버전이 비어 있습니다.")
            : sanitized;
    }

    private static void RequireHttps(Uri uri)
    {
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "CLI 다운로드 주소가 HTTPS가 아닙니다.");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("QuotaGlass", "0.1"));
        return client;
    }
}

public sealed record ManagedCliInstallResult(
    string ProviderId,
    string Version,
    string ExecutablePath);

internal sealed record ManagedCliRelease(
    string Version,
    Uri DownloadUrl,
    HashAlgorithmName HashAlgorithm,
    string ExpectedHash,
    bool IsZip);
