using System.Diagnostics;
using Spectre.Console;

namespace Orchestrator;

public static class ShellHelper
{
    public static async Task RunStream(string command, string args, string? workingDir = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory()
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[grey]   {e.Data}[/]");
        };
        process.ErrorDataReceived += (sender, e) => {
             if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[yellow]   {e.Data}[/]");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Perintah selesai dengan error (Exit Code: {process.ExitCode})[/]");
        }
    }

    public static async Task RunInteractive(string command, string args, string? workingDir = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory()
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine(e.Data);
        };
        
        process.ErrorDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                Console.Error.WriteLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var inputTask = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var input = Console.ReadLine();
                    if (input != null && !process.HasExited)
                    {
                        await process.StandardInput.WriteLineAsync(input);
                        await process.StandardInput.FlushAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Input error: {ex.Message}");
            }
        });

        await process.WaitForExitAsync();

        try
        {
            await inputTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            // Input task masih jalan, ignore
        }

        if (process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Exit Code: {process.ExitCode}[/]");
        }
    }

    public static void RunInNewTerminal(string command, string args, string? workingDir = null)
    {
        var absPath = workingDir != null ? Path.GetFullPath(workingDir) : Directory.GetCurrentDirectory();

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"cd /d \"{absPath}\" && {command} {args}\"",
                UseShellExecute = true
            });
        }
        else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            var terminal = "gnome-terminal";
            if (!IsCommandAvailable("gnome-terminal"))
            {
                terminal = IsCommandAvailable("xterm") ? "xterm" : "x-terminal-emulator";
            }
            
            Process.Start(new ProcessStartInfo
            {
                FileName = terminal,
                Arguments = $"-- bash -c 'cd \"{absPath}\" && {command} {args}; exec bash'",
                UseShellExecute = true
            });
        }
        else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e 'tell application \"Terminal\" to do script \"cd \\\"{absPath}\\\" && {command} {args}\"'",
                UseShellExecute = true
            });
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
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
