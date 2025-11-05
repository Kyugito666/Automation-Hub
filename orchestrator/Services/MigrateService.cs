using Spectre.Console;
using System.IO;
using System.Text.Json;
using Orchestrator.Core; // Diperlukan untuk BotConfig
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orchestrator.Services
{
    internal static class MigrateService
    {
        private const string OldConfigName = "config.json";
        private const string NewConfigName = "bots_config.json";
        private const string BackupConfigName = "config.json.migrated_backup";
        private const string OldLocalPathName = "localpath.txt";

        internal static void CheckAndRunMigration()
        {
            // Dapatkan path config root dari BotConfig
            string configDir = BotConfig.GetConfigDirectory();
            string oldConfigPath = Path.Combine(configDir, OldConfigName);
            string newConfigPath = Path.Combine(configDir, NewConfigName);
            string backupConfigPath = Path.Combine(configDir, BackupConfigName);
            string oldLocalPathFile = Path.Combine(configDir, OldLocalPathName);

            // Cek 1: Apakah config BARU sudah ada?
            if (File.Exists(newConfigPath))
            {
                // Config baru ada.
                // Cek apakah config LAMA masih ada (artinya migrasi sudah jalan, tapi file lama belum dihapus)
                if (File.Exists(oldConfigPath))
                {
                    AnsiConsole.MarkupLine($"[dim]Note: Found old '{OldConfigName}'. Renaming to '{BackupConfigName}' to prevent conflicts...[/]");
                    try
                    {
                        if (File.Exists(backupConfigPath)) File.Delete(backupConfigPath);
                        File.Move(oldConfigPath, backupConfigPath);
                        AnsiConsole.MarkupLine($"[dim]✓ Renamed.[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warn: Could not rename old config: {ex.Message.EscapeMarkup()}[/]");
                    }
                }
                // Cek 2: Hapus localpath.txt jika ada
                if (File.Exists(oldLocalPathFile))
                {
                    AnsiConsole.MarkupLine($"[dim]Note: Found old '{OldLocalPathName}'. This file is no longer used and will be deleted...[/]");
                    try
                    {
                        File.Delete(oldLocalPathFile);
                        AnsiConsole.MarkupLine($"[dim]✓ Deleted.[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warn: Could not delete old '{OldLocalPathName}': {ex.Message.EscapeMarkup()}[/]");
                    }
                }
                
                // Config baru sudah ada, migrasi tidak diperlukan.
                return;
            }

            // Cek 3: Config BARU tidak ada. Apakah config LAMA ada?
            if (!File.Exists(oldConfigPath))
            {
                // Tidak ada config sama sekali. Ini adalah setup baru.
                // Biarkan BotConfig.Load() yang menghandle pembuatan file baru.
                return;
            }

            // --- Mulai Migrasi ---
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold yellow]======================================================[/]");
            AnsiConsole.MarkupLine("[bold yellow]  MIGRATION REQUIRED: 'config.json' -> 'bots_config.json'  [/]");
            AnsiConsole.MarkupLine("[bold yellow]======================================================[/]");
            AnsiConsole.MarkupLine("\nStruktur config telah berubah (v4.0.0).");
            AnsiConsole.MarkupLine("File 'config.json' lama Anda akan dimigrasikan ke format 'bots_config.json' yang baru.");
            AnsiConsole.MarkupLine("\n[cyan]Memulai migrasi...[/]");

            try
            {
                // Baca config lama
                string oldJson = File.ReadAllText(oldConfigPath);
                var oldConfig = JsonSerializer.Deserialize<OldBotConfig>(oldJson);

                if (oldConfig == null || oldConfig.bots == null)
                {
                    throw new Exception("Format 'config.json' lama tidak valid atau kosong.");
                }

                AnsiConsole.MarkupLine($"[dim]Membaca {oldConfig.bots.Count} bot dari '{OldConfigName}'...[/]");

                // Buat struktur config baru
                var newConfig = new BotConfig
                {
                    BotsAndTools = new List<BotEntry>()
                };

                // Tambahkan ProxySync-Tool sebagai entri pertama
                newConfig.BotsAndTools.Add(new BotEntry
                {
                    Name = "ProxySync-Tool",
                    Path = "proxysync",
                    RepoUrl = "https://github.com/Kyugito666/ProxySync-Tool.git", // Ganti jika perlu
                    Type = "python",
                    Enabled = true // Selalu enabled
                });

                // Migrasikan bot lama
                foreach (var oldBot in oldConfig.bots)
                {
                    if (string.IsNullOrWhiteSpace(oldBot.name) || string.IsNullOrWhiteSpace(oldBot.path))
                    {
                        AnsiConsole.MarkupLine($"[yellow]Melewatkan entri bot lama yang tidak valid: {oldBot.name}[/]");
                        continue;
                    }

                    // Asumsi path lama: "privatekey/namabot"
                    string botType = oldBot.path.StartsWith("token/") ? "javascript" : "python";
                    
                    // Buat URL Repo (ini hanya tebakan, mungkin perlu diedit manual oleh user)
                    string repoUrl = $"https://github.com/YourUsername/{oldBot.name}.git"; // User HARUS ganti ini
                    
                    newConfig.BotsAndTools.Add(new BotEntry
                    {
                        Name = oldBot.name,
                        Path = oldBot.path,
                        RepoUrl = repoUrl,
                        Type = botType,
                        Enabled = oldBot.enabled
                    });
                }
                
                AnsiConsole.MarkupLine($"[dim]Total {newConfig.BotsAndTools.Count} entri dibuat (termasuk ProxySync).[/]");

                // Tulis config baru
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                string newJson = JsonSerializer.Serialize(newConfig, options);
                File.WriteAllText(newConfigPath, newJson);

                AnsiConsole.MarkupLine($"[green]✓ Migrasi sukses! '{NewConfigName}' telah dibuat.[/green]");
                
                // Ganti nama config lama
                File.Move(oldConfigPath, backupConfigPath);
                AnsiConsole.MarkupLine($"[dim]File '{OldConfigName}' lama telah di-backup ke '{BackupConfigName}'.[/]");
                
                AnsiConsole.MarkupLine("\n[bold red]PERHATIAN:[/]");
                AnsiConsole.MarkupLine($"[yellow]Migrasi HANYA menebak 'repo_url'.[/yellow]");
                AnsiConsole.MarkupLine($"[yellow]Harap buka '[white]{NewConfigName}[/]' dan [bold]EDIT SEMUA 'repo_url'[/] agar sesuai dengan repo Git Anda![/yellow]");

            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[bold red]MIGRATION FAILED![/]");
                AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"[dim]Stack: {ex.StackTrace?.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine("\nAplikasi akan ditutup. Harap perbaiki 'config.json' lama atau buat 'bots_config.json' baru secara manual.");
                throw new Exception("Migration failed.", ex); // Hentikan aplikasi
            }
            
            // Hapus localpath.txt jika ada
            if (File.Exists(oldLocalPathFile))
            {
                 AnsiConsole.MarkupLine($"[dim]Menghapus file '{OldLocalPathName}' lama...[/]");
                 try
                 {
                     File.Delete(oldLocalPathFile);
                 }
                 catch (Exception ex)
                 {
                     AnsiConsole.MarkupLine($"[yellow]Warn: Could not delete old '{OldLocalPathName}': {ex.Message.EscapeMarkup()}[/]");
                 }
            }

            AnsiConsole.MarkupLine("\n[bold]Tekan Enter untuk melanjutkan...[/bold]");
            Console.ReadLine();
        }
    }

    // Struktur kelas untuk config LAMA (hanya untuk deserialisasi)
    internal class OldBotConfig
    {
        public List<OldBotEntry>? bots { get; set; }
    }

    internal class OldBotEntry
    {
        public string? name { get; set; }
        public string? path { get; set; }
        public bool enabled { get; set; } = false;
    }
}
