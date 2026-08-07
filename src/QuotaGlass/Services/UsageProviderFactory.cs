using System.Linq;

namespace QuotaGlass.Services;

public static class UsageProviderFactory
{
    public static IReadOnlyList<IUsageProvider> CreateInstalledProviders()
    {
        var detector = new AgentInstallationDetector();

        return CreateProviders(detector.Detect());
    }

    public static IReadOnlyList<IUsageProvider> CreateProviders(
        IReadOnlyList<AgentInstallation> installations) =>
        installations
            .Select(CreateProvider)
            .ToArray();

    private static IUsageProvider CreateProvider(
        AgentInstallation installation) =>
        installation.ProviderId switch
        {
            "codex" when installation.ExecutablePath is not null =>
                new CodexAppServerUsageProvider(installation),
            "jetbrains" when installation.UsageStatePath is not null =>
                new JetBrainsAiUsageProvider(installation),
            "claude-code" =>
                new ClaudeCodeUsageProvider(installation),
            "github-copilot" when installation.ExecutablePath is not null =>
                new GitHubCopilotUsageProvider(installation),
            "cursor" when installation.UsageStatePath is not null =>
                new CursorUsageProvider(installation),
            "antigravity" =>
                new AntigravityUsageProvider(installation),
            _ => new PendingUsageProvider(installation)
        };
}
