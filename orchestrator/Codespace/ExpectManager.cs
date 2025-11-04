using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Spectre.Console;

namespace Orchestrator.Core
{
    public class ExpectStep
    {
        public string Expect { get; set; } = string.Empty;
        public string Send { get; set; } = string.Empty;
    }

    public static class ExpectManager
    {
        private const string SCRIPT_FILE_NAME = "expect_script.json";
        private static readonly string _configDir = Path.Combine(AppContext.BaseDirectory, "config");

        private static string GetScriptPath(string botPath)
        {
            string botDirName = Path.GetFileName(botPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return Path.Combine(_configDir, "expect_scripts", $"{botDirName}_{SCRIPT_FILE_NAME}");
        }

        public static bool CheckExpectScriptExists(string botPath)
        {
            return File.Exists(GetScriptPath(botPath));
        }

        public static List<ExpectStep>? LoadExpectScript(string botPath)
        {
            string filePath = GetScriptPath(botPath);
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<List<ExpectStep>>(json);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading expect script {filePath}: {ex.Message.EscapeMarkup()}[/]");
                return null;
            }
        }

        public static void SaveExpectScript(string botPath, List<ExpectStep> script)
        {
            string filePath = GetScriptPath(botPath);
            try
            {
                string dir = Path.GetDirectoryName(filePath) ?? throw new DirectoryNotFoundException("Could not get directory for expect script.");
                Directory.CreateDirectory(dir);
                
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(script, options);
                File.WriteAllText(filePath, json);
                AnsiConsole.MarkupLine($"[green]✓ Script setup disimpan ke {filePath.EscapeMarkup()}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error saving expect script {filePath}: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        public static void DeleteExpectScript(string botPath)
        {
            string filePath = GetScriptPath(botPath);
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    AnsiConsole.MarkupLine($"[green]✓ Script {filePath.EscapeMarkup()} dihapus.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Script {filePath.EscapeMarkup()} tidak ditemukan.[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error deleting expect script {filePath}: {ex.Message.EscapeMarkup()}[/]");
            }
        }
    }
}
