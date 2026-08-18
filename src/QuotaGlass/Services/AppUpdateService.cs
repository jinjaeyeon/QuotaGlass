using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaGlass.Services;

public sealed class AppUpdateService : IDisposable
{
    private const string RepositoryOwner = "jinjaeyeon";
    private const string RepositoryName = "QuotaGlass";
    private const string ExecutableName = "QuotaGlass.exe";
    private const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private const long MaximumChecksumBytes = 1024L * 1024;
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/jinjaeyeon/QuotaGlass/releases/latest";
    private const string UpdaterScript = """
        param(
            [Parameter(Mandatory=$true)][int]$ProcessId,
            [Parameter(Mandatory=$true)][string]$PackagePath,
            [Parameter(Mandatory=$true)][string]$TargetPath,
            [Parameter(Mandatory=$true)][string]$ScriptPath
        )

        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            do {
                $running = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
                if ($null -eq $running) {
                    break
                }

                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)

            if ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
                throw "QuotaGlass가 종료될 때까지 기다리지 못했습니다."
            }

            Move-Item -LiteralPath $PackagePath -Destination $TargetPath -Force
            Start-Process -FilePath $TargetPath
        }
        catch {
            # 업데이트에 실패해도 기존 설치본은 다시 시작합니다.
            try {
                Start-Process -FilePath $TargetPath
            }
            catch {
            }
        }
        finally {
            Remove-Item -LiteralPath $PackagePath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue
        }
        """;

    private static readonly HttpClient Client = CreateClient();
    private readonly Version _currentVersion;
    private readonly string? _targetExecutablePath;
    private readonly TimeSpan _checkInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private Task? _periodicCheck;
    private bool _isChecking;
    private bool _isPreparingUpdate;
    private bool _disposed;

    public AppUpdateService()
        : this(
            typeof(AppUpdateService).Assembly.GetName().Version ??
            new Version(0, 1, 0),
            Environment.ProcessPath,
            TimeSpan.FromHours(6))
    {
    }

    internal AppUpdateService(
        Version currentVersion,
        string? targetExecutablePath,
        TimeSpan checkInterval)
    {
        _currentVersion = currentVersion;
        _targetExecutablePath = targetExecutablePath;
        _checkInterval = checkInterval;
    }

    public event EventHandler? StateChanged;

    public Version CurrentVersion => _currentVersion;

    public AppUpdateInfo? AvailableUpdate { get; private set; }

    public bool IsChecking => _isChecking;

    public bool IsPreparingUpdate => _isPreparingUpdate;

    public bool CanSelfUpdate =>
        !string.IsNullOrWhiteSpace(_targetExecutablePath) &&
        File.Exists(_targetExecutablePath) &&
        string.Equals(
            Path.GetFileName(_targetExecutablePath),
            ExecutableName,
            StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSelfUpdate)
        {
            return;
        }

        _periodicCheck ??= RunPeriodicChecksAsync(_lifetime.Token);
    }

    public async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        if (!CanSelfUpdate)
        {
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _isChecking = true;
            StateChanged?.Invoke(this, EventArgs.Empty);

            var json = await Client.GetStringAsync(
                LatestReleaseUrl,
                cancellationToken);
            var release = ParseGitHubRelease(json);
            AvailableUpdate = release.Version > _currentVersion
                ? release
                : null;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 업데이트 확인 실패는 앱 사용을 방해하지 않습니다.
        }
        finally
        {
            _isChecking = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _operationLock.Release();
        }
    }

    public async Task<PreparedAppUpdate> PrepareUpdateAsync(
        CancellationToken cancellationToken)
    {
        if (!CanSelfUpdate || _targetExecutablePath is null)
        {
            throw new InvalidOperationException(
                "현재 실행 방식에서는 자동 업데이트를 사용할 수 없습니다.");
        }

        if (AvailableUpdate is not { } update ||
            update.Version <= _currentVersion)
        {
            throw new InvalidOperationException(
                "설치할 수 있는 새 버전이 없습니다.");
        }

        await _operationLock.WaitAsync(cancellationToken);
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            $"QuotaGlass-update-{Guid.NewGuid():N}.exe");
        try
        {
            _isPreparingUpdate = true;
            StateChanged?.Invoke(this, EventArgs.Empty);

            var expectedHash = update.ExpectedSha256 ??
                await DownloadExpectedHashAsync(update, cancellationToken);
            await DownloadAndVerifyAsync(
                update.DownloadUrl,
                expectedHash,
                stagingPath,
                cancellationToken,
                MaximumDownloadBytes);

            return new PreparedAppUpdate(
                update,
                stagingPath,
                _targetExecutablePath);
        }
        catch
        {
            DeleteIfExists(stagingPath);
            throw;
        }
        finally
        {
            _isPreparingUpdate = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _operationLock.Release();
        }
    }

    public void LaunchUpdater(PreparedAppUpdate update)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSelfUpdate ||
            _targetExecutablePath is null ||
            !string.Equals(
                update.TargetExecutablePath,
                _targetExecutablePath,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(update.StagedExecutablePath))
        {
            throw new InvalidOperationException(
                "업데이트 파일을 실행할 수 없습니다.");
        }

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"QuotaGlass-update-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(scriptPath, UpdaterScript, Encoding.UTF8);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(
                    _targetExecutablePath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-PackagePath");
            startInfo.ArgumentList.Add(update.StagedExecutablePath);
            startInfo.ArgumentList.Add("-TargetPath");
            startInfo.ArgumentList.Add(_targetExecutablePath!);
            startInfo.ArgumentList.Add("-ScriptPath");
            startInfo.ArgumentList.Add(scriptPath);

            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException(
                    "업데이트 보조 프로세스를 시작하지 못했습니다.");
            }
        }
        catch
        {
            DeleteIfExists(scriptPath);
            DeleteIfExists(update.StagedExecutablePath);
            throw;
        }
    }

    internal static AppUpdateInfo ParseGitHubRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tagName) ||
            !TryParseReleaseVersion(tagName, out var version))
        {
            throw new InvalidDataException(
                "GitHub 최신 릴리즈의 버전 태그가 올바르지 않습니다.");
        }

        var expectedAssetName = $"QuotaGlass-{version}-win-x64.exe";
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var executableAsset = assets.SingleOrDefault(asset =>
            string.Equals(
                asset.GetProperty("name").GetString(),
                expectedAssetName,
                StringComparison.OrdinalIgnoreCase));
        if (executableAsset.ValueKind == JsonValueKind.Undefined)
        {
            executableAsset = assets.SingleOrDefault(asset =>
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                return name.StartsWith("QuotaGlass-", StringComparison.OrdinalIgnoreCase) &&
                       name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase);
            });
        }

        if (executableAsset.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                $"GitHub 최신 릴리즈에서 {expectedAssetName}을 찾지 못했습니다.");
        }

        var assetName = executableAsset.GetProperty("name").GetString();
        var downloadUrl = ReadHttpsUri(
            executableAsset,
            "browser_download_url",
            "릴리즈 실행 파일 다운로드 주소");
        var expectedSha256 = TryReadSha256Digest(executableAsset);
        var checksumAssetName =
            Path.GetFileNameWithoutExtension(assetName!) + ".sha256";
        var checksumAsset = assets.SingleOrDefault(asset =>
            string.Equals(
                asset.GetProperty("name").GetString(),
                checksumAssetName,
                StringComparison.OrdinalIgnoreCase));
        var checksumDownloadUrl = checksumAsset.ValueKind == JsonValueKind.Undefined
            ? null
            : ReadHttpsUri(
                checksumAsset,
                "browser_download_url",
                "릴리즈 체크섬 다운로드 주소");
        var releasePageUrl = root.TryGetProperty("html_url", out var htmlUrl)
            ? ReadOptionalHttpsUri(htmlUrl.GetString())
            : null;
        releasePageUrl ??= new Uri(
            $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/tag/" +
            Uri.EscapeDataString(tagName));

        if (expectedSha256 is null && checksumDownloadUrl is null)
        {
            throw new InvalidDataException(
                "GitHub 최신 릴리즈에 SHA-256 검증 정보가 없습니다.");
        }

        return new AppUpdateInfo(
            tagName,
            version,
            releasePageUrl,
            downloadUrl,
            checksumDownloadUrl,
            assetName!,
            expectedSha256);
    }

    internal static string ParseChecksumFile(
        string checksumText,
        string assetName)
    {
        foreach (var line in checksumText.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Trim().Split(
                [' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2 ||
                !IsSha256(columns[0]))
            {
                continue;
            }

            var fileName = columns[1].TrimStart('*');
            if (string.Equals(
                    Path.GetFileName(fileName),
                    assetName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return columns[0].ToLowerInvariant();
            }
        }

        throw new InvalidDataException(
            $"체크섬 파일에서 {assetName}의 SHA-256을 찾지 못했습니다.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _operationLock.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunPeriodicChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckForUpdateAsync(cancellationToken);
            using var timer = new PeriodicTimer(_checkInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await CheckForUpdateAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // 다음 앱 실행 때 다시 확인합니다.
        }
    }

    private static async Task<string> DownloadExpectedHashAsync(
        AppUpdateInfo update,
        CancellationToken cancellationToken)
    {
        if (update.ChecksumDownloadUrl is not { } checksumUrl)
        {
            throw new InvalidDataException(
                "릴리즈 체크섬 다운로드 주소가 없습니다.");
        }

        using var response = await Client.GetAsync(
            checksumUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumChecksumBytes)
        {
            throw new InvalidDataException("릴리즈 체크섬 파일이 너무 큽니다.");
        }

        var checksumText = await response.Content.ReadAsStringAsync(
            cancellationToken);
        if (checksumText.Length > MaximumChecksumBytes)
        {
            throw new InvalidDataException("릴리즈 체크섬 파일이 너무 큽니다.");
        }

        return ParseChecksumFile(checksumText, update.AssetName);
    }

    private static async Task DownloadAndVerifyAsync(
        Uri downloadUrl,
        string expectedSha256,
        string destinationPath,
        CancellationToken cancellationToken,
        long maximumBytes)
    {
        if (!IsSha256(expectedSha256))
        {
            throw new InvalidDataException("릴리즈 SHA-256 형식이 올바르지 않습니다.");
        }

        using var response = await Client.GetAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > maximumBytes)
        {
            throw new InvalidDataException("업데이트 파일이 허용 크기를 초과했습니다.");
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
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException(
                    "업데이트 파일이 허용 크기를 초과했습니다.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "업데이트 파일의 SHA-256이 릴리즈 체크섬과 다릅니다.");
        }
    }

    private static Uri ReadHttpsUri(
        JsonElement element,
        string propertyName,
        string description)
    {
        var value = element.GetProperty(propertyName).GetString();
        var uri = ReadOptionalHttpsUri(value);
        return uri ?? throw new InvalidDataException($"{description}가 올바르지 않습니다.");
    }

    private static Uri? ReadOptionalHttpsUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri;
    }

    private static string? TryReadSha256Digest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digest) ||
            digest.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = digest.GetString();
        if (value?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        var hash = value["sha256:".Length..];
        return IsSha256(hash) ? hash.ToLowerInvariant() : null;
    }

    private static bool TryParseReleaseVersion(
        string tagName,
        out Version version)
    {
        var value = tagName.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var prereleaseSeparator = value.IndexOf('-');
        if (prereleaseSeparator >= 0)
        {
            value = value[..prereleaseSeparator];
        }

        return Version.TryParse(value, out version!) &&
               version.Major >= 0 &&
               version.Minor >= 0;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or
            >= 'a' and <= 'f' or
            >= 'A' and <= 'F');

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}

public sealed record AppUpdateInfo(
    string TagName,
    Version Version,
    Uri ReleasePageUrl,
    Uri DownloadUrl,
    Uri? ChecksumDownloadUrl,
    string AssetName,
    string? ExpectedSha256)
{
    public string DisplayVersion => Version.ToString();
}

public sealed record PreparedAppUpdate(
    AppUpdateInfo Release,
    string StagedExecutablePath,
    string TargetExecutablePath);
