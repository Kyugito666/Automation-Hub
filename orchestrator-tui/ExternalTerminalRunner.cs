using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Spectre.Console;
using System.Text;

namespace Orchestrator;

public static class ExternalTerminalRunner
{
    public static void RunBotInExternalTerminal(string botPath, string executor, string args)
    {
        var scriptPath = CreateRunnerScript(botPath, executor, args);
        
        if (string.IsNullOrEmpty(scriptPath))
        {
            AnsiConsole.MarkupLine("[red]Failed to create runner script[/]");
            return;
        }

        LaunchInTerminal(scriptPath, botPath);
        AnsiConsole.MarkupLine("[green]✓ Bot launched in external terminal[/]");
        AnsiConsole.MarkupLine("[dim]Interact with the bot manually in the new window[/]");
    }

    private static string? CreateRunnerScript(string botPath, string executor, string args)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return CreateWindowsPowerShellScript(botPath, executor, args);
            }
            else
            {
                return CreateLinuxScript(botPath, executor, args);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating script: {ex.Message}[/]");
            return null;
        }
    }

    private static string CreateWindowsPowerShellScript(string botPath, string executor, string args)
    {
        var ps1Path = Path.Combine(botPath, "_run_bot.ps1");
        var utf8WithoutBom = new UTF8Encoding(false);
        
        using (var writer = new StreamWriter(ps1Path, false, utf8WithoutBom))
        {
            writer.WriteLine("# Bot Runner - Manual Interaction Mode");
            writer.WriteLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine("[Console]::InputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
            writer.WriteLine();
            writer.WriteLine($"Set-Location -Path \"{botPath}\"");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine($"Write-Host \"Running: {Path.GetFileName(botPath)}\" -ForegroundColor White");
            writer.WriteLine("Write-Host \"MODE: MANUAL INTERACTION\" -ForegroundColor Yellow");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
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

            // Run bot with full interaction
            writer.WriteLine("try {");
            writer.WriteLine($"    & \"{executablePath}\" {args}");
            writer.WriteLine("    $exitCode = $LASTEXITCODE");
            writer.WriteLine("} catch {");
            writer.WriteLine("    Write-Host \"Error: $_\" -ForegroundColor Red");
            writer.WriteLine("    $exitCode = 1");
            writer.WriteLine("}");
            
            writer.WriteLine();
            writer.WriteLine("Write-Host \"\" ");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine("Write-Host \"Bot finished (Exit Code: $exitCode)\" -ForegroundColor $(if ($exitCode -eq 0) { 'Green' } else { 'Red' })");
            writer.WriteLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            writer.WriteLine("Write-Host \"Press any key to close...\" -ForegroundColor Gray");
            writer.WriteLine("[void][System.Console]::ReadKey($true)");
        }
        
        AnsiConsole.MarkupLine($"[dim]Created PowerShell script: {Path.GetFileName(ps1Path)}[/]");
        return ps1Path;
    }

    private static string CreateLinuxScript(string botPath, string executor, string args)
    {
        var shellPath = Path.Combine(botPath, "_run_bot.sh");
        var utf8WithoutBom = new UTF8Encoding(false);
        
        using (var writer = new StreamWriter(shellPath, false, utf8WithoutBom))
        {
            writer.WriteLine("#!/bin/bash");
            writer.WriteLine($"cd \"{botPath}\"");
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine($"echo \"Running: {Path.GetFileName(botPath)}\"");
            writer.WriteLine("echo \"MODE: MANUAL INTERACTION\"");
            writer.WriteLine("echo \"========================================\"");
            writer.WriteLine();

            bool useNpmStart = executor.Contains("npm") && args == "start";
            string actualExecutor = useNpmStart ? "node" : executor;
            string actualArgs = useNpmStart ? "index.js" : args;

            writer.WriteLine($"\"{actualExecutor}\" {actualArgs}");
            
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
