using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class JetBrainsAiUsageProvider(
    AgentInstallation installation) : IUsageProvider
{
    public string ProviderId => installation.ProviderId;
    public string DisplayName => installation.DisplayName;
    public string IconText => installation.IconText;
    public string AccountLabel => installation.AccountLabel;

    public async Task<UsageSnapshot> FetchAsync(
        CancellationToken cancellationToken)
    {
        var candidates = FindQuotaStateCandidates(
            installation.UsageStatePath);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "JetBrains AI quota 상태 파일을 찾을 수 없습니다.");
        }

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ParseState(candidate);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    InvalidOperationException or
                    FormatException or
                    XmlException or
                    JsonException)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException(
            "JetBrains AI quota 캐시를 읽을 수 없습니다.",
            lastError);
    }

    internal static IReadOnlyList<string> FindQuotaStateCandidates(
        string? preferredPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredPath) &&
            File.Exists(preferredPath))
        {
            candidates.Add(Path.GetFullPath(preferredPath));
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JetBrains");
        if (Directory.Exists(root))
        {
            try
            {
                candidates.AddRange(
                    Directory.EnumerateFiles(
                            root,
                            "AIAssistantQuotaManager2.xml",
                            SearchOption.AllDirectories)
                        .Where(File.Exists)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .Select(Path.GetFullPath));
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private UsageSnapshot ParseState(string statePath)
    {
        var document = XDocument.Load(statePath);
        var component = document.Descendants("component")
            .Single(element =>
                (string?)element.Attribute("name") ==
                "AIAssistantQuotaManager2");
        var quotaJson = ReadOption(component, "quotaInfo");
        var refillJson = ReadOption(component, "nextRefill");

        using var quotaDocument = JsonDocument.Parse(quotaJson);
        using var refillDocument = JsonDocument.Parse(refillJson);

        var tariffQuota = quotaDocument.RootElement
            .GetProperty("tariffQuota");
        var remaining = ParseDecimal(
            tariffQuota.GetProperty("available").GetString());
        var maximum = ParseDecimal(
            tariffQuota.GetProperty("maximum").GetString());

        var refillRoot = refillDocument.RootElement;
        var resetsAt = DateTimeOffset.Parse(
            refillRoot.GetProperty("next").GetString()
                ?? throw new InvalidOperationException(
                    "JetBrains AI refill 시각이 없습니다."),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        var durationText = refillRoot
            .GetProperty("tariff")
            .GetProperty("duration")
            .GetString();
        var duration = durationText is null
            ? TimeSpan.FromDays(30)
            : XmlConvert.ToTimeSpan(durationText);
        var observedAt = File.GetLastWriteTimeUtc(statePath);

        return new UsageSnapshot(
            ProviderId,
            DisplayName,
            IconText,
            "개인 · 월간 · IDE 캐시",
            [
                new UsageMeter(
                    "jetbrains-monthly-credits",
                    "월간",
                    (double)remaining,
                    (double)maximum,
                    "credits",
                    resetsAt - duration,
                    resetsAt)
            ],
            observedAt,
            "JetBrains AI quota cache");
    }

    private static string ReadOption(XElement component, string name) =>
        component.Elements("option")
            .Single(element => (string?)element.Attribute("name") == name)
            .Attribute("value")?
            .Value
        ?? throw new InvalidOperationException(
            $"JetBrains AI {name} 값이 없습니다.");

    private static decimal ParseDecimal(string? value) =>
        decimal.Parse(
            value ?? throw new InvalidOperationException(
                "JetBrains AI quota 숫자가 없습니다."),
            NumberStyles.Number,
            CultureInfo.InvariantCulture);
}
