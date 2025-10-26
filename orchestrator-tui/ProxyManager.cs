using Spectre.Console;

namespace Orchestrator;

public static class ProxyManager
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ProxySyncDir = Path.Combine(ProjectRoot, "proxysync");
    private static readonly string ProxySyncScript = Path.Combine(ProxySyncDir, "main.py");
    private static readonly string ProxySyncReqs = Path.Combine(ProxySyncDir, "requirements.txt");

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

        AnsiConsole.MarkupLine("\n[cyan]1. Menginstal dependensi ProxySync (pip)...[/]");
        try
        {
// === PERBAIKAN IMPORT ERROR ===
            await ShellHelper.RunCommandAsync("pip", $"install --no-cache-dir --upgrade -r \"{ProxySyncReqs}\"", ProxySyncDir);
            // === AKHIR PERBAIKAN ===
            AnsiConsole.MarkupLine("[green]   ✓ Dependensi ProxySync terinstal.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]   Gagal menginstal dependensi: {ex.Message}[/]");
            return;
        }

        AnsiConsole.MarkupLine("\n[cyan]2. Menjalankan ProxySync...[/]");
        AnsiConsole.MarkupLine("[dim]   (Anda akan masuk ke UI interaktif ProxySync)[/]");

        try
        {
// === PERBAIKAN PATH PYTHON ===
            // Ganti path di bawah ini dengan hasil 'where python' lu
            string pythonExecutablePath = @"C:\Users\ADIT\AppData\Local\Microsoft\WindowsApps\PythonSoftwareFoundation.Python.3.11_qbz5n2kfra8p0\python.exe"; 
            await ShellHelper.RunInteractive(pythonExecutablePath, $"\"{ProxySyncScript}\"", ProxySyncDir, null, cancellationToken);
            // === AKHIR PERBAIKAN ===
        }
        catch (OperationCanceledException)
        {
             AnsiConsole.MarkupLine("[yellow]   ProxySync dibatalkan oleh user.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]   Gagal menjalankan ProxySync: {ex.Message}[/]");
        }

        AnsiConsole.MarkupLine("\n[bold green]✅ Proses ProxySync selesai.[/]");
        AnsiConsole.MarkupLine("[dim]   File 'proxysync/proxy.txt' (master list) mungkin telah diperbarui.[/]");
        AnsiConsole.MarkupLine("[dim]   File ini akan di-upload ke Codespace pada 'Start/Manage'.[/]");
    }
}
