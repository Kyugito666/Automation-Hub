using System.Text.Json;
using Spectre.Console;

namespace Orchestrator;

public static class BotUpdater
{
    // Path relatif dari executable TUI
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static BotConfig? LoadConfig()
    {
        return BotConfig.Load(); // Gunakan loader dari BotConfig
    }

    public static void ShowConfig()
    {
        var config = LoadConfig();
        if (config == null) return;

        var table = new Table().Title("Konfigurasi Bot & Tools (config/bots_config.json)").Expand();
        table.AddColumn("Name");
        table.AddColumn("Path (Relative)");
        table.AddColumn("Repo URL");
        table.AddColumn("Type");
        table.AddColumn("Enabled");

        foreach (var bot in config.BotsAndTools)
        {
            table.AddRow(
                bot.Name, 
                bot.Path, 
                bot.RepoUrl,
                bot.Type,
                bot.Enabled ? "[green]Yes[/]" : "[red]No[/]"
                );
        }
        AnsiConsole.Write(table);
    }
    
    /// <summary>
    /// Update SEMUA bot secara LOKAL. Berguna untuk setup awal atau debug.
    /// Di mode Codespace, ini TIDAK mempengaruhi environment remote.
    /// </summary>
    public static async Task UpdateAllBotsLocally()
    {
        var config = LoadConfig();
        if (config == null) return;

        AnsiConsole.MarkupLine($"[cyan]Mulai proses update LOKAL untuk {config.BotsAndTools.Count} entri...[/]");
        AnsiConsole.MarkupLine("[yellow]WARNING: Ini hanya meng-update salinan lokal, BUKAN di Codespace remote.[/]");

        int successCount = 0;
        int failCount = 0;

        foreach (var bot in config.BotsAndTools)
        {
            AnsiConsole.MarkupLine($"\n[bold cyan]--- Memproses Lokal: {bot.Name} ---[/]");
            
            if (string.IsNullOrEmpty(bot.Path) || string.IsNullOrEmpty(bot.RepoUrl))
            {
                AnsiConsole.MarkupLine("[yellow]   Entri tidak valid, skipping...[/]");
                failCount++;
                continue;
            }
            
            // Target path sekarang relatif terhadap ProjectRoot
            var targetPath = Path.Combine(ProjectRoot, bot.Path);

            try 
            {
                // Pastikan parent directory ada
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                if (Directory.Exists(Path.Combine(targetPath, ".git")))
                {
                    AnsiConsole.MarkupLine($"   Folder [yellow]{bot.Path}[/] ditemukan. Menjalankan 'git pull'...");
                    // Gunakan ShellHelper tanpa token (command lokal)
                    await ShellHelper.RunCommandAsync("git", "pull --rebase", targetPath);
                    successCount++;
                }
                else
                {
                    AnsiConsole.MarkupLine($"   Folder [yellow]{bot.Path}[/] tidak ditemukan. Menjalankan 'git clone'...");
                    await ShellHelper.RunCommandAsync("git", $"clone --depth 1 {bot.RepoUrl} \"{targetPath}\"", ProjectRoot);
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                 AnsiConsole.MarkupLine($"[red]   ✗ Gagal sync LOKAL untuk {bot.Name}: {ex.Message}[/]");
                 failCount++;
            }
        }
        AnsiConsole.MarkupLine($"\n[bold green]✅ Update LOKAL selesai. Berhasil: {successCount}, Gagal/Skip: {failCount}[/]");
    }
}
