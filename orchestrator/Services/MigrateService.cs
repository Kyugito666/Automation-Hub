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
        private static readonly string OldPathFile = Path.Combine(ConfigRoot, "localpath.txt"); // <-- Sumber Path (SAMA UNTUK SEMUA)

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
        
        // Helper ini (GetOldPaths) sekarang dipakai oleh KEDUA menu
        private static List<string> GetOldPaths()
        {
            var paths = new List<string>();
            if (!File.Exists(OldPathFile))
            {
                AnsiConsole.MarkupLine($"[red]✗ File '{OldPathFile}' tidak ditemukan.[/]");
                AnsiConsole.MarkupLine($"[dim]   Buat file itu dan isi dengan path root (misal: D:\\SC\\PrivateKey)[/]");
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

        // Helper generik untuk baca file list (sync_files.txt ATAU upload_files.txt)
        private static List<string> LoadFileList(string configFileName, List<string> defaultList, string helpText)
        {
            string configFilePath = Path.Combine(ConfigRoot, configFileName);
            
            if (!File.Exists(configFilePath)) {
                AnsiConsole.MarkupLine($"[yellow]Warn: '{configFilePath}' tidak ditemukan.[/]");
                try 
                {
                    File.WriteAllText(configFilePath, $"# {helpText}\n# Contoh:\n# file1.txt\n# file2.js\n");
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

        // === FUNGSI LAMA (Menu 5): Sekarang jadi wrapper ===
        public static async Task RunMigration(CancellationToken cancellationToken = default)
        {
            var defaultList = new List<string> { 
                "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", 
                "config.json", "data.txt", "query.txt", "wallet.txt", 
                "settings.yaml", "mnemonics.txt" 
            };
            
            await RunFileSyncEngine(
                operationName: "Migrasi Kredensial", 
                fileListFileName: "upload_files.txt", // <-- Config File List
                defaultFileList: defaultList, 
                fileListHelpText: "Isi dengan nama file KREDENSIAL (misal: pk.txt)",
                cancellationToken: cancellationToken
            );
        }

        // === FUNGSI BARU (Menu 7): Wrapper baru ===
        public static async Task RunCustomScriptSync(CancellationToken cancellationToken = default)
        {
            var defaultList = new List<string>(); // Kosong, user wajib isi
            
            await RunFileSyncEngine(
                operationName: "Sinkronisasi Skrip", 
                fileListFileName: "sync_files.txt", // <-- Config File List (BARU)
                defaultFileList: defaultList, 
                fileListHelpText: "Isi dengan nama file SKRIP (misal: index.js)",
                cancellationToken: cancellationToken
            );
        }

        // === INI "MESIN" UTAMANYA ===
        // Dia otomatis pake localpath.txt, tapi file list-nya dinamis
        private static async Task RunFileSyncEngine(
            string operationName, 
            string fileListFileName, 
            List<string> defaultFileList, 
            string fileListHelpText,
            CancellationToken cancellationToken)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText(operationName).Color(Color.Orange1));
            AnsiConsole.MarkupLine($"[bold]Menyalin file dari '[yellow]{fileListFileName}[/]' (sumber: '[yellow]localpath.txt[/]') ke /bots/.[/]");
            
            // 1. Sumber path SELALU localpath.txt
            var oldRootPaths = GetOldPaths(); 
            if (oldRootPaths == null || !oldRootPaths.Any())
            {
                AnsiConsole.MarkupLine($"[red]✗ Tidak ada path root sumber yang valid. Isi 'config/localpath.txt' terlebih dahulu.[/]");
                return;
            }
            
            var config = BotConfig.Load();
            if (config == null || !config.BotsAndTools.Any())
            {
                AnsiConsole.MarkupLine("[red]✗ Gagal memuat 'bots_config.json'.[/]");
                return;
            }

            // 2. Sumber file list DINAMIS (sesuai argumen)
            var filesToLookFor = LoadFileList(fileListFileName, defaultFileList, fileListHelpText);
            if (!filesToLookFor.Any())
            {
                AnsiConsole.MarkupLine($"[yellow]! Tidak ada file terdaftar di 'config/{fileListFileName}'. Proses dilewati.[/]");
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

                        // Ini adalah logic folder-matching spesifik yang lu mau
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
            AnsiConsole.MarkupLine($"[yellow]PENTING: Jika 'Bot diproses' masih 0, pastikan nama folder di sumber SAMA PERSIS dengan nama folder di 'path' bots_config.json.[/]");
            AnsiConsole.MarkupLine($"[red]File Anda sekarang ada di folder /bots/ di dalam repo ini. Folder ini sudah di-ignore oleh .gitignore.[/]");
        }
    }
}
