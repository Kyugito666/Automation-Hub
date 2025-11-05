using Spectre.Console;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Core
{
    // Struktur untuk bots_config.json
    internal class BotConfig
    {
        [JsonPropertyName("bots_and_tools")]
        public List<BotEntry> BotsAndTools { get; set; } = new List<BotEntry>();

        private static string? _configDirectory = null;
        private static string? _configFilePath = null;
        private static string? _projectRootPath = null;
        private static string? _localBotsPath = null;

        // Mendapatkan direktori config (biasanya .../Automation-Hub/config)
        public static string GetConfigDirectory()
        {
            if (_configDirectory == null)
            {
                _configDirectory = Path.Combine(GetProjectRootPath(), "config");
                Directory.CreateDirectory(_configDirectory);
            }
            return _configDirectory;
        }

        // Mendapatkan path file config (config/bots_config.json)
        public static string GetConfigFilePath()
        {
            if (_configFilePath == null)
            {
                _configFilePath = Path.Combine(GetConfigDirectory(), "bots_config.json");
            }
            return _configFilePath;
        }

        // Mendapatkan path root project (Automation-Hub)
        public static string GetProjectRootPath()
        {
            if (_projectRootPath == null)
            {
                var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
                // Cari ke atas sampai menemukan folder yang berisi ".git" atau "config"
                while (currentDir != null)
                {
                    if (Directory.Exists(Path.Combine(currentDir.FullName, ".git")) || Directory.Exists(Path.Combine(currentDir.FullName, "config")))
                    {
                        _projectRootPath = currentDir.FullName;
                        return _projectRootPath;
                    }
                    currentDir = currentDir.Parent;
                }
                // Fallback jika tidak ditemukan (misal di-run dari tempat aneh)
                _projectRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
                 AnsiConsole.MarkupLine($"[yellow]Warn: Tidak bisa auto-detect root via .git/config. Menggunakan path fallback: {_projectRootPath.EscapeMarkup()}[/]");
            }
            return _projectRootPath;
        }

        // Mendapatkan path LOKAL folder 'bots' (dari localpath.txt)
        public static string GetLocalBotsPath()
        {
            if (_localBotsPath == null)
            {
                _localBotsPath = TokenManager.GetLocalPath(); // Manfaatkan logic yang sudah ada
            }
            return _localBotsPath;
        }
        
        // Mendapatkan path LOKAL bot spesifik (misal: D:/MyBots/privatekey/namabot)
        public static string GetLocalBotPath(string relativeBotPath)
        {
            return Path.Combine(GetLocalBotsPath(), relativeBotPath);
        }


        // Method untuk load config
        public static BotConfig? Load(bool validateOnly = false)
        {
            string configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                if (validateOnly)
                {
                    AnsiConsole.MarkupLine($"[red]✗ BotConfig:[/red] File '{Path.GetFileName(configPath)}' not found.");
                    return null;
                }
                
                AnsiConsole.MarkupLine($"[yellow]File '{configPath.EscapeMarkup()}' not found. Creating default config...[/]");
                return CreateDefaultConfig(configPath);
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<BotConfig>(json);

                if (config == null || config.BotsAndTools == null)
                {
                     throw new JsonException("Config file is invalid or empty.");
                }
                
                if (!validateOnly)
                {
                    AnsiConsole.MarkupLine($"[dim]Config '{Path.GetFileName(configPath)}' loaded ({config.BotsAndTools.Count} entries).[/dim]");
                }
                return config;
            }
            catch (JsonException ex)
            {
                AnsiConsole.MarkupLine($"[red]FATAL: Error parsing '{configPath.EscapeMarkup()}'.[/]");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
                if (validateOnly) return null;
                throw; // Re-throw jika bukan validation
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]FATAL: Failed to read config file '{configPath.EscapeMarkup()}'.[/]");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
                if (validateOnly) return null;
                throw;
            }
        }

        // Method untuk buat config default
        private static BotConfig CreateDefaultConfig(string configPath)
        {
            var defaultConfig = new BotConfig
            {
                BotsAndTools = new List<BotEntry>
                {
                    new BotEntry
                    {
                        Name = "ProxySync-Tool",
                        Path = "proxysync",
                        RepoUrl = "https://github.com/Kyugito666/ProxySync-Tool.git",
                        Type = "python",
                        Enabled = true
                    },
                    new BotEntry
                    {
                        Name = "ContohBot-Python",
                        Path = "privatekey/contohbot-python",
                        RepoUrl = "https://github.com/Username/RepoBotPython.git",
                        Type = "python",
                        Enabled = false
                    },
                    new BotEntry
                    {
                        Name = "ContohBot-NodeJS",
                        Path = "token/contohbot-nodejs",
                        RepoUrl = "https://github.com/Username/RepoBotNode.git",
                        Type = "javascript",
                        Enabled = false
                    }
                }
            };

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                string newJson = JsonSerializer.Serialize(defaultConfig, options);
                File.WriteAllText(configPath, newJson);
                AnsiConsole.MarkupLine($"[green]✓ Default config file created at '{configPath.EscapeMarkup()}'[/]");
                AnsiConsole.MarkupLine("[yellow]Please edit this file to add your bots.[/]");
                return defaultConfig;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]FATAL: Failed to create default config file at '{configPath.EscapeMarkup()}'.[/]");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
                throw;
            }
        }
    }

    // Struktur entri untuk setiap bot/tool
    internal class BotEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";
        
        [JsonPropertyName("repo_url")]
        public string RepoUrl { get; set; } = "";
        
        [JsonPropertyName("type")]
        public string Type { get; set; } = "python"; // "python" atau "javascript"
        
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;
    }
}
