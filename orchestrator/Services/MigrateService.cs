using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core;
using Spectre.Console;

namespace Orchestrator.Services
{
    public static class MigrateService
    {
        public static async Task RunMigration(CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[bold yellow]Memulai Migrasi Kredensial Lokal...[/]");
            var config = BotConfig.Load();
            if (config == null)
            {
                AnsiConsole.MarkupLine("[red]✗ Gagal memuat bots_config.json.[/]");
                return;
            }

            var localPathConfig = Path.Combine(AppContext.BaseDirectory, "config", "localpath.txt");
            if (!File.Exists(localPathConfig))
            {
                AnsiConsole.MarkupLine($"[red]✗ File 'config/localpath.txt' tidak ditemukan.[/]");
                AnsiConsole.MarkupLine("[dim]File ini harus berisi path absolut ke folder D:/SC/ kalian, misal: 'D:\\SC'[/]");
                return;
            }
            
            string baseLocalPath = (await File.ReadAllTextAsync(localPathConfig, cancellationToken)).Trim();
            if (string.IsNullOrEmpty(baseLocalPath) || !Directory.Exists(baseLocalPath))
            {
                AnsiConsole.MarkupLine($"[red]✗ Path di 'config/localpath.txt' tidak valid: '{baseLocalPath}'[/]");
                return;
            }
            
            AnsiConsole.MarkupLine($"[dim]Base path lokal terdeteksi: {baseLocalPath.EscapeMarkup()}[/]");

            foreach (var bot in config.BotsAndTools)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(bot.RepoUrl) || !bot.RepoUrl.StartsWith("D:"))
                {
                    continue;
                }

                string oldPath = bot.RepoUrl;
                string relativePath = Path.GetRelativePath("D:\\SC", oldPath);
                string newRepoUrl = Path.Combine(baseLocalPath, relativePath);

                if (oldPath.Equals(newRepoUrl, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AnsiConsole.MarkupLine($"[cyan]Migrasi {bot.Name.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"  [red]- {oldPath.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"  [green]+ {newRepoUrl.EscapeMarkup()}[/]");
                
                bot.RepoUrl = newRepoUrl;
            }

            BotConfig.Save(config);
            AnsiConsole.MarkupLine("\n[green]✓ Migrasi selesai. 'config/bots_config.json' telah diperbarui.[/]");
        }
    }
}
