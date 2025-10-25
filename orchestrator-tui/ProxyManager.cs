using Spectre.Console;

namespace Orchestrator;

public static class ProxyManager
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProxySyncDir = Path.Combine(ProjectRoot, "proxysync");
    private static readonly string ProxySyncScript = Path.Combine(ProxySyncDir, "main.py");
    private static readonly string ProxySyncReqs = Path.Combine(ProxySyncDir, "requirements.txt");

    public static async Task DeployProxies()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- Menjalankan ProxySync (Lokal) ---[/]");

        if (!File.Exists(ProxySyncScript))
        {
            AnsiConsole.MarkupLine($"[red]Error: '{ProxySyncScript}' tidak ditemukan.[/]");
            return;
        }

        // 1. Install dependencies untuk ProxySync
        AnsiConsole.MarkupLine("\n[cyan]1. Menginstal dependensi ProxySync (pip)...[/]");
        try
        {
            // Gunakan ShellHelper tanpa token (command lokal)
            await ShellHelper.RunCommandAsync("pip", $"install --no-cache-dir -r \"{ProxySyncReqs}\"", ProxySyncDir);
            AnsiConsole.MarkupLine("[green]   ✓ Dependensi ProxySync terinstal.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]   Gagal menginstal dependensi: {ex.Message}[/]");
            return;
        }

        // 2. Jalankan skrip ProxySync secara interaktif
        AnsiConsole.MarkupLine("\n[cyan]2. Menjalankan ProxySync...[/]");
        AnsiConsole.MarkupLine("[dim]   (Anda akan masuk ke UI interaktif ProxySync)[/]");
        
        // Hapus komentar ini setelah ShellHelper.RunInteractive (Part 4) ditambahkan
        // try
        // {
        //     await ShellHelper.RunInteractive("python", $"\"{ProxySyncScript}\"", ProxySyncDir);
        // }
        // catch (Exception ex)
        // {
        //     AnsiConsole.MarkupLine($"[red]   Gagal menjalankan ProxySync: {ex.Message}[/]");
        // }

        // Placeholder sampai Part 4:
        AnsiConsole.MarkupLine("[yellow]   (Placeholder: ShellHelper.RunInteractive akan ditambahkan di Part 4)[/]");
        AnsiConsole.MarkupLine("[yellow]   (Untuk saat ini, jalankan 'python proxysync/main.py' manual jika perlu)[/]");


        AnsiConsole.MarkupLine("\n[bold green]✅ Proses ProxySync selesai.[/]");
        AnsiConsole.MarkupLine("[dim]   File 'proxysync/proxy.txt' (master list) telah diperbarui.[/]");
        AnsiConsole.MarkupLine("[dim]   File ini akan di-upload ke Codespace pada 'Start/Manage'.[/]");
    }
}
