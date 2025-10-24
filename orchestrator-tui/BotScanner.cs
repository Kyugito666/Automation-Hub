using Spectre.Console;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Orchestrator;

public static class BotScanner
{
    private const string LogFile = "../.raw-bots.log";

    // Keyword berbahaya yang menandakan Raw Input
    private static readonly string[] PyRawKeywords = { "msvcrt.getch", "termios.tcsetattr", "tty.setraw" };
    private static readonly string[] JsRawKeywords = { ".setRawMode(true)", "process.stdin.on('data'" };
    
    // File entry point yang umum
    private static readonly string[] PyEntryPoints = { "run.py", "main.py", "bot.py" };
    private static readonly string[] JsEntryPoints = { "index.js", "main.js", "bot.js" };

    public static async Task ScanAllBots(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- Memulai Scan Kompatibilitas Input Bot ---[/]");
        AnsiConsole.MarkupLine("[dim]Menganalisa file kode... (Tidak menjalankan bot, proses cepat)[/]");
        
        var config = BotConfig.Load();
        if (config == null) return;

        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).ToList();
        var rawBots = new List<BotEntry>();

        var table = new Table().Title("Hasil Scan Kompatibilitas Input").Expand();
        table.AddColumn("Bot");
        table.AddColumn("Tipe");
        table.AddColumn("Status");
        table.AddColumn("Catatan");

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Scanning bots...[/]", new ProgressTaskSettings { MaxValue = bots.Count });

                foreach (var bot in bots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    task.Description = $"[green]Scanning:[/] {bot.Name}";

                    var (isRaw, note) = await IsBotRawInput(bot);

                    if (isRaw)
                    {
                        rawBots.Add(bot);
                        table.AddRow(bot.Name, bot.Type, "[yellow]RAW (Perlu Modif)[/]", $"[yellow]{note}[/]");
                    }
                    else
                    {
                        table.AddRow(bot.Name, bot.Type, "[green]Line (Kompatibel)[/]", $"[green]{note}[/]");
                    }
                    task.Increment(1);
                }
            });

        AnsiConsole.Write(table);

        // Tulis ke file log
        try
        {
            var logContent = new List<string>
            {
                "# Daftar bot yang terdeteksi menggunakan Raw Input (perlu modifikasi manual)",
                $"# Scan dijalankan pada: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                ""
            };
            logContent.AddRange(rawBots.Select(b => $"{b.Name} (Path: {b.Path})"));
            
            await File.WriteAllLinesAsync(LogFile, logContent, cancellationToken);
            
            AnsiConsole.MarkupLine($"\n[bold green]✓ {rawBots.Count} bot yang berpotensi 'Raw' telah disimpan ke:[/] [underline]{LogFile}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Gagal menyimpan file log: {ex.Message}[/]");
        }
    }

    private static async Task<(bool IsRaw, string Note)> IsBotRawInput(BotEntry bot)
    {
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath))
        {
            return (false, "Folder bot tidak ditemukan");
        }

        string[] entryPoints;
        string[] keywords;

        if (bot.Type == "python")
        {
            entryPoints = PyEntryPoints;
            keywords = PyRawKeywords;
        }
        else if (bot.Type == "javascript")
        {
            entryPoints = JsEntryPoints;
            keywords = JsRawKeywords;
        }
        else
        {
            return (false, "Tipe bot tidak dikenal");
        }

        foreach (var entry in entryPoints)
        {
            var filePath = Path.Combine(botPath, entry);
            if (File.Exists(filePath))
            {
                try
                {
                    // Baca file dengan toleransi (bisa sangat besar)
                    using var reader = new StreamReader(filePath);
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        foreach (var keyword in keywords)
                        {
                            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                return (true, $"Terdeteksi: '{keyword}' di {entry}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"Gagal baca {entry}: {ex.Message}");
                }
            }
        }
        
        return (false, "Tidak ada keyword 'raw input' ditemukan");
    }
}
