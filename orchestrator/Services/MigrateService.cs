using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;
using Orchestrator.Core; // Menggunakan Core.BotConfig

namespace Orchestrator.Services // Namespace baru
{
    // Ganti nama kelas
    public static class MigrateService
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
        
        // === PERBAIKAN (dari file asli): Ganti nama ke plural dan baca SEMUA path ===
        private static List<string> GetOldPaths()
        {
            var paths = new List<string>();
            if (!File.Exists(OldPathFile))
            {
                AnsiConsole.MarkupLine($"[red]✗ File '{OldPathFile}' tidak ditemukan.[/]");
                AnsiConsole.MarkupLine($"[dim]   Buat file itu dan isi dengan path root lama (misal: D:\\SC\\PrivateKey)[/]");
                return paths;
            }
            try
            {
                var lines = File.ReadAllLines(OldPathFile);
                foreach (var line in lines)
                {
                    var path = line.Trim();
                    if (string.IsNullOrEmpty(path) || path.StartsWith("#"))
                        continue;

                    if (Directory.Exists(path))
                    {
                        AnsiConsole.MarkupLine($"[green]✓ Path Lama Ditemukan:[/] {path}");
                        paths.Add(path);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]✗ Path di '{OldPathFile}' ('{path}') tidak valid atau tidak ditemukan. Dilewati.[/]");
                    }
                }

                if (!paths.Any())
                {
                    AnsiConsole.MarkupLine($"[red]✗ Tidak ada path lama yang valid di '{OldPathFile}'.[/]");
                }
                
                return paths;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Gagal membaca '{OldPathFile}': {ex.Message.EscapeMarkup()}[/]");
                return paths;
            }
        }
        // === AKHIR PERBAIKAN ===

        private static List<string> LoadUploadFileList()
        {
            // (Logika tidak berubah)
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
            
            // === PERBAIKAN (dari file asli): Gunakan GetOldPaths() (plural) ===
            var oldRootPaths = GetOldPaths();
            if (oldRootPaths == null || !oldRootPaths.Any())
            {
                AnsiConsole.MarkupLine($"[red]✗ Tidak ada path root lama yang valid. Isi 'config/localpath.txt' terlebih dahulu.[/]");
                return;
            }
            
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
            AnsiConsole.MarkupLine($"[dim]Mencari di {oldRootPaths.Count} path root...[/]");
            AnsiConsole.MarkupLine("[yellow]PERINGATAN: File yang ada di tujuan (repo /bots/...) AKAN DITIMPA![/]");
            if (!AnsiConsole.Confirm("\n[bold orange1]Lanjutkan migrasi?[/]", false))
            {
                AnsiConsole.MarkupLine("[yellow]Migrasi dibatalkan.[/]");
                return;
            }

            int botsProcessed = 0;
            int filesCopied = 0;
            int filesSkipped = 0;
            int botsSkipped = 0;

            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn() })
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Memigrasi...[/]", new ProgressTaskSettings { MaxValue = config.BotsAndTools.Count });

                    foreach (var bot in config.BotsAndTools)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        
                        task.Description = $"[green]Cek:[/] {bot.Name}";

                        if (bot.Name == "ProxySync-Tool") 
                        {
                            task.Increment(1);
                            continue;
                        }
                        
                        var oldBotName = bot.Path.Split('/', '\\').Last();
                        string? foundOldBotDir = null;

                        // === PERBAIKAN (dari file asli): Cari folder bot di SEMUA root path ===
                        foreach (var rootPath in oldRootPaths)
                        {
                            string potentialPath = Path.Combine(rootPath, oldBotName);
                            if (Directory.Exists(potentialPath))
                            {
                                foundOldBotDir = potentialPath;
                                break; 
                            }
                        }

                        string newBotDir = BotConfig.GetLocalBotPath(bot.Path);

                        if (foundOldBotDir == null)
                        {
                            botsSkipped++;
                            task.Increment(1);
                            continue;
                        }
                        // === AKHIR PERBAIKAN ===
                        
                        botsProcessed++;
                        Directory.CreateDirectory(newBotDir); 

                        foreach (var fileName in filesToLookFor)
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            
                            string oldFilePath = Path.Combine(foundOldBotDir, fileName);
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
                        await Task.Delay(5, cancellationToken); 
                    }
                });
            
            AnsiConsole.MarkupLine($"\n[bold green]✅ Migrasi Selesai.[/]");
            AnsiConsole.MarkupLine($"[dim]   Bot ditemukan & diproses: {botsProcessed} (Dilewati: {botsSkipped})[/]");
            AnsiConsole.MarkupLine($"[dim]   File disalin: {filesCopied}[/]");
            AnsiConsole.MarkupLine($"[dim]   File gagal: {filesSkipped}[/]");
            AnsiConsole.MarkupLine($"[yellow]PENTING: Jika 'Bot diproses' masih 0, pastikan nama folder di D:\\SC... SAMA PERSIS dengan nama folder di 'path' bots_config.json.[/]");
            AnsiConsole.MarkupLine($"[red]Kredensial Anda sekarang ada di folder /bots/ di dalam repo ini. Folder ini sudah di-ignore oleh .gitignore.[/]");
        }
    }
}
