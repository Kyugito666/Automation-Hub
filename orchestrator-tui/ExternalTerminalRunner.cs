using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Spectre.Console;
using System.Text;
using System.Linq;

namespace Orchestrator;

public static class ExternalTerminalRunner
{
    public static void RunBotInExternalTerminal(string botPath, string executor, string args, string? inputFile = null, bool recordMode = false)
    {
        var scriptPath = CreateRunnerScript(botPath, executor, args, inputFile, recordMode);
        
        if (string.IsNullOrEmpty(scriptPath))
        {
            AnsiConsole.MarkupLine("[red]Failed to create runner script[/]");
            return;
        }

        LaunchInTerminal(scriptPath, botPath);
        
        if (recordMode)
        {
            AnsiConsole.MarkupLine("[green]✓ Bot launched in RECORD mode[/]");
            AnsiConsole.MarkupLine("[yellow]Your inputs will be saved for GitHub Actions replay[/]");
        }
        else if (inputFile != null)
        {
            AnsiConsole.MarkupLine("[green]✓ Bot launched with auto-input[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]✓ Bot launched in manual mode[/]");
        }
        
        AnsiConsole.MarkupLine("[dim]Interact with the bot in the new window[/]");
    }

    private static string? CreateRunnerScript(string botPath, string executor, string args, string? inputFile, bool recordMode)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return CreateWindowsPowerShellScript(botPath, executor, args, inputFile, recordMode);
            }
            else
            {
                return CreateLinuxScript(botPath, executor, args, inputFile, recordMode);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating script: {ex.Message}[/]");
            return null;
        }
    }

    private static string CreateWindowsPowerShellScript(string botPath, string executor, string args, string? inputFile, bool recordMode)
    {
        var ps1Path = Path.Combine(botPath, "_run_bot.ps1");
        var utf8WithoutBom = new UTF8Encoding(false);
        
        using (var writer = new StreamWriter(ps1Path, false, utf8WithoutBom))
        {
            writer.WriteLine("# PowerShell Bot Runner - UTF-8 Support");
            writer.WriteLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine("[Console]::InputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine();
            writer.WriteLine($"Set-Location -Path \"{botPath}\"");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine($"Write-Host \"Running: {Path.GetFileName(botPath)}\" -ForegroundColor White");
            
            if (recordMode)
            {
                writer.WriteLine("Write-Host \"MODE: RECORDING INPUTS\" -ForegroundColor Yellow");
                writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
                writer.WriteLine("Write-Host \"\" ");
                writer.WriteLine("Write-Host \"WARNING: PowerShell cannot capture stdin natively\" -ForegroundColor Red");
                writer.WriteLine("Write-Host \"Please create answer file manually after testing\" -ForegroundColor Yellow");
                writer.WriteLine($"Write-Host \"Target: {Path.Combine("..", ".bot-inputs", $"{SanitizeBotName(Path.GetFileName(botPath))}.json")}\" -ForegroundColor Gray");
                writer.WriteLine("Write-Host \"\" ");
            }
            else if (!string.IsNullOrEmpty(inputFile) && File.Exists(inputFile))
            {
                writer.WriteLine("Write-Host \"MODE: AUTO-INPUT\" -ForegroundColor Green");
            }
            else
            {
                writer.WriteLine("Write-Host \"MODE: MANUAL\" -ForegroundColor White");
            }
            
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine("Write-Host \"\" ");
            writer.WriteLine();

            // Build command
            string executablePath = executor;
            bool useNpmStart = executor.Contains("npm") && args == "start";
            
            if (useNpmStart)
            {
                executablePath = "node";
                args = "index.js";
            }

            if (recordMode)
            {
                // Record mode: Run tanpa input redirect
                writer.WriteLine("try {");
                writer.WriteLine($"    & \"{executablePath}\" {args}");
                writer.WriteLine("    $exitCode = $LASTEXITCODE");
                writer.WriteLine("} catch {");
                writer.WriteLine("    Write-Host \"Error: $_\" -ForegroundColor Red");
                writer.WriteLine("    $exitCode = 1");
                writer.WriteLine("}");
            }
            else if (!string.IsNullOrEmpty(inputFile) && File.Exists(inputFile))
            {
                // Auto-input mode
                var inputTextFile = ConvertJsonToTextInput(inputFile, botPath);
                
                if (!string.IsNullOrEmpty(inputTextFile))
                {
                    writer.WriteLine("try {");
                    writer.WriteLine($"    Get-Content \"{inputTextFile}\" | & \"{executablePath}\" {args}");
                    writer.WriteLine("    $exitCode = $LASTEXITCODE");
                    writer.WriteLine("} catch {");
                    writer.WriteLine("    Write-Host \"Error: $_\" -ForegroundColor Red");
                    writer.WriteLine("    $exitCode = 1");
                    writer.WriteLine("}");
                }
                else
                {
                    // Fallback ke manual
                    writer.WriteLine("Write-Host \"Warning: Could not process input file, running manually\" -ForegroundColor Yellow");
                    writer.WriteLine("try {");
                    writer.WriteLine($"    & \"{executablePath}\" {args}");
                    writer.WriteLine("    $exitCode = $LASTEXITCODE");
                    writer.WriteLine("} catch {");
                    writer.WriteLine("    Write-Host \"Error: $_\" -ForegroundColor Red");
                    writer.WriteLine("    $exitCode = 1");
                    writer.WriteLine("}");
                }
            }
            else
            {
                // Manual mode
                writer.WriteLine("try {");
                writer.WriteLine($"    & \"{executablePath}\" {args}");
                writer.WriteLine("    $exitCode = $LASTEXITCODE");
                writer.WriteLine("} catch {");
                writer.WriteLine("    Write-Host \"Error: $_\" -ForegroundColor Red");
                writer.WriteLine("    $exitCode = 1");
                writer.WriteLine("}");
            }
            
            writer.WriteLine();
            writer.WriteLine("Write-Host \"\" ");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine("Write-Host \"Bot finished (Exit Code: $exitCode)\" -ForegroundColor $(if ($exitCode -eq 0) { 'Green' } else { 'Red' })");
            
            if (recordMode)
            {
                writer.WriteLine("Write-Host \"\" ");
                writer.WriteLine("Write-Host \"Next steps:\" -ForegroundColor Yellow");
                writer.WriteLine($"Write-Host \"1. Create JSON file at: {Path.Combine("..", ".bot-inputs", $"{SanitizeBotName(Path.GetFileName(botPath))}.json")}\" -ForegroundColor Gray");
                writer.WriteLine("Write-Host \"2. Format: { \\\"key1\\\": \\\"value1\\\", \\\"key2\\\": \\\"value2\\\" }\" -ForegroundColor Gray");
            }
            
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine("Write-Host \"Press any key to close...\" -ForegroundColor Gray");
            writer.WriteLine("[void][System.Console]::ReadKey($true)");
        }
        
        AnsiConsole.MarkupLine($"[dim]Created PowerShell script: {Path.GetFileName(ps1Path)}[/]");
        return ps1Path;
    }

    private static string CreateLinuxScript(string botPath, string executor, string args, string? inputFile, bool recordMode)
    {
        var shellPath = Path.Combine(botPath, "_run_bot.sh");
        var utf8WithoutBom = new UTF8Encoding(false);
        
        using (var writer = new StreamWriter(shellPath, false, utf8WithoutBom))
        {
            writer.WriteLine("#!/bin/bash");
            writer.WriteLine($"cd \"{botPath}\"");
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine($"echo \"Running: {Path.GetFileName(botPath)}\"");
            
            if (recordMode)
            {
                writer.WriteLine("echo \"MODE: RECORDING INPUTS\"");
            }
            else if (inputFile != null)
            {
                writer.WriteLine("echo \"MODE: AUTO-INPUT\"");
            }
            else
            {
                writer.WriteLine("echo \"MODE: MANUAL\"");
            }
            
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine();

            bool useNpmStart = executor.Contains("npm") && args == "start";
            string actualExecutor = useNpmStart ? "node" : executor;
            string actualArgs = useNpmStart ? "index.js" : args;

            if (recordMode)
            {
                var recordFile = Path.Combine(botPath, "_input_record.log");
                writer.WriteLine($"echo \"Recording session to: {Path.GetFileName(recordFile)}\"");
                writer.WriteLine();
                writer.WriteLine($"script -q -c '\"{actualExecutor}\" {actualArgs}' \"{recordFile}\"");
                writer.WriteLine();
                writer.WriteLine("echo \"\"");
                writer.WriteLine("echo \"Session recorded. Convert to JSON manually.\"");
            }
            else if (!string.IsNullOrEmpty(inputFile) && File.Exists(inputFile))
            {
                var inputTextFile = ConvertJsonToTextInput(inputFile, botPath);
                if (!string.IsNullOrEmpty(inputTextFile))
                {
                    writer.WriteLine($"\"{actualExecutor}\" {actualArgs} < \"{inputTextFile}\"");
                }
                else
                {
                    writer.WriteLine($"\"{actualExecutor}\" {actualArgs}");
                }
            }
            else
            {
                writer.WriteLine($"\"{actualExecutor}\" {actualArgs}");
            }
            
            writer.WriteLine();
            writer.WriteLine("echo \"\"");
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine("echo \"Bot finished. Press Enter to close...\"");
            writer.WriteLine("read");
        }
        
        File.SetUnixFileMode(shellPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        AnsiConsole.MarkupLine($"[dim]Created shell script: {Path.GetFileName(shellPath)}[/]");
        return shellPath;
    }

    private static string? ConvertJsonToTextInput(string jsonFile, string botPath)
    {
        try
        {
            var json = File.ReadAllText(jsonFile);
            var data = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json);
            
            if (data == null || !data.Any()) return null;
            
            var textFile = Path.Combine(botPath, "_auto_input.txt");
            var lines = data.Where(x => !x.Key.StartsWith("_")).Select(x => x.Value);
            
            var utf8WithoutBom = new UTF8Encoding(false);
            File.WriteAllLines(textFile, lines, utf8WithoutBom);
            
            AnsiConsole.MarkupLine($"[dim]Created input file: {Path.GetFileName(textFile)} ({lines.Count()} lines)[/]");
            return textFile;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Could not convert JSON to text: {ex.Message}[/]");
            return null;
        }
    }

    private static void LaunchInTerminal(string scriptPath, string workingDir)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Launch PowerShell window
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    WorkingDirectory = workingDir,
                    CreateNoWindow = false
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var terminal = "gnome-terminal";
                if (!IsCommandAvailable("gnome-terminal"))
                {
                    terminal = IsCommandAvailable("xterm") ? "xterm" : "x-terminal-emulator";
                }
                
                Process.Start(new ProcessStartInfo
                {
                    FileName = terminal,
                    Arguments = $"-- bash -c '{scriptPath}; exec bash'",
                    UseShellExecute = true,
                    WorkingDirectory = workingDir
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e 'tell application \"Terminal\" to do script \"{scriptPath}\"'",
                    UseShellExecute = true,
                    WorkingDirectory = workingDir
                });
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to launch terminal: {ex.Message}[/]");
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeBotName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Replace(" ", "_").Replace("-", "_");
    }
}
