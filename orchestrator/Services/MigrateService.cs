using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;
using Orchestrator.Core; 
using System.Collections.Generic; // <-- Tambahkan
using System; // <-- Tambahkan

namespace Orchestrator.Services 
{
    public static class MigrateService
    {
        private static readonly string ProjectRoot = GetProjectRoot();
        private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
        private static readonly string OldPathFile = Path.Combine(ConfigRoot, "localpath.txt");
        // Hapus UploadFilesListPath, kita buat dinamis

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

        // === PERBAIKAN: Helper generik untuk baca file list ===
        private static List<string> LoadFileList(string fileName, List<string> defaultList)
        {
            string configFilePath = Path.Combine(ConfigRoot, fileName);
            
            if (!File.Exists(configFilePath)) {
                AnsiConsole.MarkupLine($"[yellow]Warn: '{configFilePath}' tidak ditemukan.[/]");
                // Coba buat filenya jika belum ada
                try 
                {
                    File.WriteAllText(configFilePath, $"# Isi dengan nama file yang akan disinkronkan (satu per baris)\n# Contoh:\n# index.js\n# main.py\n");
                    AnsiConsole.MarkupLine($"[green]✓ File '{configFilePath}' baru saja dibuatkan. Silakan isi.[/]");
                } 
                catch (Exception ex) 
                {
                    AnsiConsole.MarkupLine($"[red]Gagal buat file '{configFilePath}': {ex.Message.EscapeMarkup()}[/]");
                }
                return defaultList;
            }
            try {
                return File.ReadAllLines(configFilePath).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#")).ToList();
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error reading '{configFilePath}': {ex.Message.EscapeMarkup()}. Using defaults.[/]");
                return defaultList;
            }
        }

        // === FUNGSI BARU: Wrapper untuk Menu 5 (Kredensial) ===
        public static async Task RunMigration(CancellationToken cancellationToken = default)
        {
            var defaultList = new List<string> { 
                "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", 
                "config.json", "data.txt", "query.txt", "wallet.txt", 
                "settings.yaml", "mnemonics.txt" 
            };
            await RunFileSync(
                "Migrasi Kredensial", 
                "upload_files.txt", // Tetap pakai file lama
                defaultList, 
                cancellationToken
            );
        }

        // === FUNGSI BARU: Wrapper untuk Menu 7 (Skrip Kustom) ===
        public static async Task RunCustomScriptSync(CancellationToken cancellationToken = default)
        {
            // Daftar default-nya kosong, kita mau user yang isi manual
            var defaultList = new List<string>(); 
            await RunFileSync(
                "Sinkronisasi Skrip", 
                "sync_files.txt", // Pakai file config BARU
                defaultList, 
                cancellationToken
            );
        }

        // === PERBAIKAN: Ini adalah "Mesin" utamanya, digeneralisasi dari RunMigration ===
        private static async Task RunFileSync(string operationName, string configFileName, List<string> defaultList, CancellationToken cancellationToken)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText(operationName).Color(Color.Orange1));
            AnsiConsole.MarkupLine($"[bold]Memindahkan file LOKAL (dari '{configFileName}') ke struktur repo.[/]");
            
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

            var filesToLookFor = LoadFileList(configFileName, defaultList);
            if (!filesToLookFor.Any())
            {
                AnsiConsole.MarkupLine($"[yellow]! Tidak ada file terdaftar di '{configFileName}'. Proses dilewati.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[dim]Akan memindai {config.BotsAndTools.Count} bot untuk {filesToLookFor.Count} jenis file...[/]");
            AnsiConsole.MarkupLine($"[dim]Mencari di {oldRootPaths.Count} path root...[/]");
            AnsiConsole.MarkupLine("[yellow]PERINGATAN: File yang ada di tujuan (repo /bots/...) AKAN DITIMPA![/]");
            if (!AnsiConsole.Confirm($"\n[bold orange1]Lanjutkan {operationName}?[/]", false))
            {
                AnsiConsole.MarkupLine($"[yellow]{operationName} dibatalkan.[/]");
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
                    var task = ctx.AddTask("[green]Memproses...[/]", new ProgressTaskSettings { MaxValue = config.BotsAndTools.Count });

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
                                    File.Copy(oldFilePath, newFilePath, true); 
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
            
            AnsiConsole.MarkupLine($"\n[bold green]✅ {operationName} Selesai.[/]");
            AnsiConsole.MarkupLine($"[dim]   Bot ditemukan & diproses: {botsProcessed} (Dilewati: {botsSkipped})[/]");
            AnsiConsole.MarkupLine($"[dim]   File disalin: {filesCopied}[/]");
            AnsiConsole.MarkupLine($"[dim]   File gagal: {filesSkipped}[/]");
            AnsiConsole.MarkupLine($"[yellow]PENTING: Jika 'Bot diproses' masih 0, pastikan nama folder di D:\\SC... SAMA PERSIS dengan nama folder di 'path' bots_config.json.[/]");
            AnsiConsole.MarkupLine($"[red]File Anda sekarang ada di folder /bots/ di dalam repo ini. Folder ini sudah di-ignore oleh .gitignore.[/]");
        }
    }
}
