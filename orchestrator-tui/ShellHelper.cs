using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace Orchestrator;

public static class ShellHelper
{
    // CRITICAL FIX: Gunakan absolute path yang diresolve saat startup
    private static readonly string PtyHelperScript;
    private static readonly string PythonExecutable;

    static ShellHelper()
    {
        // Resolve absolute path saat class di-load (HANYA SEKALI)
        var baseDir = AppDomain.CurrentDomain.BaseDirectory; // bin/Debug/net8.0/
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        PtyHelperScript = Path.Combine(projectRoot, "pty-helper-py", "pty_helper.py");

        // Detect Python executable dengan fallback
        PythonExecutable = DetectPythonExecutable();

        // Validasi saat startup
        if (!File.Exists(PtyHelperScript))
        {
            AnsiConsole.MarkupLine($"[red]CRITICAL: PTY helper script NOT FOUND at: {PtyHelperScript}[/]");
            AnsiConsole.MarkupLine("[yellow]Sistem akan tetap jalan, tapi fitur Proxy Mode akan error.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]✓ PTY helper loaded: {Path.GetFileName(PtyHelperScript)}[/]");
        }

        AnsiConsole.MarkupLine($"[dim]✓ Python executable: {PythonExecutable}[/]");
    }

    private static string DetectPythonExecutable()
    {
        // Priority: python3 > python > py
        string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "python", "py", "python3" }
            : new[] { "python3", "python" };

        foreach (var candidate in candidates)
        {
            if (IsCommandAvailable(candidate))
            {
                return candidate;
            }
        }

        // Fallback default
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
    }

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

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[grey]{e.Data.EscapeMarkup()}[/]");
        };
        process.ErrorDataReceived += (sender, e) =>
        {
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
            if (command == "npm")
            {
                AnsiConsole.MarkupLine($"[red]Error running npm. Pastikan Node.js terinstall & ada di PATH.[/]");
            }
        }
    }

    public static async Task RunInteractivePty(string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PtyHelperScript))
        {
            AnsiConsole.MarkupLine($"[red]FATAL: PTY helper script tidak ditemukan: {PtyHelperScript}[/]");
            AnsiConsole.MarkupLine("[yellow]Fallback: Menjalankan tanpa PTY (mungkin tidak interaktif)...[/]");
            await RunStreamInteractive(command, args, workingDir, cancellationToken);
            return;
        }

        // CRITICAL FIX: Gunakan absolute path yang sudah diresolve
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
            AnsiConsole.MarkupLine($"[dim]Command: {PythonExecutable} {pythonArgs}[/]");
            AnsiConsole.MarkupLine($"[dim]WorkDir: {workingDir ?? Directory.GetCurrentDirectory()}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
    }

    public static async Task RunPtyWithScript(string inputFile, string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PtyHelperScript))
        {
            AnsiConsole.MarkupLine($"[red]FATAL: PTY helper script tidak ditemukan: {PtyHelperScript}[/]");
            AnsiConsole.MarkupLine("[yellow]Fallback: Menjalankan tanpa auto-answer (mungkin hang)...[/]");
            await RunStreamInteractive(command, args, workingDir, cancellationToken);
            return;
        }

        string absInputFile = Path.GetFullPath(inputFile);
        
        // Validasi input file
        if (!File.Exists(absInputFile))
        {
            AnsiConsole.MarkupLine($"[red]ERROR: Input file tidak ditemukan: {absInputFile}[/]");
            return;
        }

        // CRITICAL FIX: Gunakan absolute path
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

        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[grey]{e.Data.EscapeMarkup()}[/]");
        };
        process.ErrorDataReceived += (s, e) =>
        {
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
            AnsiConsole.MarkupLine($"[dim]Command: {PythonExecutable} {pythonArgs}[/]");
            AnsiConsole.MarkupLine($"[dim]Input: {absInputFile}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
    }

    // FALLBACK: Run without PTY (basic subprocess)
    private static async Task RunStreamInteractive(string command, string args, string? workingDir, CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[grey]{e.Data.EscapeMarkup()}[/]");
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[yellow]{e.Data.EscapeMarkup()}[/]");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
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
