using System.Text.Json;
using System.IO;

namespace QuotaGlass.Services;

public static class ManagedCliStore
{
    private const string ToolsDirectoryOverride =
        "QUOTAGLASS_TOOLS_DIRECTORY";
    private const string ManifestFileName = "current.json";

    public static string RootDirectory =>
        Environment.GetEnvironmentVariable(ToolsDirectoryOverride)
        ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "QuotaGlass",
            "tools");

    public static string? FindExecutable(string providerId) =>
        FindExecutable(RootDirectory, providerId);

    internal static string? FindExecutable(
        string rootDirectory,
        string providerId)
    {
        var providerDirectory = Path.Combine(rootDirectory, providerId);
        var manifestPath = Path.Combine(providerDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize<ManagedCliManifest>(stream);
            if (manifest is null ||
                string.IsNullOrWhiteSpace(manifest.Executable))
            {
                return null;
            }

            var candidate = Path.GetFullPath(
                Path.Combine(providerDirectory, manifest.Executable));
            var providerRoot = Path.GetFullPath(providerDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return candidate.StartsWith(
                       providerRoot,
                       StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(candidate)
                ? candidate
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                NotSupportedException)
        {
            return null;
        }
    }

    internal static void Activate(
        string rootDirectory,
        string providerId,
        string version,
        string executablePath)
    {
        var providerDirectory = Path.Combine(rootDirectory, providerId);
        var relativeExecutable = Path.GetRelativePath(
            providerDirectory,
            executablePath);
        if (relativeExecutable.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "관리 CLI 실행 파일이 공급자 디렉터리 밖에 있습니다.");
        }

        Directory.CreateDirectory(providerDirectory);
        var manifestPath = Path.Combine(providerDirectory, ManifestFileName);
        var temporaryPath = manifestPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new ManagedCliManifest(version, relativeExecutable),
                    new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record ManagedCliManifest(
        string Version,
        string Executable);
}
