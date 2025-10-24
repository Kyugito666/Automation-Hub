using Spectre.Console;

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
        AnsiConsole.MarkupLine("[dim](Ini mungkin butuh waktu lama untuk tes proxy...)[/]");
        await ShellHelper.RunStream("python", "main.py", ProxySyncPath);
        
        AnsiConsole.MarkupLine("\n[bold green]✅ Proses deploy proxy selesai.[/]");
    }
}
