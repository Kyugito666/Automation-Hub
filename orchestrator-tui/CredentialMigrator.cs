using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;

namespace Orchestrator;

public static class CredentialMigrator
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    private static readonly string OldPathFile = Path.Combine(ConfigRoot, "localpath.txt");
    private static readonly string UploadFilesListPath = Path.Combine(ConfigRoot, "upload_files.txt");

    private static string GetProjectRoot()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null) {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            if (Directory.Exists(configDir) && File.Exists(gitignore)) { return currentDir.FullName; }
            currentDir = currentDir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", ".."));
    }
    
    private static string? GetOldPath()
    {
        if (!File.Exists(OldPathFile))
        {
            AnsiConsole.MarkupLine($"[red]✗ File '{OldPathFile}' tidak ditemukan.[/]");
            AnsiConsole.MarkupLine($"[dim]   Buat file itu dan isi dengan path root lama (misal: D:\\SC)[/]");
            return null;
        }
        try
        {
            var path = File.ReadAllLines(OldPathFile)
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"));
            
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]✗ Path di '{OldPathFile}' ('{path ?? "N/A"}') tidak valid atau tidak ditemukan.[/]");
                return null;
            }
            AnsiConsole.MarkupLine($"[green]✓ Path Lama ditemukan:[/] {path}");
            return path;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Gagal membaca '{OldPathFile}': {ex.Message.EscapeMarkup()}[/]");
            return null;
        }
    }

    private static List<string> LoadUploadFileList()
    {
        // (Menggunakan logika yang sama persis seperti di CodespaceManager)
        if (!File.Exists(UploadFilesListPath)) {
            AnsiConsole.MarkupLine($"[yellow]Warn: '{UploadFilesListPath}' not found. Using defaults.[/]");
            return new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt", "wallet.txt", "settings.yaml", "mnemonics.txt" };
        }
        try {
            return File.ReadAllLines(UploadFilesListPath).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#")).ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error reading '{UploadFilesListPath}': {ex.Message.EscapeMarkup()}. Using defaults.[/]");
            return new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt", "wallet.txt", "settings.yaml", "mnemonics.txt" };
        }
    }

    public static async Task RunMigration(CancellationToken cancellationToken = default)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("Migrator").Color(Color.Orange1));
        AnsiConsole.MarkupLine("[bold]Memindahkan file kredensial LOKAL ke struktur repo.[/]");
        
        var oldRootPath = GetOldPath();
        if (oldRootPath == null) return;
        
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any())
        {
            AnsiConsole.MarkupLine("[red]✗ Gagal memuat 'bots_config.json'.[/]");
            return;
        }

        var filesToLookFor = LoadUploadFileList();
        if (!filesToLookFor.Any())
        {
            AnsiConsole.MarkupLine("[yellow]! Tidak ada file kredensial terdaftar di 'upload_files.txt'.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Akan memindai {config.BotsAndTools.Count} bot untuk {filesToLookFor.Count} jenis file...[/]");
        AnsiConsole.MarkupLine("[yellow]PERINGATAN: File yang ada di tujuan (repo /bots/...) AKAN DITIMPA![/]");
        if (!AnsiConsole.Confirm("\n[bold orange1]Lanjutkan migrasi?[/]", false))
        {
            AnsiConsole.MarkupLine("[yellow]Migrasi dibatalkan.[/]");
            return;
        }

        int botsProcessed = 0;
        int filesCopied = 0;
        int filesSkipped = 0;

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn() })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Memigrasi...[/]", new ProgressTaskSettings { MaxValue = config.BotsAndTools.Count });

                foreach (var bot in config.BotsAndTools)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    
                    task.Description = $"[green]Cek:[/] {bot.Name}";
                    
                    // Dapatkan path LAMA (D:\SC\...)
                    // Kita pakai logika lama dari BotUpdater/BotConfig
                    var oldBotName = bot.Path.Split('/', '\\').Last();
                    string oldBotDir;
                    if (bot.Path.Contains("privatekey", StringComparison.OrdinalIgnoreCase))
                        oldBotDir = Path.Combine(oldRootPath, "PrivateKey", oldBotName);
                    else if (bot.Path.Contains("token", StringComparison.OrdinalIgnoreCase))
                        oldBotDir = Path.Combine(oldRootPath, "Token", oldBotName);
                    else if (bot.Name == "ProxySync-Tool") // ProxySync tidak punya kredensial
                    {
                        task.Increment(1);
                        continue;
                    }
                    else
                        oldBotDir = Path.Combine(oldRootPath, oldBotName);

                    // Dapatkan path BARU (automation-hub/bots/...)
                    // Ini menggunakan logika BotConfig.cs yang SUDAH DIPERBARUI
                    string newBotDir = BotConfig.GetLocalBotPath(bot.Path);

                    if (!Directory.Exists(oldBotDir))
                    {
                        // AnsiConsole.MarkupLine($"[dim]SKIP {bot.Name}: Folder lama tidak ada ({oldBotDir.EscapeMarkup()})[/]");
                        task.Increment(1);
                        continue;
                    }
                    
                    botsProcessed++;
                    Directory.CreateDirectory(newBotDir); // Buat folder baru jika belum ada

                    foreach (var fileName in filesToLookFor)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        
                        string oldFilePath = Path.Combine(oldBotDir, fileName);
                        string newFilePath = Path.Combine(newBotDir, fileName);

                        if (File.Exists(oldFilePath))
                        {
                            try
                            {
                                task.Description = $"[cyan]Copy:[/] {bot.Name}/{fileName}";
                                File.Copy(oldFilePath, newFilePath, true); // Overwrite = true
                                filesCopied++;
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]GAGAL copy {fileName} ke {bot.Name}: {ex.Message.EscapeMarkup()}[/]");
                                filesSkipped++;
                            }
                        }
                    }
                    task.Increment(1);
                    await Task.Delay(10, cancellationToken);
                }
            });
        
        AnsiConsole.MarkupLine($"\n[bold green]✅ Migrasi Selesai.[/]");
        AnsiConsole.MarkupLine($"[dim]   Bot diproses: {botsProcessed}[/]");
        AnsiConsole.MarkupLine($"[dim]   File disalin: {filesCopied}[/]");
        AnsiConsole.MarkupLine($"[dim]   File gagal: {filesSkipped}[/]");
        AnsiConsole.MarkupLine($"[yellow]PENTING: Pastikan file di 'config/localpath.txt' sudah benar jika ada file yang terlewat.[/]");
        AnsiConsole.MarkupLine($"[red]Kredensial Anda sekarang ada di folder /bots/ di dalam repo ini. Folder ini sudah di-ignore oleh .gitignore.[/]");
    }
}
