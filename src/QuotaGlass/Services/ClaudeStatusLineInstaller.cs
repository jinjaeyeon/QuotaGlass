using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGlass.Services;

public static class ClaudeStatusLineInstaller
{
    private const string BridgeMarker = "--claude-statusline-bridge";
    private const string BridgeScriptName = "claude-statusline-bridge.ps1";

    private const string BridgeScript =
        """
        $stateDirectory = if ([string]::IsNullOrWhiteSpace($env:QUOTAGLASS_STATE_DIRECTORY)) {
            Join-Path $env:LOCALAPPDATA 'QuotaGlass'
        } else {
            $env:QUOTAGLASS_STATE_DIRECTORY
        }
        $inputJson = [Console]::In.ReadToEnd()

        try {
            $payload = $inputJson | ConvertFrom-Json
            if ($null -ne $payload.rate_limits) {
                New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
                $destination = Join-Path $stateDirectory 'claude-rate-limits.json'
                $temporary = Join-Path $stateDirectory ("claude-rate-limits.{0}.tmp" -f [guid]::NewGuid().ToString('N'))
                [IO.File]::WriteAllText($temporary, $inputJson, [Text.UTF8Encoding]::new($false))
                Move-Item -LiteralPath $temporary -Destination $destination -Force
            }
        } catch {
            # Status-line rendering must continue even if the quota cache fails.
        }

        $configurationPath = Join-Path $stateDirectory 'claude-statusline-bridge.json'
        if (Test-Path -LiteralPath $configurationPath) {
            try {
                $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
                if (-not [string]::IsNullOrWhiteSpace($configuration.originalCommand)) {
                    $inputJson | & $env:COMSPEC /d /s /c $configuration.originalCommand
                    exit $LASTEXITCODE
                }
            } catch {
                exit 0
            }
        }
        exit 0
        """;

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
        if (!File.Exists(settingsPath))
        {
            return;
        }

        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        var statusLine = root?["statusLine"] as JsonObject;
        var currentCommand = statusLine?["command"]?.GetValue<string>();
        if (root is null ||
            statusLine is null ||
            string.IsNullOrWhiteSpace(currentCommand))
        {
            return;
        }

        var stateDirectory =
            Environment.GetEnvironmentVariable(
                "QUOTAGLASS_STATE_DIRECTORY")
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "QuotaGlass");
        Directory.CreateDirectory(stateDirectory);
        var bridgeScriptPath = Path.Combine(
            stateDirectory,
            BridgeScriptName);
        WriteTextAtomically(bridgeScriptPath, BridgeScript);

        var bridgeCommand =
            $"powershell.exe -NoProfile -NonInteractive " +
            $"-ExecutionPolicy Bypass -File \"{bridgeScriptPath}\"";
        var isExistingBridge =
            currentCommand.Contains(
                BridgeMarker,
                StringComparison.OrdinalIgnoreCase) ||
            currentCommand.Contains(
                BridgeScriptName,
                StringComparison.OrdinalIgnoreCase);
        if (isExistingBridge)
        {
            if (!string.Equals(
                    currentCommand,
                    bridgeCommand,
                    StringComparison.Ordinal))
            {
                statusLine["command"] = bridgeCommand;
                WriteJsonAtomically(settingsPath, root);
            }

            return;
        }

        var bridgeConfiguration = new JsonObject
        {
            ["originalCommand"] = currentCommand
        };
        WriteJsonAtomically(
            Path.Combine(
                stateDirectory,
                "claude-statusline-bridge.json"),
            bridgeConfiguration);

        var backupPath = settingsPath + ".quotaglass.bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(settingsPath, backupPath);
        }

        statusLine["command"] = bridgeCommand;
        WriteJsonAtomically(settingsPath, root);
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
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        File.Move(temporary, path, true);
    }

    private static void WriteTextAtomically(string path, string value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "브리지 파일의 상위 폴더를 찾을 수 없습니다.");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(
            directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, value);
        File.Move(temporary, path, true);
    }
}
