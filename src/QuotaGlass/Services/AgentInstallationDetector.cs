using System.Diagnostics;
using System.IO;
using System.Linq;

namespace QuotaGlass.Services;

public sealed class AgentInstallationDetector
{
    public IReadOnlyList<AgentInstallation> Detect()
    {
        var installations = new List<AgentInstallation>();

        AddCommandLineAgent(
            installations,
            "claude-code",
            "Claude Code",
            "C",
            "claude");

        AddCommandLineAgent(
            installations,
            "codex",
            "Codex",
            "<>",
            "codex");

        AddCommandLineAgent(
            installations,
            "github-copilot",
            "GitHub Copilot",
            "GH",
            "copilot");

        AddDesktopAgent(
            installations,
            "antigravity",
            "Antigravity",
            "✦",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "antigravity",
                "Antigravity.exe"));

        AddCursor(installations);

        if (IsJetBrainsAiInstalled())
        {
            installations.Add(
                new AgentInstallation(
                    "jetbrains",
                    "JetBrains AI",
                    "JB",
                    "설치됨",
                    null,
                    null,
                    FindLatestJetBrainsQuotaState()));
        }

        return installations;
    }

    private static void AddCommandLineAgent(
        ICollection<AgentInstallation> installations,
        string providerId,
        string displayName,
        string iconText,
        string command)
    {
        var path = FindOnPath(command);
        if (path is null)
        {
            return;
        }

        installations.Add(
            new AgentInstallation(
                providerId,
                displayName,
                iconText,
                "설치됨",
                path,
                ReadVersion(path)));
    }

    private static void AddDesktopAgent(
        ICollection<AgentInstallation> installations,
        string providerId,
        string displayName,
        string iconText,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        installations.Add(
            new AgentInstallation(
                providerId,
                displayName,
                iconText,
                "설치됨",
                path,
                ReadVersion(path!)));
    }

    private static void AddCursor(
        ICollection<AgentInstallation> installations)
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var executablePath = FindFirstExisting(
            FindOnPath("cursor-agent"),
            FindOnPath("cursor"),
            Path.Combine(localAppData, "cursor-agent", "cursor-agent.cmd"),
            Path.Combine(localAppData, "Programs", "cursor", "Cursor.exe"),
            Path.Combine(localAppData, "Programs", "Cursor", "Cursor.exe"));
        var authPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor",
            "auth.json");

        if (executablePath is null && !File.Exists(authPath))
        {
            return;
        }

        installations.Add(
            new AgentInstallation(
                "cursor",
                "Cursor",
                "↖",
                "설치됨",
                executablePath,
                executablePath is null ? null : ReadVersion(executablePath),
                File.Exists(authPath) ? authPath : null));
    }

    private static string? FindOnPath(string command)
    {
        var extensions = new[] { ".exe", ".cmd", ".bat", string.Empty };
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in pathEntries)
        {
            var directory = entry.Trim().Trim('"');
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string? FindFirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate =>
            candidate is not null && File.Exists(candidate));

    private static string? ReadVersion(string executablePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsJetBrainsAiInstalled()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JetBrains");

        if (!Directory.Exists(root))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateDirectories(
                    root,
                    "ml-llm",
                    SearchOption.AllDirectories)
                .Any(path => path.Contains(
                    $"{Path.DirectorySeparatorChar}plugins{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string? FindLatestJetBrainsQuotaState()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JetBrains");

        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(
                    root,
                    "AIAssistantQuotaManager2.xml",
                    SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
