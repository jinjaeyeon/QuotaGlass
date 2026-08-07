using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGlass.Services;

public static class ClaudeStatusLineInstaller
{
    internal const string BridgeMarker = "--claude-statusline-bridge";
    private const string LegacyBridgeScriptName =
        "claude-statusline-bridge.ps1";
    private const string BridgeConfigurationName =
        "claude-statusline-bridge.json";

    public static void EnsureInstalled()
    {
        var settingsPath =
            Environment.GetEnvironmentVariable(
                "QUOTAGLASS_CLAUDE_SETTINGS_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                ".claude",
                "settings.json");
        var settingsExist = File.Exists(settingsPath);
        var root = settingsExist
            ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject
            : new JsonObject();
        if (root is null)
        {
            return;
        }

        var statusLine = root["statusLine"] as JsonObject;
        var currentCommand = statusLine?["command"] is JsonValue commandValue &&
                             commandValue.TryGetValue<string>(out var command)
            ? command
            : null;

        var stateDirectory = ClaudeStatusLineBridge.StateDirectory;
        Directory.CreateDirectory(stateDirectory);
        var bridgeConfigurationPath = Path.Combine(
            stateDirectory,
            BridgeConfigurationName);
        var executablePath =
            Environment.GetEnvironmentVariable(
                "QUOTAGLASS_EXECUTABLE_PATH")
            ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var bridgeCommand = $"\"{executablePath}\" {BridgeMarker}";

        if (string.IsNullOrWhiteSpace(currentCommand))
        {
            BackupSettings(settingsPath, settingsExist);
            File.Delete(bridgeConfigurationPath);
            if (statusLine is null)
            {
                statusLine = [];
                root["statusLine"] = statusLine;
            }

            statusLine["type"] = "command";
            statusLine["command"] = bridgeCommand;
            WriteJsonAtomically(settingsPath, root);
            return;
        }

        var isExistingBridge =
            currentCommand.Contains(
                BridgeMarker,
                StringComparison.OrdinalIgnoreCase) ||
            currentCommand.Contains(
                LegacyBridgeScriptName,
                StringComparison.OrdinalIgnoreCase);
        if (isExistingBridge)
        {
            if (!string.Equals(
                    currentCommand,
                    bridgeCommand,
                    StringComparison.Ordinal))
            {
                statusLine!["command"] = bridgeCommand;
                WriteJsonAtomically(settingsPath, root);
            }

            return;
        }

        WriteJsonAtomically(
            bridgeConfigurationPath,
            new JsonObject { ["originalCommand"] = currentCommand });

        BackupSettings(settingsPath, settingsExist);
        statusLine!["command"] = bridgeCommand;
        WriteJsonAtomically(settingsPath, root);
    }

    private static void BackupSettings(string settingsPath, bool settingsExist)
    {
        if (!settingsExist)
        {
            return;
        }

        var backupPath = settingsPath + ".quotaglass.bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(settingsPath, backupPath);
        }
    }

    private static void WriteJsonAtomically(string path, JsonNode value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "설정 파일의 상위 폴더를 찾을 수 없습니다.");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(
            directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(
            temporary,
            value.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
