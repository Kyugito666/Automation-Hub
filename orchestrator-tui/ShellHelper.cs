using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

public static class ShellHelper
{
    // Path ke PTY wrapper kita
    private const string PtyHelperExe = "pty-helper.exe";

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

    // === METHOD BARU: PTY INTERAKTIF (MANUAL) ===
    // Menggantikan RunInteractive dan RunInteractiveWindows
    public static async Task RunInteractivePty(string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PtyHelperExe))
        {
            AnsiConsole.MarkupLine($"[red]FATAL: Wrapper PTY '{PtyHelperExe}' tidak ditemukan.[/]");
            AnsiConsole.MarkupLine("[red]Pastikan lu sudah build pty-helper-node (npm run build).[/]");
            return;
        }

        // pty-helper <command> <args...>
        string ptyArgs = $"\"{command}\" {args}";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PtyHelperExe,
                Arguments = ptyArgs,
                UseShellExecute = false,
                CreateNoWindow = false, // Harus False agar TTY bisa attach
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
                // CRITICAL: Jangan redirect I/O agar PTY bisa mengambil alih
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
                // Jangan throw exception agar alur TUI bisa lanjut
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Proses interaktif dibatalkan.[/]");
            try
            {
                if (!process.HasExited)
                {
                    // Kirim Ctrl+C ke PTY
                    process.Kill(true); // Kirim SIGINT/Break
                }
            }
            catch { }
            throw; // Re-throw agar TUI utama tahu (Program.cs)
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Error saat menjalankan PTY wrapper: {ex.Message}[/]");
             try { if (!process.HasExited) process.Kill(true); } catch {}
             throw; // Re-throw agar caller tahu ada error
        }
    }

    // === METHOD BARU: PTY DENGAN SCRIPT (AUTO-ANSWER) ===
    public static async Task RunPtyWithScript(string inputFile, string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PtyHelperExe))
        {
            AnsiConsole.MarkupLine($"[red]FATAL: Wrapper PTY '{PtyHelperExe}' tidak ditemukan.[/]");
            return;
        }

        // pty-helper <input_file> <command> <args...>
        // Pastikan path file input absolut dan dalam tanda kutip
        string absInputFile = Path.GetFullPath(inputFile);
        string ptyArgs = $"\"{absInputFile}\" \"{command}\" {args}";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PtyHelperExe,
                Arguments = ptyArgs,
                UseShellExecute = false,
                CreateNoWindow = true, // Bisa true karena tidak interaktif
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
                // Kita redirect I/O di sini agar TUI bisa menampilkan log-nya
                RedirectStandardInput = false, // PTY helper tidak baca stdin di mode script
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


    // === Method lama untuk Linux/macOS (tetap pakai raw process) ===
    // SAYA COMMENT KARENA KITA SEKARANG PAKAI PTY UNTUK SEMUA
    /*
    public static async Task RunInteractive(string command, string args, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        // ... (Implementasi lama) ...
    }
    */
    
    // Method ini punya fungsi BEDA (buka terminal baru), jadi biarkan saja.
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
