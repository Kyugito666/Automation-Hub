using System.Text.Json;
using Spectre.Console;
using System.IO; 
using System.Threading.Tasks; 
using Orchestrator.Core; // <-- PERBAIKAN: Ditambahkan
using Orchestrator.Util; // <-- PERBAIKAN: Ditambahkan

namespace Orchestrator.Services 
{
    public static class UpdateService
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

        private static string GetLocalBotPathForUpdate(string configPath)
        {
            return BotConfig.GetLocalBotPath(configPath);
        }
        
        public static async Task UpdateAllBotsLocally()
        {
            var config = LoadConfig();
            if (config == null) return;

            AnsiConsole.MarkupLine($"[cyan]Mulai proses update LOKAL untuk {config.BotsAndTools.Count} entri...[/]");
            AnsiConsole.MarkupLine("[yellow]INFO: Bot akan di-update di folder /bots/ di dalam repo ini.[/]");

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
                
                string targetPath = GetLocalBotPathForUpdate(bot.Path);
                
                AnsiConsole.MarkupLine($"   [dim]Target: {targetPath.EscapeMarkup()}[/]");

                try 
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                    if (Directory.Exists(Path.Combine(targetPath, ".git")))
                    {
                        AnsiConsole.MarkupLine($"   Folder [yellow]{bot.Path}[/] ditemukan. Menjalankan 'git pull'...");
                        
                        bool hasChanges = false;
                        try
                        {
                            await ShellUtil.RunCommandAsync("git", "diff --quiet", targetPath);
                        }
                        catch
                        {
                            hasChanges = true;
                        }
                        
                        if (hasChanges)
                        {
                            AnsiConsole.MarkupLine("   [yellow]⚠ Unstaged changes terdeteksi. Melakukan stash...[/]");
                            
                            try
                            {
                                await ShellUtil.RunCommandAsync("git", "stash push -u -m \"Auto-stash by BotUpdater\"", targetPath);
                                AnsiConsole.MarkupLine("   [green]✓ Changes di-stash[/]");
                            }
                            catch (Exception stashEx)
                            {
                                AnsiConsole.MarkupLine($"   [red]✗ Gagal stash: {stashEx.Message}[/]");
                                AnsiConsole.MarkupLine("   [yellow]Mencoba hard reset...[/]");
                                
                                try
                                {
                                    await ShellUtil.RunCommandAsync("git", "fetch origin", targetPath);
                                    await ShellUtil.RunCommandAsync("git", "reset --hard origin/HEAD", targetPath);
                                    AnsiConsole.MarkupLine("   [yellow]✓ Hard reset dilakukan (local changes discarded)[/]");
                                }
                                catch (Exception resetEx)
                                {
                                    AnsiConsole.MarkupLine($"   [red]✗ Hard reset gagal: {resetEx.Message}[/]");
                                    throw;
                                }
                            }
                        }
                        
                        try
                        {
                            await ShellUtil.RunCommandAsync("git", "pull --no-rebase origin HEAD", targetPath);
                            AnsiConsole.MarkupLine("   [green]✓ Git pull berhasil[/]");
                            successCount++;
                        }
                        catch (Exception pullEx)
                        {
                            AnsiConsole.MarkupLine($"   [red]✗ Git pull gagal: {pullEx.Message}[/]");
                            
                            AnsiConsole.MarkupLine("   [yellow]Mencoba fetch + reset...[/]");
                            await ShellUtil.RunCommandAsync("git", "fetch origin", targetPath);
                            await ShellUtil.RunCommandAsync("git", "reset --hard origin/HEAD", targetPath);
                            AnsiConsole.MarkupLine("   [green]✓ Sync via fetch+reset berhasil[/]");
                            successCount++;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"   Folder [yellow]{bot.Path}[/] tidak ditemukan. Menjalankan 'git clone'...");
                        await ShellUtil.RunCommandAsync("git", $"clone --depth 1 {bot.RepoUrl} \"{targetPath}\"", ProjectRoot);
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
}
