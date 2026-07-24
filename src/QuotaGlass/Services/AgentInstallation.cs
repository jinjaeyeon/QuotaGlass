namespace QuotaGlass.Services;

public sealed record AgentInstallation(
    string ProviderId,
    string DisplayName,
    string IconText,
    string AccountLabel,
    string? ExecutablePath,
    string? Version,
    string? UsageStatePath = null);
