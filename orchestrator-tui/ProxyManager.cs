using Spectre.Console;
using System.Threading; // <-- Tambah using Threading
using System.Threading.Tasks; // <-- Tambah using Tasks

namespace Orchestrator;

public static class ProxyManager
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ProxySyncDir = Path.Combine(ProjectRoot, "proxysync");
    private static readonly string ProxySyncScript = Path.Combine(ProxySyncDir, "main.py");
    private static readonly string ProxySyncReqs = Path.Combine(ProxySyncDir, "requirements.txt");

    private static string GetProjectRoot()
    {
        // (Fungsi GetProjectRoot tidak berubah)
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null) {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            if (Directory.Exists(configDir) && File.Exists(gitignore)) {
                return currentDir.FullName;
            }
            currentDir = currentDir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    // --- FUNGSI BARU: Hanya jalankan IP Auth ---
    public static async Task<bool> RunIpAuthorizationOnlyAsync(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[cyan]--- Menjalankan Auto IP Authorization (ProxySync) ---[/]");

        if (!File.Exists(ProxySyncScript)) {
            AnsiConsole.MarkupLine($"[red]   Error: Skrip ProxySync '{ProxySyncScript}' tidak ditemukan.[/]");
            return false;
        }

        // Tidak perlu install dependensi di sini, asumsi sudah terinstall saat DeployProxies atau di remote
        // Jika butuh, bisa tambahkan logic cek & install dependensi seperti di DeployProxies

        AnsiConsole.MarkupLine("[dim]   Memulai proses IP Auth...[/]");
        try
        {
            // Panggil main.py dengan flag --ip-auth-only
            // Gunakan RunCommandAsync karena ini non-interaktif
            await ShellHelper.RunCommandAsync("python", $"\"{ProxySyncScript}\" --ip-auth-only", ProxySyncDir); // <-- Tambah flag
            AnsiConsole.MarkupLine("[green]   ✓ Proses IP Auth selesai.[/]");
            return true; // Anggap sukses jika command selesai tanpa error
        }
        catch (OperationCanceledException) {
             AnsiConsole.MarkupLine("[yellow]   Proses IP Auth dibatalkan.[/]");
             return false;
        }
        catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]   ✗ Gagal menjalankan IP Auth: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            return false;
        }
    }
    // --- AKHIR FUNGSI BARU ---


    // Fungsi DeployProxies tetap sama
    public static async Task DeployProxies(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- Menjalankan ProxySync (Lokal - Menu Lengkap) ---[/]");

        if (!File.Exists(ProxySyncScript)) {
            AnsiConsole.MarkupLine($"[red]Error: '{ProxySyncScript}' tidak ditemukan.[/]");
            AnsiConsole.MarkupLine($"[yellow]Path yang dicari: {ProxySyncScript}[/]");
            AnsiConsole.MarkupLine($"[yellow]Project Root: {ProjectRoot}[/]");
            return;
        }

        AnsiConsole.MarkupLine("\n[cyan]1. Menginstal/Update dependensi ProxySync (pip)...[/]");
        try {
            await ShellHelper.RunCommandAsync("pip", $"install --no-cache-dir --upgrade -r \"{ProxySyncReqs}\"", ProxySyncDir);
            AnsiConsole.MarkupLine("[green]   ✓ Dependensi ProxySync siap.[/]");
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]   Gagal menginstal dependensi: {ex.Message}[/]");
            return;
        }

        AnsiConsole.MarkupLine("\n[cyan]2. Menjalankan Menu Interaktif ProxySync...[/]");
        AnsiConsole.MarkupLine("[dim]   (Anda akan masuk ke UI interaktif ProxySync)[/]");
        try {
            // Jalankan tanpa argumen untuk mode interaktif
            await ShellHelper.RunInteractive("python", $"\"{ProxySyncScript}\"", ProxySyncDir, null, cancellationToken);
        } catch (OperationCanceledException) {
             AnsiConsole.MarkupLine("[yellow]   ProxySync dibatalkan oleh user.[/]");
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]   Gagal menjalankan ProxySync: {ex.Message}[/]");
        }

        AnsiConsole.MarkupLine("\n[bold green]✅ Proses ProxySync selesai.[/]");
        AnsiConsole.MarkupLine("[dim]   File 'proxysync/success_proxy.txt' (hasil tes) dan 'config/apilist.txt' (cache URL) mungkin telah diperbarui.[/]");
        AnsiConsole.MarkupLine("[dim]   File-file ini akan digunakan oleh TUI dan diupload ke Codespace.[/]");
    }
}
