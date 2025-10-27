using System.Text.Json;
using Spectre.Console;

namespace Orchestrator;

public static class BotUpdater
{
    private static readonly string ProjectRoot = GetProjectRoot();

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

    private static BotConfig? LoadConfig()
    {
        return BotConfig.Load();
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
    
    public static async Task UpdateAllBotsLocally()
    {
        var config = LoadConfig();
        if (config == null) return;

        AnsiConsole.MarkupLine($"[cyan]Mulai proses update LOKAL untuk {config.BotsAndTools.Count} entri...[/]");
        AnsiConsole.MarkupLine("[yellow]INFO: Bot akan di-update di D:\\SC\\PrivateKey dan D:\\SC\\Token[/]");

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
            
            // === PERBAIKAN: Gunakan path D:\SC ===
            string targetPath = GetLocalBotPathForUpdate(bot.Path);
            
            AnsiConsole.MarkupLine($"   [dim]Target: {targetPath.EscapeMarkup()}[/]");

            try 
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                if (Directory.Exists(Path.Combine(targetPath, ".git")))
                {
                    AnsiConsole.MarkupLine($"   Folder [yellow]{bot.Path}[/] ditemukan. Menjalankan 'git pull'...");
                    
                    // === PERBAIKAN UTAMA: Handle unstaged changes ===
                    
                    // Opsi 1: Cek status dulu
                    bool hasChanges = false;
                    try
                    {
                        await ShellHelper.RunCommandAsync("git", "diff --quiet", targetPath);
                    }
                    catch
                    {
                        hasChanges = true;
                    }
                    
                    if (hasChanges)
                    {
                        AnsiConsole.MarkupLine("   [yellow]⚠ Unstaged changes terdeteksi. Melakukan stash...[/]");
                        
                        // Stash perubahan lokal
                        try
                        {
                            await ShellHelper.RunCommandAsync("git", "stash push -u -m \"Auto-stash by BotUpdater\"", targetPath);
                            AnsiConsole.MarkupLine("   [green]✓ Changes di-stash[/]");
                        }
                        catch (Exception stashEx)
                        {
                            AnsiConsole.MarkupLine($"   [red]✗ Gagal stash: {stashEx.Message}[/]");
                            AnsiConsole.MarkupLine("   [yellow]Mencoba hard reset...[/]");
                            
                            // Fallback: Hard reset (HATI-HATI: Menghapus perubahan lokal!)
                            try
                            {
                                await ShellHelper.RunCommandAsync("git", "fetch origin", targetPath);
                                await ShellHelper.RunCommandAsync("git", "reset --hard origin/HEAD", targetPath);
                                AnsiConsole.MarkupLine("   [yellow]✓ Hard reset dilakukan (local changes discarded)[/]");
                            }
                            catch (Exception resetEx)
                            {
                                AnsiConsole.MarkupLine($"   [red]✗ Hard reset gagal: {resetEx.Message}[/]");
                                throw; // Re-throw untuk ditangani di outer catch
                            }
                        }
                    }
                    
                    // Pull dengan strategy yang lebih aman
                    try
                    {
                        // Gunakan --rebase=false untuk menghindari konflik rebase
                        await ShellHelper.RunCommandAsync("git", "pull --no-rebase origin HEAD", targetPath);
                        AnsiConsole.MarkupLine("   [green]✓ Git pull berhasil[/]");
                        successCount++;
                    }
                    catch (Exception pullEx)
                    {
                        AnsiConsole.MarkupLine($"   [red]✗ Git pull gagal: {pullEx.Message}[/]");
                        
                        // Jika pull gagal, coba fetch + reset
                        AnsiConsole.MarkupLine("   [yellow]Mencoba fetch + reset...[/]");
                        await ShellHelper.RunCommandAsync("git", "fetch origin", targetPath);
                        await ShellHelper.RunCommandAsync("git", "reset --hard origin/HEAD", targetPath);
                        AnsiConsole.MarkupLine("   [green]✓ Sync via fetch+reset berhasil[/]");
                        successCount++;
                    }
                    
                    // === AKHIR PERBAIKAN ===
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
