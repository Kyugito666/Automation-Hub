using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.IO;
using Spectre.Console;
using System;
using System.Linq; // Tambahkan

namespace Orchestrator.Core
{
    // Model untuk menyimpan satu langkah interaksi
    public class ExpectStep
    {
        [JsonPropertyName("expect")]
        public string Expect { get; set; } = string.Empty;

        [JsonPropertyName("send")]
        public string Send { get; set; } = string.Empty;
    }

    public static class ExpectManager
    {
        private const string EXPECT_FILENAME = "autostart.json";
        
        private static readonly string ProjectRoot = GetProjectRoot();
        
        private static string GetProjectRoot()
        {
            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (currentDir != null)
            {
                var configDir = Path.Combine(currentDir.FullName, "config");
                var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
                
                if (Directory.Exists(configDir) && File.Exists(gitignore))
                {
                    return currentDir.FullName;
                }
                currentDir = currentDir.Parent;
            }
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", ".."));
        }

        public static void SaveExpectScript(string botPath, List<ExpectStep> script)
        {
            try
            {
                var fullLocalPath = BotConfig.GetLocalBotPath(botPath);
                var filePath = Path.Combine(fullLocalPath, EXPECT_FILENAME);
                
                // Pastikan folder ada
                if (!Directory.Exists(fullLocalPath)) Directory.CreateDirectory(fullLocalPath);
                
                var json = JsonSerializer.Serialize(script, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                AnsiConsole.MarkupLine($"\n[green]✓ Setup interaktif berhasil direkam ke '{Path.GetFileName(filePath)}'[/green]");
                AnsiConsole.MarkupLine("[dim]   Bot akan otomatis menggunakan script ini untuk Autostart (di Codespace).[/dim]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Gagal menyimpan script expect: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        public static List<ExpectStep>? LoadExpectScript(string botPath)
        {
             try
            {
                var fullLocalPath = BotConfig.GetLocalBotPath(botPath);
                var filePath = Path.Combine(fullLocalPath, EXPECT_FILENAME);

                if (!File.Exists(filePath)) return null;

                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<List<ExpectStep>>(json);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Gagal memuat script expect: {ex.Message.EscapeMarkup()}[/]");
                return null;
            }
        }

        public static bool CheckExpectScriptExists(string botPath)
        {
            var filePath = Path.Combine(BotConfig.GetLocalBotPath(botPath), EXPECT_FILENAME);
            return File.Exists(filePath);
        }
        
        public static bool DeleteExpectScript(string botPath)
        {
             try
             {
                var filePath = Path.Combine(BotConfig.GetLocalBotPath(botPath), EXPECT_FILENAME);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    AnsiConsole.MarkupLine($"[green]✓ Script expect di {botPath} berhasil dihapus.[/]");
                    return true;
                }
                return false;
             }
             catch (Exception ex)
             {
                AnsiConsole.MarkupLine($"[red]✗ Gagal menghapus script expect: {ex.Message.EscapeMarkup()}[/]");
                return false;
             }
        }
    }
}
