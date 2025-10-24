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

    // === KEYWORD DIPERBANYAK ===
    // Keyword Python
    private static readonly string[] PyRawKeywords = 
    { 
        "msvcrt.getch", 
        "termios.tcsetattr", 
        "tty.setraw",
        "readchar" // Library 'readchar'
    };
    
    // Keyword JavaScript (ditambah library TUI populer)
    private static readonly string[] JsRawKeywords = 
    { 
        ".setRawMode(true)", 
        "process.stdin.on('data'", 
        "process.stdin.on('keypress'", // Keyword penting
        "require('enquirer')",       // Library TUI
        "require('prompts')",        // Library TUI
        "require('inquirer')",       // Library TUI
        "require('keypress')"        // Library Raw Input
    };
    
    // File entry point (TIDAK DIPAKAI LAGI, TAPI DISIMPAN BUAT REFERENSI)
    // private static readonly string[] PyEntryPoints = { "run.py", "main.py", "bot.py" };
    // private static readonly string[] JsEntryPoints = { "index.js", "main.js", "bot.js" };

    public static async Task ScanAllBots(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- Memulai Scan Kompatibilitas Input Bot (Deep Scan) ---[/]");
        AnsiConsole.MarkupLine("[dim]Menganalisa SEMUA file kode... (Tidak menjalankan bot, proses cepat)[/]");
        
        var config = BotConfig.Load();
        if (config == null) return;

        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).ToList();
        var rawBots = new List<BotEntry>();

        var table = new Table().Title("Hasil Scan Kompatibilitas Input (Deep Scan)").Expand();
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

                    // === PANGGIL SCANNER BARU ===
                    var (isRaw, note) = await IsBotRawInputRecursive(bot, cancellationToken);

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

    // === METHOD SCAN DIRUBAH TOTAL ===
    private static async Task<(bool IsRaw, string Note)> IsBotRawInputRecursive(BotEntry bot, CancellationToken cancellationToken)
    {
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath))
        {
            return (false, "Folder bot tidak ditemukan");
        }

        string[] keywords;
        string searchPattern;

        if (bot.Type == "python")
        {
            keywords = PyRawKeywords;
            searchPattern = "*.py";
        }
        else if (bot.Type == "javascript")
        {
            keywords = JsRawKeywords;
            searchPattern = "*.js";
        }
        else
        {
            return (false, "Tipe bot tidak dikenal");
        }

        try
        {
            // Scan semua file di semua sub-folder
            var files = Directory.EnumerateFiles(botPath, searchPattern, SearchOption.AllDirectories);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Dapatkan path relatif untuk filtering
                var relativePath = Path.GetRelativePath(botPath, file);

                // Skip folder 'node_modules' atau '.git'
                if (relativePath.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase) || 
                    relativePath.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Scan file baris per baris
                try
                {
                    using var reader = new StreamReader(file);
                    string? line;
                    int lineNum = 0;
                    while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                    {
                        lineNum++;
                        foreach (var keyword in keywords)
                        {
                            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                            {
                                // KETEMU!
                                return (true, $"Terdeteksi: '{keyword}' di [bold]{relativePath}[/] (Line {lineNum})");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; } // Propagate cancel
                catch (Exception ex)
                {
                    return (false, $"Gagal baca {relativePath}: {ex.Message[..20]}...");
                }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Gagal scan folder: {ex.Message[..20]}...");
        }
        
        // Aman, nggak nemu apa-apa
        return (false, "Tidak ada keyword 'raw input' ditemukan (scan mendalam)");
    }
}
