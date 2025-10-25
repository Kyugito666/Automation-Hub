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
    public static void RunBotInExternalTerminal(string botPath, string executor, string args, string? inputFile = null)
    {
        var scriptPath = CreateRunnerScript(botPath, executor, args, inputFile);
        
        if (string.IsNullOrEmpty(scriptPath))
        {
            AnsiConsole.MarkupLine("[red]Failed to create runner script[/]");
            return;
        }

        LaunchInTerminal(scriptPath, botPath);
        
        AnsiConsole.MarkupLine("[green]✓ Bot launched in external terminal[/]");
        AnsiConsole.MarkupLine("[dim]Interact with the bot in the new window[/]");
    }

    private static string? CreateRunnerScript(string botPath, string executor, string args, string? inputFile)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var batchPath = Path.Combine(botPath, "_run_bot.bat");
                
                // CRITICAL FIX: UTF-8 WITHOUT BOM
                var utf8WithoutBom = new UTF8Encoding(false);
                
                using (var writer = new StreamWriter(batchPath, false, utf8WithoutBom))
                {
                    writer.WriteLine("@echo off");
                    writer.WriteLine("chcp 65001 >nul 2>&1");
                    writer.WriteLine($"cd /d \"{botPath}\"");
                    writer.WriteLine("echo ========================================");
                    writer.WriteLine($"echo Running: {Path.GetFileName(botPath)}");
                    writer.WriteLine("echo ========================================");
                    writer.WriteLine();
                    
                    if (!string.IsNullOrEmpty(inputFile) && File.Exists(inputFile))
                    {
                        writer.WriteLine($"echo Using auto-input from: {Path.GetFileName(inputFile)}");
                        writer.WriteLine();
                        
                        var inputTextFile = ConvertJsonToTextInput(inputFile, botPath);
                        if (!string.IsNullOrEmpty(inputTextFile))
                        {
                            // FIX: Use node directly for npm start (bypass npm.cmd issues)
                            if (executor.Contains("npm") && args == "start")
                            {
                                writer.WriteLine($"node index.js < \"{inputTextFile}\"");
                            }
                            else
                            {
                                writer.WriteLine($"\"{executor}\" {args} < \"{inputTextFile}\"");
                            }
                        }
                        else
                        {
                            if (executor.Contains("npm") && args == "start")
                            {
                                writer.WriteLine("node index.js");
                            }
                            else
                            {
                                writer.WriteLine($"\"{executor}\" {args}");
                            }
                        }
                    }
                    else
                    {
                        if (executor.Contains("npm") && args == "start")
                        {
                            writer.WriteLine("node index.js");
                        }
                        else
                        {
                            writer.WriteLine($"\"{executor}\" {args}");
                        }
                    }
                    
                    writer.WriteLine();
                    writer.WriteLine("echo.");
                    writer.WriteLine("echo ========================================");
                    writer.WriteLine("echo Bot finished. Press any key to close...");
                    writer.WriteLine("pause >nul");
                }
                
                AnsiConsole.MarkupLine($"[dim]Created script: {Path.GetFileName(batchPath)}[/]");
                return batchPath;
            }
            else
            {
                var shellPath = Path.Combine(botPath, "_run_bot.sh");
                
                using (var writer = new StreamWriter(shellPath, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("#!/bin/bash");
                    writer.WriteLine($"cd \"{botPath}\"");
                    writer.WriteLine("echo \"========================================\"");
                    writer.WriteLine($"echo \"Running: {Path.GetFileName(botPath)}\"");
                    writer.WriteLine("echo \"========================================\"");
                    writer.WriteLine();
                    
                    if (!string.IsNullOrEmpty(inputFile) && File.Exists(inputFile))
                    {
                        writer.WriteLine($"echo \"Using auto-input from: {Path.GetFileName(inputFile)}\"");
                        writer.WriteLine();
                        
                        var inputTextFile = ConvertJsonToTextInput(inputFile, botPath);
                        if (!string.IsNullOrEmpty(inputTextFile))
                        {
                            if (executor.Contains("npm") && args == "start")
                            {
                                writer.WriteLine($"node index.js < \"{inputTextFile}\"");
                            }
                            else
                            {
                                writer.WriteLine($"\"{executor}\" {args} < \"{inputTextFile}\"");
                            }
                        }
                        else
                        {
                            if (executor.Contains("npm") && args == "start")
                            {
                                writer.WriteLine("node index.js");
                            }
                            else
                            {
                                writer.WriteLine($"\"{executor}\" {args}");
                            }
                        }
                    }
                    else
                    {
                        if (executor.Contains("npm") && args == "start")
                        {
                            writer.WriteLine("node index.js");
                        }
                        else
                        {
                            writer.WriteLine($"\"{executor}\" {args}");
                        }
                    }
                    
                    writer.WriteLine();
                    writer.WriteLine("echo \"\"");
                    writer.WriteLine("echo \"========================================\"");
                    writer.WriteLine("echo \"Bot finished. Press Enter to close...\"");
                    writer.WriteLine("read");
                }
                
                File.SetUnixFileMode(shellPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                AnsiConsole.MarkupLine($"[dim]Created script: {Path.GetFileName(shellPath)}[/]");
                return shellPath;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating script: {ex.Message}[/]");
            return null;
        }
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
            
            // CRITICAL FIX: UTF-8 WITHOUT BOM
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"Bot Runner\" cmd /k \"{scriptPath}\"",
                    UseShellExecute = true,
                    WorkingDirectory = workingDir
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
