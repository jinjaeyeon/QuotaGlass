using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace QuotaGlass.Services;

public static class ClaudeStatusLineBridge
{
    private const string CacheFileName = "claude-rate-limits.json";
    private const string ConfigurationFileName =
        "claude-statusline-bridge.json";

    internal static string StateDirectory =>
        Environment.GetEnvironmentVariable(
            "QUOTAGLASS_STATE_DIRECTORY")
        ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "QuotaGlass");

    public static int Run() =>
        Run(
            new StreamReader(
                Console.OpenStandardInput(),
                Console.InputEncoding,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true),
            new StreamWriter(
                Console.OpenStandardOutput(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true)
            {
                AutoFlush = true
            },
            StateDirectory);

    internal static int Run(
        TextReader input,
        TextWriter output,
        string stateDirectory)
    {
        var inputJson = input.ReadToEnd();
        JsonDocument? payload = null;
        try
        {
            payload = JsonDocument.Parse(inputJson);
            if (payload.RootElement.TryGetProperty("rate_limits", out _))
            {
                WriteCacheAtomically(stateDirectory, inputJson);
            }
        }
        catch (JsonException)
        {
            // Status-line rendering must continue with malformed input.
        }
        catch (IOException)
        {
            // A cache write failure must not break Claude's status line.
        }
        catch (UnauthorizedAccessException)
        {
            // A cache write failure must not break Claude's status line.
        }

        try
        {
            var originalCommand = ReadOriginalCommand(stateDirectory);
            if (!string.IsNullOrWhiteSpace(originalCommand))
            {
                return RunOriginalCommand(originalCommand, inputJson, output);
            }

            if (payload is not null)
            {
                WriteDefaultStatus(payload.RootElement, output);
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            payload?.Dispose();
        }
    }

    private static void WriteCacheAtomically(
        string stateDirectory,
        string inputJson)
    {
        Directory.CreateDirectory(stateDirectory);
        var destination = Path.Combine(stateDirectory, CacheFileName);
        var temporary = Path.Combine(
            stateDirectory,
            $"claude-rate-limits.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporary,
                inputJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string? ReadOriginalCommand(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, ConfigurationFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty(
            "originalCommand",
            out var command)
            ? command.GetString()
            : null;
    }

    private static int RunOriginalCommand(
        string originalCommand,
        string inputJson,
        TextWriter output)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC")
                       ?? "cmd.exe",
            Arguments = $"/d /s /c \"{originalCommand}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "기존 Claude status-line 명령을 시작하지 못했습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(inputJson);
        process.StandardInput.Close();
        process.WaitForExit();
        output.Write(outputTask.GetAwaiter().GetResult());
        _ = errorTask.GetAwaiter().GetResult();
        return process.ExitCode;
    }

    private static void WriteDefaultStatus(
        JsonElement payload,
        TextWriter output)
    {
        var segments = new List<string>();
        if (payload.TryGetProperty("model", out var model) &&
            model.TryGetProperty("display_name", out var displayName) &&
            !string.IsNullOrWhiteSpace(displayName.GetString()))
        {
            segments.Add(displayName.GetString()!);
        }

        if (payload.TryGetProperty("workspace", out var workspace) &&
            workspace.TryGetProperty("current_dir", out var currentDirectory) &&
            !string.IsNullOrWhiteSpace(currentDirectory.GetString()))
        {
            segments.Add(
                Path.GetFileName(
                    currentDirectory.GetString()!
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar)));
        }

        if (segments.Count > 0)
        {
            output.WriteLine(string.Join(" | ", segments));
        }
    }
}
