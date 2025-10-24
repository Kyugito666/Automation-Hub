using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

public static class ShellHelper
{
    public static async Task RunStream(string command, string args, string? workingDir = null)
    {
        string fileName;
        string finalArgs;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fileName = "cmd.exe";
            finalArgs = $"/c \"{command} {args}\"";
        }
        else
        {
             fileName = "/bin/bash";
            finalArgs = $"-c \"{command} {args}\"";
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = finalArgs,
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
                AnsiConsole.MarkupLineInterpolated($"[grey]{e.Data.EscapeMarkup()}[/]");
        };
        process.ErrorDataReceived += (sender, e) => {
             if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[yellow]{e.Data.EscapeMarkup()}[/]");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Exit Code: {process.ExitCode}[/]");
             if (command == "npm") {
                 AnsiConsole.MarkupLine($"[red]Error running npm. Pastikan Node.js terinstall & ada di PATH.[/]");
             }
        }
    }

    // === METHOD BARU: Windows Interactive Mode (dengan Shell Wrapper) ===
    public static async Task RunInteractiveWindows(string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Fallback ke method lama kalau bukan Windows
            await RunInteractive(command, args, workingDir, cancellationToken);
            return;
        }

        // Di Windows, wrap command dengan cmd.exe /c agar PATH environment ter-inherit
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{command} {args}\"", // Wrap dengan cmd shell
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
                // CRITICAL: Jangan redirect I/O agar user bisa interaksi langsung
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            }
        };

        try
        {
            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]Exit Code: {process.ExitCode}[/]");
                throw new Exception($"Process exited with code {process.ExitCode}");
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Proses interaktif dibatalkan.[/]");
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch { }
            throw;
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Error saat menjalankan proses interaktif: {ex.Message}[/]");
             try { if (!process.HasExited) process.Kill(true); } catch {}
             throw; // Re-throw agar caller tahu ada error
        }
    }

    // Method lama untuk Linux/macOS (tetap pakai raw process)
    public static async Task RunInteractive(string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory()
            }
        };

        try
        {
            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]Exit Code: {process.ExitCode}[/]");
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Proses interaktif dibatalkan.[/]");
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch { }
            throw;
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Error saat menjalankan proses interaktif: {ex.Message}[/]");
             try { if (!process.HasExited) process.Kill(true); } catch {}
        }
    }

    public static void RunInNewTerminal(string command, string args, string? workingDir = null)
    {
        var absPath = workingDir != null ? Path.GetFullPath(workingDir) : Directory.GetCurrentDirectory();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"cd /d \"{absPath}\" && {command} {args}\"",
                UseShellExecute = true
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
                Arguments = $"-- bash -c 'cd \"{absPath}\" && {command} {args}; exec bash'",
                UseShellExecute = true
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
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
        string executor = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executor,
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            string? output = process?.StandardOutput.ReadToEnd();
            return process?.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }
}
