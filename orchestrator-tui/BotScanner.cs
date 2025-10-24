using Spectre.Console;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text.RegularExpressions; // Ditambahkan untuk Regex

namespace Orchestrator;

public static class BotScanner
{
    private const string LogFile = "../.raw-bots.log";

    // Keyword Python
    private static readonly string[] PyRawKeywords = 
    { 
        "msvcrt.getch", 
        "termios.tcsetattr", 
        "tty.setraw",
        "readchar" 
    };
    
    // Keyword JavaScript (ditambah readline-sync, getch)
    private static readonly string[] JsRawKeywords = 
    { 
        ".setRawMode(true)", 
        "process.stdin.on('data'", 
        "process.stdin.on('keypress'", 
        "require('enquirer')",       
        "require('prompts')",        
        "require('inquirer')",       
        "require('keypress')",
        "require('readline-sync')", // <-- BARU
        ".question\\(",             // <-- BARU (readline-sync pattern)
        "require('getch')"          // <-- BARU (npm i getch)
    };
    
    // Pattern untuk skip folder venv/library
    private static readonly Regex SkipDirPattern = new Regex(
        @"(/|\\)(node_modules|Lib(/|\\)site-packages|lib(/|\\)python\d\.\d+(/|\\)site-packages|.git|.venv|venv|myenv)(/|\\|$)", 
        RegexOptions.IgnoreCase | RegexOptions.Compiled);


    public static async Task ScanAllBots(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- Memulai Scan Kompatibilitas Input Bot (Deep Scan v3) ---[/]");
        AnsiConsole.MarkupLine("[dim]Menganalisa SEMUA file kode (skip library)... (Tidak menjalankan bot, proses cepat)[/]");
        
        var config = BotConfig.Load();
        if (config == null) return;

        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).ToList();
        var rawBots = new List<BotEntry>();

        var table = new Table().Title("Hasil Scan Kompatibilitas Input (Deep Scan v3)").Expand();
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
            AnsiConsole.MarkupLine("[yellow]CATATAN: Scan ini mungkin masih melewatkan library TUI yang tidak umum. Verifikasi manual tetap disarankan.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Gagal menyimpan file log: {ex.Message}[/]");
        }
    }

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
                
                // === LOGIKA SKIP BARU ===
                // Skip folder library/venv/git
                if (SkipDirPattern.IsMatch(file)) 
                {
                    // AnsiConsole.MarkupLine($"[grey]Skipping library/venv file: {relativePath}[/]"); // Uncomment for debugging
                    continue;
                }
                // === AKHIR LOGIKA SKIP ===


                // Scan file baris per baris
                try
                {
                    using var reader = new StreamReader(file);
                    string? line;
                    int lineNum = 0;
                    while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                    {
                        lineNum++;
                        // Skip komentar (sederhana)
                        if (line.TrimStart().StartsWith("#") || line.TrimStart().StartsWith("//")) continue;

                        foreach (var keyword in keywords)
                        {
                            // Gunakan Regex untuk keyword JS tertentu agar lebih akurat
                            bool match = keyword.Contains("\\") // Cek apakah keyword butuh Regex
                                ? Regex.IsMatch(line, keyword, RegexOptions.IgnoreCase)
                                : line.Contains(keyword, StringComparison.OrdinalIgnoreCase);

                            if (match)
                            {
                                // KETEMU!
                                return (true, $"Terdeteksi: '{keyword}' di [bold]{relativePath.EscapeMarkup()}[/] (Line {lineNum})");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; } // Propagate cancel
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Skip file error: Gagal baca {relativePath}: {ex.Message.Split('\n')[0].EscapeMarkup()}[/]");
                    // return (false, $"Gagal baca {relativePath}: {ex.Message[..Math.Min(ex.Message.Length,30)]}...");
                }
            }
        }
        catch (OperationCanceledException) { throw; } // Propagate cancel
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Scan folder error: {ex.Message.Split('\n')[0].EscapeMarkup()}[/]");
            // return (false, $"Gagal scan folder: {ex.Message[..Math.Min(ex.Message.Length, 30)]}...");
        }
        
        // Aman, nggak nemu apa-apa
        return (false, "Tidak ada keyword 'raw input' ditemukan (scan mendalam v3)");
    }
}
