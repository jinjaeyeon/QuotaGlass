using System.Linq;

namespace QuotaGlass.Services;

public static class UsageProviderFactory
{
    public static IReadOnlyList<IUsageProvider> CreateInstalledProviders()
    {
        var detector = new AgentInstallationDetector();

        return detector.Detect()
            .Select(CreateProvider)
            .ToArray();
    }

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
            "antigravity" =>
                new AntigravityUsageProvider(installation),
            _ => new PendingUsageProvider(installation)
        };
}
