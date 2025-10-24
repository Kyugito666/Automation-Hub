using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace Orchestrator;

public static class ShellHelper
{
    // UBAH: Pakai Python script
    private static readonly string PtyHelperScript = Path.Combine("..", "pty-helper-py", "pty_helper.py");
    private static string PythonExecutable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";

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

    public static async Task RunInteractivePty(string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PtyHelperScript))
        {
            AnsiConsole.MarkupLine($"[red]FATAL: PTY helper script tidak ditemukan: {PtyHelperScript}[/]");
            AnsiConsole.MarkupLine("[yellow]Pastikan folder 'pty-helper-py' ada di root project.[/]");
            return;
        }

        // python pty_helper.py <command> <args>
        string pythonArgs = $"\"{PtyHelperScript}\" \"{command}\" {args}";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PythonExecutable,
                Arguments = pythonArgs,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
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
                AnsiConsole.MarkupLine($"[yellow]Process exited with code {process.ExitCode}[/]");
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
             AnsiConsole.MarkupLine($"[red]Error saat menjalankan PTY wrapper: {ex.Message}[/]");
             try { if (!process.HasExited) process.Kill(true); } catch {}
             throw;
        }
    }

    public static async Task RunPtyWithScript(string inputFile, string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PtyHelperScript))
        {
            AnsiConsole.MarkupLine($"[red]FATAL: PTY helper script tidak ditemukan: {PtyHelperScript}[/]");
            return;
        }

        string absInputFile = Path.GetFullPath(inputFile);
        // python pty_helper.py <input_file> <command> <args>
        string pythonArgs = $"\"{PtyHelperScript}\" \"{absInputFile}\" \"{command}\" {args}";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PythonExecutable,
                Arguments = pythonArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.OutputDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[grey]{e.Data.EscapeMarkup()}[/]");
        };
        process.ErrorDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[yellow]{e.Data.EscapeMarkup()}[/]");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Auto-run process exited with code {process.ExitCode}[/]");
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Auto-run dibatalkan.[/]");
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch { }
            throw;
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Error saat menjalankan PTY auto-run: {ex.Message}[/]");
             try { if (!process.HasExited) process.Kill(true); } catch {}
             throw;
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
```

---

## 3. Update `.gitignore`
```
# Abaikan SEMUA yang di-clone saat runtime
bots/*
!bots/.gitkeep

proxysync/

# File cache dan rahasia lainnya
__pycache__/
.venv/
*.log
*.env

# File state & input rahasia dari Orchestrator
.token-state.json
.bot-inputs/
.token-cache.json

# Abaikan file .NET build (bin/obj)
**/bin/
**/obj/

# File log baru dari BotScanner
.raw-bots.log

# HAPUS: Node PTY (sudah diganti Python)
pty-helper-node/
