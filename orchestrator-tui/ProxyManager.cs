using Spectre.Console;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orchestrator;

public static class ProxyManager
{
    private const string ProxySyncPath = "../proxysync";
    private const string ConfigSourcePath = "../config";

    public static async Task DeployProxies()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- Memulai Proses Deploy Proxy ---[/]");

        if (!Directory.Exists(ProxySyncPath))
        {
            AnsiConsole.MarkupLine($"[red]Error: Folder '{ProxySyncPath}' tidak ditemukan.[/]");
            AnsiConsole.MarkupLine("[yellow]Harap jalankan 'Update Semua Bot & Tools' (Menu 1) terlebih dahulu.[/]");
            return;
        }

        AnsiConsole.MarkupLine("1. Menyalin file config (apilist.txt, paths.txt)...");
        try
        {
            File.Copy(Path.Combine(ConfigSourcePath, "apilist.txt"), Path.Combine(ProxySyncPath, "apilist.txt"), true);
            File.Copy(Path.Combine(ConfigSourcePath, "paths.txt"), Path.Combine(ProxySyncPath, "paths.txt"), true);
            AnsiConsole.MarkupLine("[green]   Salin config berhasil.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]   Gagal menyalin config: {ex.Message}[/]");
            return;
        }

        AnsiConsole.MarkupLine("\n2. Menjalankan 'pip install -r requirements.txt' untuk ProxySync...");
        await ShellHelper.RunStream("pip", "install -r requirements.txt", ProxySyncPath);

        AnsiConsole.MarkupLine("\n3. Menjalankan 'python main.py'...");
        
        var absPath = Path.GetFullPath(ProxySyncPath);
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"cd /d \"{absPath}\" && python main.py\"",
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
                Arguments = $"-- bash -c 'cd \"{absPath}\" && python main.py; exec bash'",
                UseShellExecute = true
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = $"-a Terminal \"{absPath}\"",
                UseShellExecute = true
            });
        }
        
        AnsiConsole.MarkupLine("[green]Terminal eksternal dibuka. Selesaikan proses di terminal tersebut.[/]");
        AnsiConsole.MarkupLine("[dim]Tekan Enter di sini setelah selesai...[/]");
        Console.ReadLine();
        
        AnsiConsole.MarkupLine("\n[bold green]✅ Proses deploy proxy selesai.[/]");
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
