using System.Text.Json;
using Spectre.Console;

namespace Orchestrator;

public static class BotUpdater
{
    private const string ConfigFile = "../config/bots_config.json";

    private static BotConfig? LoadConfig()
    {
        if (!File.Exists(ConfigFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: File konfig '{ConfigFile}' tidak ditemukan.[/]");
            return null;
        }
        var json = File.ReadAllText(ConfigFile);
        return JsonSerializer.Deserialize<BotConfig>(json);
    }

    public static void ShowConfig()
    {
        var config = LoadConfig();
        if (config == null) return;

        var table = new Table().Title("Konfigurasi Bot & Tools").Expand();
        table.AddColumn("Nama");
        table.AddColumn("Path (Tujuan)");
        table.AddColumn("URL Repository");

        foreach (var bot in config.BotsAndTools)
        {
            table.AddRow(bot.Name, bot.Path, bot.RepoUrl);
        }
        AnsiConsole.Write(table);
    }
    
    public static async Task UpdateAllBots()
    {
        var config = LoadConfig();
        if (config == null) return;

        AnsiConsole.MarkupLine($"[cyan]Mulai proses update untuk {config.BotsAndTools.Count} entri...[/]");

        foreach (var bot in config.BotsAndTools)
        {
            AnsiConsole.MarkupLine($"\n[bold cyan]--- Memproses: {bot.Name} ---[/]");
            
            if (string.IsNullOrEmpty(bot.Path) || string.IsNullOrEmpty(bot.RepoUrl))
            {
                AnsiConsole.MarkupLine("[yellow]Entri tidak valid, skipping...[/]");
                continue;
            }
            
            var targetPath = Path.Combine("..", bot.Path);

            if (Directory.Exists(targetPath))
            {
                AnsiConsole.MarkupLine($"Folder [yellow]{targetPath}[/] ditemukan. Menjalankan 'git pull'...");
                await ShellHelper.RunStream("git", "pull", targetPath);
            }
            else
            {
                AnsiConsole.MarkupLine($"Folder [yellow]{targetPath}[/] tidak ditemukan. Menjalankan 'git clone'...");
                await ShellHelper.RunStream("git", $"clone {bot.RepoUrl} {targetPath}");
            }
        }
        AnsiConsole.MarkupLine("\n[bold green]✅ Semua bot & tools berhasil di-clone/update.[/]");
    }
}
