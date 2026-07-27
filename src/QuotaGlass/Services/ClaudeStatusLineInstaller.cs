using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaGlass.Services;

public static class ClaudeStatusLineInstaller
{
    private const string BridgeMarker = "--claude-statusline-bridge";
    private const string BridgeScriptName = "claude-statusline-bridge.ps1";
    private const string BridgeConfigurationName =
        "claude-statusline-bridge.json";

    private const string BridgeScript =
        """
        $stateDirectory = if ([string]::IsNullOrWhiteSpace($env:QUOTAGLASS_STATE_DIRECTORY)) {
            Join-Path $env:LOCALAPPDATA 'QuotaGlass'
        } else {
            $env:QUOTAGLASS_STATE_DIRECTORY
        }
        $inputJson = [Console]::In.ReadToEnd()
        $payload = $null

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

        if ($null -ne $payload) {
            $segments = @(
                $payload.model.display_name,
                $(if ([string]::IsNullOrWhiteSpace($payload.workspace.current_dir)) {
                    $null
                } else {
                    Split-Path -Leaf $payload.workspace.current_dir
                })
            ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            if ($segments.Count -gt 0) {
                Write-Output ($segments -join ' | ')
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
        var bridgeConfigurationPath = Path.Combine(
            stateDirectory,
            BridgeConfigurationName);

        if (string.IsNullOrWhiteSpace(currentCommand))
        {
            // status line이 없으면 사용량 페이로드가 전달되지 않으므로 직접 설치한다.
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
                BridgeScriptName,
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

        var bridgeConfiguration = new JsonObject
        {
            ["originalCommand"] = currentCommand
        };
        WriteJsonAtomically(bridgeConfigurationPath, bridgeConfiguration);

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
