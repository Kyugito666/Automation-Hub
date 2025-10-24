using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;

namespace Orchestrator;

public static class ShellHelper
{
    public static async Task RunStream(string command, string args, string? workingDir = null)
    {
        string fileName;
        string finalArgs;

        // === PERUBAHAN DI SINI ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Jalankan via cmd /c untuk memastikan PATH dibaca
            fileName = "cmd.exe";
            // Kutip argumen asli untuk mencegah masalah spasi
            finalArgs = $"/c \"{command} {args}\"";
        }
        else // Linux/macOS
        {
             // Jalankan via bash -c
            fileName = "/bin/bash";
             // Kutip argumen asli
            finalArgs = $"-c \"{command} {args}\"";
        }
        // ========================


        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                // Gunakan fileName & finalArgs yang sudah disiapkan
                FileName = fileName,
                Arguments = finalArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false, // Tetap false, karena kita panggil shell secara eksplisit
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
            // Tambahkan pesan error jika gagal, terutama untuk npm
             if (command == "npm") {
                 AnsiConsole.MarkupLine($"[red]Error running npm. Pastikan Node.js terinstall & ada di PATH.[/]");
             }
        }
    }

    // ... (RunInteractive, RunInNewTerminal, IsCommandAvailable tidak berubah) ...
     public static async Task RunInteractive(string command, string args, string? workingDir = null)
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

        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Exit Code: {process.ExitCode}[/]");
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
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which", // 'where' on Windows, 'which' on Unix
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
            // On Windows, 'which' might not exist, try 'where'
             if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
             {
                 try
                 {
                     var process = Process.Start(new ProcessStartInfo
                     {
                         FileName = "where",
                         Arguments = command,
                         RedirectStandardOutput = true,
                         UseShellExecute = false,
                         CreateNoWindow = true
                     });
                     process?.WaitForExit();
                     return process?.ExitCode == 0;
                 }
                 catch { return false; }
             }
            return false;
        }
    }
}
