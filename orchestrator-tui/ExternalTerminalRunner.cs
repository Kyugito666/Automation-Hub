using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Spectre.Console;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace Orchestrator;

public static class ExternalTerminalRunner
{
    public static string RunBotInExternalTerminal(string botPath, string executor, string args)
    {
        var transcriptFile = Path.Combine(botPath, "_session_transcript.txt");
        
        var scriptPath = CreateRunnerScript(botPath, executor, args, transcriptFile);
        
        if (string.IsNullOrEmpty(scriptPath))
        {
            AnsiConsole.MarkupLine("[red]Failed to create runner script[/]");
            return;
        }

        LaunchInTerminal(scriptPath, botPath);
        AnsiConsole.MarkupLine("[green]✓ Bot launched with session recording[/]");
        AnsiConsole.MarkupLine($"[dim]Transcript will be saved to: {Path.GetFileName(transcriptFile)}[/]");
    }

    private static string? CreateRunnerScript(string botPath, string executor, string args, string transcriptFile)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return CreateWindowsPowerShellScript(botPath, executor, args, transcriptFile);
            }
            else
            {
                return CreateLinuxScript(botPath, executor, args, transcriptFile);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating script: {ex.Message}[/]");
            return null;
        }
    }

    private static string CreateWindowsPowerShellScript(string botPath, string executor, string args, string transcriptFile)
    {
        var ps1Path = Path.Combine(botPath, "_run_bot.ps1");
        var utf8WithoutBom = new UTF8Encoding(false);
        
        using (var writer = new StreamWriter(ps1Path, false, utf8WithoutBom))
        {
            writer.WriteLine("# Bot Runner with Session Recording");
            writer.WriteLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine("[Console]::InputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine();
            writer.WriteLine($"Set-Location -Path \"{botPath}\"");
            writer.WriteLine();
            
            // Start transcript recording
            writer.WriteLine($"$transcriptPath = \"{transcriptFile}\"");
            writer.WriteLine("if (Test-Path $transcriptPath) { Remove-Item $transcriptPath -Force }");
            writer.WriteLine("Start-Transcript -Path $transcriptPath -Force | Out-Null");
            writer.WriteLine();
            
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine($"Write-Host \"Running: {Path.GetFileName(botPath)}\" -ForegroundColor White");
            writer.WriteLine("Write-Host \"MODE: RECORDING SESSION\" -ForegroundColor Yellow");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine("Write-Host \"All your inputs will be captured automatically\" -ForegroundColor Green");
            writer.WriteLine("Write-Host \"\" ");
            writer.WriteLine();

            // Determine actual command
            string executablePath = executor;
            bool useNpmStart = executor.Contains("npm") && args == "start";
            
            if (useNpmStart)
            {
                executablePath = "node";
                args = "index.js";
            }

            // Run bot with recording
            writer.WriteLine("try {");
            writer.WriteLine($"    & \"{executablePath}\" {args}");
            writer.WriteLine("    $exitCode = $LASTEXITCODE");
            writer.WriteLine("} catch {");
            writer.WriteLine("    Write-Host \"Error: $_\" -ForegroundColor Red");
            writer.WriteLine("    $exitCode = 1");
            writer.WriteLine("}");
            
            writer.WriteLine();
            writer.WriteLine("Stop-Transcript | Out-Null");
            writer.WriteLine();
            writer.WriteLine("Write-Host \"\" ");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine("Write-Host \"Bot finished (Exit Code: $exitCode)\" -ForegroundColor $(if ($exitCode -eq 0) { 'Green' } else { 'Red' })");
            writer.WriteLine("Write-Host \"Session recorded to: $transcriptPath\" -ForegroundColor Green");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine("Write-Host \"Press any key to close...\" -ForegroundColor Gray");
            writer.WriteLine("[void][System.Console]::ReadKey($true)");
        }
        
        AnsiConsole.MarkupLine($"[dim]Created recording script: {Path.GetFileName(ps1Path)}[/]");
        return ps1Path;
    }

    private static string CreateLinuxScript(string botPath, string executor, string args, string transcriptFile)
    {
        var shellPath = Path.Combine(botPath, "_run_bot.sh");
        var utf8WithoutBom = new UTF8Encoding(false);
        
        using (var writer = new StreamWriter(shellPath, false, utf8WithoutBom))
        {
            writer.WriteLine("#!/bin/bash");
            writer.WriteLine($"cd \"{botPath}\"");
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine($"echo \"Running: {Path.GetFileName(botPath)}\"");
            writer.WriteLine("echo \"MODE: RECORDING SESSION\"");
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine("echo \"All your inputs will be captured automatically\"");
            writer.WriteLine("echo \"\"");
            writer.WriteLine();

            bool useNpmStart = executor.Contains("npm") && args == "start";
            string actualExecutor = useNpmStart ? "node" : executor;
            string actualArgs = useNpmStart ? "index.js" : args;

            // Use script command for recording
            writer.WriteLine($"script -q -c '\"{actualExecutor}\" {actualArgs}' \"{transcriptFile}\"");
            
            writer.WriteLine();
            writer.WriteLine("echo \"\"");
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine($"echo \"Session recorded to: {Path.GetFileName(transcriptFile)}\"");
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine("echo \"Press Enter to close...\"");
            writer.WriteLine("read");
        }
        
        File.SetUnixFileMode(shellPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        AnsiConsole.MarkupLine($"[dim]Created recording script: {Path.GetFileName(shellPath)}[/]");
        return shellPath;
    }

    private static void LaunchInTerminal(string scriptPath, string workingDir)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
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

    public static Dictionary<string, string> ParseTranscript(string transcriptFile)
    {
        var inputs = new Dictionary<string, string>();
        
        if (!File.Exists(transcriptFile))
        {
            AnsiConsole.MarkupLine($"[yellow]Transcript file not found: {transcriptFile}[/]");
            return inputs;
        }

        try
        {
            var lines = File.ReadAllLines(transcriptFile);
            var promptPattern = new Regex(@"(?:pilih|select|enter|input|choose|->|:)\s*(.+?)$", RegexOptions.IgnoreCase);
            
            string? lastPrompt = null;
            int inputCounter = 1;
            
            foreach (var line in lines)
            {
                var cleanLine = line.Trim();
                
                // Skip empty lines and PS header/footer
                if (string.IsNullOrWhiteSpace(cleanLine) || 
                    cleanLine.StartsWith("****") || 
                    cleanLine.Contains("Transcript started") ||
                    cleanLine.Contains("Transcript stopped"))
                {
                    continue;
                }
                
                // Detect prompt
                var match = promptPattern.Match(cleanLine);
                if (match.Success || cleanLine.Contains("?") || cleanLine.EndsWith(":") || cleanLine.Contains("->"))
                {
                    lastPrompt = cleanLine;
                    continue;
                }
                
                // Detect input (short lines after prompt, numeric, or y/n)
                if (lastPrompt != null && 
                    (cleanLine.Length <= 10 || 
                     Regex.IsMatch(cleanLine, @"^\d+$") || 
                     Regex.IsMatch(cleanLine, @"^[yn]$", RegexOptions.IgnoreCase)))
                {
                    var key = $"input_{inputCounter}";
                    inputs[key] = cleanLine;
                    inputCounter++;
                    lastPrompt = null;
                }
            }
            
            AnsiConsole.MarkupLine($"[green]✓ Parsed {inputs.Count} inputs from transcript[/]");
            
            if (inputs.Any())
            {
                AnsiConsole.MarkupLine("[cyan]Captured inputs:[/]");
                foreach (var kv in inputs.Take(5))
                {
                    AnsiConsole.MarkupLine($"  [dim]{kv.Key}:[/] [yellow]{kv.Value}[/]");
                }
                if (inputs.Count > 5)
                {
                    AnsiConsole.MarkupLine($"  [dim]... and {inputs.Count - 5} more[/]");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error parsing transcript: {ex.Message}[/]");
        }
        
        return inputs;
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
}
