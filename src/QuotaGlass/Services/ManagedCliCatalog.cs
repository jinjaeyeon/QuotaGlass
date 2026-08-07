using System.Runtime.InteropServices;

namespace QuotaGlass.Services;

public static class ManagedCliCatalog
{
    private static readonly IReadOnlyList<ManagedCliDefinition> Definitions =
    [
        new(
            "codex",
            "Codex",
            ManagedCliSource.GitHubRelease,
            "https://api.github.com/repos/openai/codex/releases/latest",
            RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "codex-aarch64-pc-windows-msvc.exe.zip"
                : "codex-x86_64-pc-windows-msvc.exe.zip",
            RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "codex-aarch64-pc-windows-msvc.exe"
                : "codex-x86_64-pc-windows-msvc.exe",
            "codex.exe"),
        new(
            "claude-code",
            "Claude Code",
            ManagedCliSource.ClaudeRelease,
            "https://downloads.claude.ai/claude-code-releases",
            RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "win32-arm64"
                : "win32-x64",
            "claude.exe",
            "claude.exe"),
        new(
            "github-copilot",
            "GitHub Copilot",
            ManagedCliSource.GitHubRelease,
            "https://api.github.com/repos/github/copilot-cli/releases/latest",
            RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "copilot-win32-arm64.zip"
                : "copilot-win32-x64.zip",
            "copilot.exe",
            "copilot.exe"),
        new(
            "antigravity",
            "Antigravity",
            ManagedCliSource.AntigravityRelease,
            "https://antigravity-cli-auto-updater-974169037036.us-central1.run.app",
            RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "windows_arm64"
                : "windows_amd64",
            "agy.exe",
            "agy.exe")
    ];

    public static IReadOnlyList<ManagedCliDefinition> All => Definitions;

    public static ManagedCliDefinition Find(string providerId) =>
        Definitions.SingleOrDefault(definition =>
            definition.ProviderId.Equals(
                providerId,
                StringComparison.Ordinal))
        ?? throw new ArgumentException(
            $"관리 설치를 지원하지 않는 공급자입니다: {providerId}",
            nameof(providerId));
}

public sealed record ManagedCliDefinition(
    string ProviderId,
    string DisplayName,
    ManagedCliSource Source,
    string MetadataUrl,
    string AssetName,
    string ExecutableInPackage,
    string InstalledExecutableName);

public enum ManagedCliSource
{
    GitHubRelease,
    ClaudeRelease,
    AntigravityRelease
}
