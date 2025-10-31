using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;
using System.IO; 
using Orchestrator.Util; // <-- PERBAIKAN: Ditambahkan

namespace Orchestrator.Services 
{
    public static class ProxyService
    {
        private static readonly string ProjectRoot = GetProjectRoot();
        private static readonly string ProxySyncDir = Path.Combine(ProjectRoot, "proxysync");
        private static readonly string ProxySyncScript = Path.Combine(ProxySyncDir, "main.py");
        private static readonly string ProxySyncReqs = Path.Combine(ProxySyncDir, "requirements.txt");

         private static string GetProjectRoot()
        {
            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (currentDir != null) {
                var configDir = Path.Combine(currentDir.FullName, "config");
                var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
                if (Directory.Exists(configDir) && File.Exists(gitignore)) { return currentDir.FullName; }
                currentDir = currentDir.Parent;
            }
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        }

        public static async Task<bool> RunIpAuthorizationOnlyAsync(CancellationToken cancellationToken = default)
        {
            AnsiConsole.MarkupLine("[cyan]--- Menjalankan Auto IP Authorization (ProxySync) ---[/]");
            if (!File.Exists(ProxySyncScript)) {
                AnsiConsole.MarkupLine($"[red]   Error: Skrip ProxySync '{ProxySyncScript}' tidak ditemukan.[/]");
                return false;
            }
            AnsiConsole.MarkupLine("[dim]   Memulai proses IP Auth...[/]");
            try {
                await ShellUtil.RunCommandAsync("python", $"\"{ProxySyncScript}\" --ip-auth-only", ProxySyncDir);
                AnsiConsole.MarkupLine("[green]   ✓ Proses IP Auth selesai.[/]");
                return true;
            } catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]   Proses IP Auth dibatalkan.[/]");
                 return false;
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]   ✗ Gagal menjalankan IP Auth: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                return false;
            }
        }

        public static async Task<bool> RunProxyTestAndSaveAsync(CancellationToken cancellationToken = default)
        {
            AnsiConsole.MarkupLine("[cyan]--- Menjalankan Auto Proxy Test & Save (ProxySync) ---[/]");

            if (!File.Exists(ProxySyncScript)) {
                AnsiConsole.MarkupLine($"[red]   Error: Skrip ProxySync '{ProxySyncScript}' tidak ditemukan.[/]");
                return false;
            }

            AnsiConsole.MarkupLine("[dim]   Memulai proses Test & Save...[/]");
            try
            {
                await ShellUtil.RunCommandAsync("python", $"\"{ProxySyncScript}\" --test-and-save-only", ProxySyncDir); 
                AnsiConsole.MarkupLine("[green]   ✓ Proses Test & Save selesai. 'success_proxy.txt' mungkin diperbarui.[/]");
                return true; 
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]   Proses Test & Save dibatalkan.[/]");
                 return false;
            }
            catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]   ✗ Gagal menjalankan Test & Save: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                return false;
            }
        }

        public static async Task DeployProxies(CancellationToken cancellationToken = default)
        {
            AnsiConsole.MarkupLine("[bold cyan]--- Menjalankan ProxySync (Lokal - Menu Lengkap) ---[/]");
             if (!File.Exists(ProxySyncScript)) {
                AnsiConsole.MarkupLine($"[red]Error: '{ProxySyncScript}' tidak ditemukan.[/]");
                return;
            }
            AnsiConsole.MarkupLine("\n[cyan]1. Menginstal/Update dependensi ProxySync (pip)...[/]");
            try {
                await ShellUtil.RunCommandAsync("pip", $"install --no-cache-dir --upgrade -r \"{ProxySyncReqs}\"", ProxySyncDir);
                AnsiConsole.MarkupLine("[green]   ✓ Dependensi ProxySync siap.[/]");
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]   Gagal menginstal dependensi: {ex.Message}[/]"); return;
            }
            AnsiConsole.MarkupLine("\n[cyan]2. Menjalankan Menu Interaktif ProxySync...[/]");
            AnsiConsole.MarkupLine("[dim]   (Anda akan masuk ke UI interaktif ProxySync)[/]");
            try {
                await ShellUtil.RunInteractive("python", $"\"{ProxySyncScript}\"", ProxySyncDir, null, cancellationToken);
            } catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]   ProxySync dibatalkan oleh user.[/]");
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]   Gagal menjalankan ProxySync: {ex.Message}[/]");
            }
            AnsiConsole.MarkupLine("\n[bold green]✅ Proses ProxySync selesai.[/]");
            AnsiConsole.MarkupLine("[dim]   File 'proxysync/success_proxy.txt' dan 'config/apilist.txt' mungkin telah diperbarui.[/]");
        }
    }
}
