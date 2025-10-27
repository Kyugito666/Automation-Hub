using Spectre.Console;

namespace Orchestrator;

public static class ProxyManager
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ProxySyncDir = Path.Combine(ProjectRoot, "proxysync");
    private static readonly string ProxySyncScript = Path.Combine(ProxySyncDir, "main.py");
    private static readonly string ProxySyncReqs = Path.Combine(ProxySyncDir, "requirements.txt");
    private static readonly string VenvDir = Path.Combine(ProxySyncDir, ".venv");


    private static string GetProjectRoot()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        
        while (currentDir != null)
        {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            
            if (Directory.Exists(configDir) && File.Exists(gitignore))
            {
                return currentDir.FullName;
            }
            
            currentDir = currentDir.Parent;
        }
        
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }
    
    // Helper untuk menemukan executable di dalam venv
    private static string GetVenvExecutable(string exeName)
    {
        var winPath = Path.Combine(VenvDir, "Scripts", $"{exeName}.exe");
        if (File.Exists(winPath)) return $"\"{winPath}\"";
        
        var linPath = Path.Combine(VenvDir, "bin", exeName);
        if (File.Exists(linPath)) return $"\"{linPath}\"";
        
        // Fallback ke global jika venv tidak ada
        return exeName;
    }


    public static async Task DeployProxies(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- Menjalankan ProxySync (Lokal) ---[/]");

        if (!File.Exists(ProxySyncScript))
        {
            AnsiConsole.MarkupLine($"[red]Error: '{ProxySyncScript}' tidak ditemukan.[/]");
            AnsiConsole.MarkupLine($"[yellow]Path yang dicari: {ProxySyncScript}[/]");
            AnsiConsole.MarkupLine($"[yellow]Project Root: {ProjectRoot}[/]");
            return;
        }

        try
        {
            // === PERBAIKAN: Gunakan VENV ===
            
            // 1. Buat Venv jika belum ada
            if (!Directory.Exists(VenvDir))
            {
                AnsiConsole.MarkupLine("\n[cyan]1. Membuat virtual environment (venv) untuk ProxySync...[/]");
                await ShellHelper.RunCommandAsync("python", $"-m venv \"{VenvDir}\"", ProxySyncDir);
                AnsiConsole.MarkupLine("[green]   ✓ Venv dibuat.[/]");
            }
            
            // 2. Instal dependensi menggunakan pip dari venv
            AnsiConsole.MarkupLine("\n[cyan]2. Menginstal dependensi ProxySync (pip ke venv)...[/]");
            string pipCmd = GetVenvExecutable("pip");
            await ShellHelper.RunCommandAsync(pipCmd, $"install --no-cache-dir --upgrade -r \"{ProxySyncReqs}\"", ProxySyncDir);
            AnsiConsole.MarkupLine("[green]   ✓ Dependensi ProxySync terinstal.[/]");

            // 3. Jalankan skrip menggunakan python dari venv
            AnsiConsole.MarkupLine("\n[cyan]3. Menjalankan ProxySync...[/]");
            AnsiConsole.MarkupLine("[dim]   (Anda akan masuk ke UI interaktif ProxySync)[/]");
            string pythonCmd = GetVenvExecutable("python");
            await ShellHelper.RunInteractive(pythonCmd, $"\"{ProxySyncScript}\"", ProxySyncDir, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
             AnsiConsole.MarkupLine("[yellow]   ProxySync dibatalkan oleh user.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]   Gagal menjalankan ProxySync: {ex.Message}[/]");
            AnsiConsole.WriteException(ex);
        }

        AnsiConsole.MarkupLine("\n[bold green]✅ Proses ProxySync selesai.[/]");
        AnsiConsole.MarkupLine("[dim]   File 'proxysync/proxy.txt' (master list) mungkin telah diperbarui.[/]");
        AnsiConsole.MarkupLine("[dim]   File ini akan di-upload ke Codespace pada 'Start/Manage'.[/]");
    }
}
