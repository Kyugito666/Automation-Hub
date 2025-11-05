using Spectre.Console;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Core
{
    // ... (TokenEntry dan TokenState tetap sama) ...
    internal class TokenEntry
    {
        public string Owner { get; set; } = "";
        public string Repo { get; set; } = "";
        public string Token { get; set; } = "";
        public string? Proxy { get; set; }
        public string? Username { get; set; }
    }

    internal class TokenState
    {
        [JsonPropertyName("current_index")]
        public int CurrentIndex { get; set; } = 0;
        
        [JsonPropertyName("active_codespace_name")]
        public string? ActiveCodespaceName { get; set; } = null;
    }

    internal static class TokenManager
    {
        private static List<TokenEntry> _tokens = new List<TokenEntry>();
        private static List<string> _proxies = new List<string>();
        private static int _currentProxyIndex = 0;
        private static bool _proxyEnabled = true;
        
        private static string _localPath = "";

        private static readonly string ConfigDir = BotConfig.GetConfigDirectory();
        private static readonly string TokenFile = Path.Combine(ConfigDir, "github_tokens.txt");
        private static readonly string StateFile = Path.Combine(ConfigDir, "orchestrator_state.json");
        private static readonly string LocalPathFile = Path.Combine(ConfigDir, "localpath.txt"); 
        private static readonly string ProxyFile = Path.Combine(BotConfig.GetProjectRootPath(), "proxysync", "success_proxy.txt");
        private static readonly string FallbackProxyFile = Path.Combine(BotConfig.GetProjectRootPath(), "proxysync", "proxy.txt");

        public static void ReloadAllConfigs(bool validateOnly = false)
        {
            if (!validateOnly) AnsiConsole.MarkupLine("[dim]Reloading all configurations...[/dim]");
            
            // 1. Load Local Path
            _localPath = LoadLocalPath(validateOnly);
            if (string.IsNullOrEmpty(_localPath) && !validateOnly)
            {
                AnsiConsole.MarkupLine($"[red]FATAL: Local path in '{Path.GetFileName(LocalPathFile)}' is not set.[/]");
                AnsiConsole.MarkupLine("[dim]Please run 'Orchestrator.exe --init' or edit the file manually.[/]");
                throw new Exception("Local path configuration is missing or invalid.");
            }
            if (validateOnly) AnsiConsole.MarkupLine($"[green]✓ Local Path:[/green] '{_localPath.EscapeMarkup()}'");


            // 2. Load Tokens
            var (owner, repo, tokens) = LoadTokensFromFile(validateOnly);
            if (!tokens.Any() && !validateOnly)
            {
                 AnsiConsole.MarkupLine($"[red]FATAL: No valid GitHub tokens found in '{Path.GetFileName(TokenFile)}'.[/]");
                 throw new Exception("Token configuration is missing or invalid.");
            }
            if (validateOnly) AnsiConsole.MarkupLine($"[green]✓ Tokens:[/green] {tokens.Count} tokens loaded for repo '{owner}/{repo}'.");
            
            // 3. Load Proxies
            _proxies = LoadProxiesFromFile(validateOnly);
            if (validateOnly) AnsiConsole.MarkupLine($"[green]✓ Proxies:[/green] {_proxies.Count} proxies loaded.");

            // 4. Assign Tokens & Proxies
            _tokens.Clear();
            _currentProxyIndex = 0;
            int proxyCount = _proxies.Count;
            int tokenIndex = 0;

            foreach (var tokenStr in tokens)
            {
                var entry = new TokenEntry
                {
                    Owner = owner,
                    Repo = repo,
                    Token = tokenStr,
                    Proxy = proxyCount > 0 ? _proxies[tokenIndex % proxyCount] : null,
                    Username = $"Token_{tokenIndex + 1}" // Default username
                };
                _tokens.Add(entry);
                tokenIndex++;
            }
            
            if (!validateOnly) AnsiConsole.MarkupLine($"[dim]Reload complete. {_tokens.Count} tokens and {_proxies.Count} proxies loaded.[/dim]");
        }

        private static string LoadLocalPath(bool validateOnly)
        {
            if (!File.Exists(LocalPathFile))
            {
                if (validateOnly)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Local Path:[/red] File '{Path.GetFileName(LocalPathFile)}' not found.");
                    return "";
                }
                
                AnsiConsole.MarkupLine($"[yellow]File '{Path.GetFileName(LocalPathFile)}' not found. Creating...[/]");
                string defaultPath = Path.GetFullPath(Path.Combine(BotConfig.GetProjectRootPath(), "..", "MyBots"));
                try
                {
                    File.WriteAllText(LocalPathFile, "# Masukkan path absolut ke folder LOKAL tempat bot Anda berada.\n# Contoh: D:\\MyProjects\\Bots\n" + defaultPath);
                    AnsiConsole.MarkupLine($"[green]✓ Default local path file created.[/]");
                    AnsiConsole.MarkupLine($"[yellow]Please edit '{LocalPathFile.EscapeMarkup()}' to point to your bots directory.[/]");
                    return defaultPath;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]FATAL: Failed to create '{LocalPathFile.EscapeMarkup()}': {ex.Message.EscapeMarkup()}[/]");
                    throw;
                }
            }
            
            try
            {
                var lines = File.ReadAllLines(LocalPathFile);
                var pathLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));
                if (string.IsNullOrWhiteSpace(pathLine))
                {
                     throw new Exception($"'{Path.GetFileName(LocalPathFile)}' is empty or only contains comments.");
                }
                
                // Validasi sederhana
                if (pathLine.Contains("D:\\MyProjects\\Bots") || pathLine.Length < 3)
                {
                    if(validateOnly) AnsiConsole.MarkupLine($"[yellow]✗ Local Path:[/yellow] Path is default or looks invalid: '{pathLine.EscapeMarkup()}'");
                    else AnsiConsole.MarkupLine($"[yellow]Warn: Local path in '{Path.GetFileName(LocalPathFile)}' seems to be the default value. Ensure this is correct.[/]");
                }

                return pathLine.Trim();
            }
            catch (Exception ex)
            {
                 if(validateOnly) AnsiConsole.MarkupLine($"[red]✗ Local Path:[/red] Failed to read '{Path.GetFileName(LocalPathFile)}': {ex.Message.EscapeMarkup()}");
                 else AnsiConsole.MarkupLine($"[red]FATAL: Failed to read '{LocalPathFile.EscapeMarkup()}': {ex.Message.EscapeMarkup()}[/]");
                 throw;
            }
        }


        private static (string Owner, string Repo, List<string> Tokens) LoadTokensFromFile(bool validateOnly)
        {
            if (!File.Exists(TokenFile))
            {
                if (validateOnly)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Tokens:[/red] File '{Path.GetFileName(TokenFile)}' not found.");
                    return ("", "", new List<string>());
                }
                
                AnsiConsole.MarkupLine($"[yellow]File '{TokenFile.EscapeMarkup()}' not found. Creating default...[/]");
                try
                {
                    File.WriteAllText(TokenFile, "OwnerUsername\nRepoName\nghp_YourToken1,ghp_YourToken2");
                    AnsiConsole.MarkupLine($"[green]✓ Default token file created.[/]");
                    AnsiConsole.MarkupLine($"[yellow]Please edit '{TokenFile.EscapeMarkup()}' with your GitHub details and tokens.[/]");
                    return ("", "", new List<string>());
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]FATAL: Failed to create '{TokenFile.EscapeMarkup()}': {ex.Message.EscapeMarkup()}[/]");
                    throw;
                }
            }

            try
            {
                var lines = File.ReadAllLines(TokenFile)
                                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                                .Select(l => l.Trim())
                                .ToList();

                if (lines.Count < 3)
                {
                    throw new Exception($"File must contain 3 non-comment lines: Owner, Repo, Tokens. Found {lines.Count}.");
                }

                string owner = lines[0];
                string repo = lines[1];
                List<string> tokens = lines[2].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(t => t.Trim())
                                           .Where(t => t.StartsWith("ghp_") || t.StartsWith("github_pat_"))
                                           .ToList();

                if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
                {
                    throw new Exception("Owner or Repo name on lines 1 or 2 is empty.");
                }
                
                if (!tokens.Any())
                {
                    throw new Exception("No valid tokens (ghp_... or github_pat_...) found on line 3.");
                }

                return (owner, repo, tokens);
            }
            catch (Exception ex)
            {
                if (validateOnly)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Tokens:[/red] Failed to read '{Path.GetFileName(TokenFile)}': {ex.Message.EscapeMarkup()}");
                    return ("", "", new List<string>());
                }
                AnsiConsole.MarkupLine($"[red]FATAL: Failed to read tokens from '{TokenFile.EscapeMarkup()}'.[/]");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
                throw;
            }
        }
        
        private static List<string> LoadProxiesFromFile(bool validateOnly)
        {
            string fileToLoad = ProxyFile; // Prioritaskan success_proxy.txt

            if (!File.Exists(fileToLoad) || new FileInfo(fileToLoad).Length == 0)
            {
                if (validateOnly) AnsiConsole.MarkupLine($"[dim]✓ Proxies: '{Path.GetFileName(ProxyFile)}' not found or empty. Checking fallback...[/dim]");
                fileToLoad = FallbackProxyFile; // Fallback ke proxy.txt
                
                if (!File.Exists(fileToLoad) || new FileInfo(fileToLoad).Length == 0)
                {
                     if (validateOnly) AnsiConsole.MarkupLine($"[yellow]✗ Proxies:[/yellow] No proxy files ('{Path.GetFileName(ProxyFile)}' or '{Path.GetFileName(FallbackProxyFile)}') found.");
                     else AnsiConsole.MarkupLine($"[yellow]Warn: No proxy files found. Proxy features will be disabled.[/]");
                     return new List<string>();
                }
                
                if (validateOnly) AnsiConsole.MarkupLine($"[dim]✓ Proxies: Using fallback '{Path.GetFileName(FallbackProxyFile)}'.[/dim]");
                else AnsiConsole.MarkupLine($"[yellow]Warn: Using fallback proxy file '{Path.GetFileName(FallbackProxyFile)}'. Run ProxySync (Menu 6) to test.[/]");
            }

            try
            {
                var proxies = File.ReadAllLines(fileToLoad)
                                  .Select(l => l.Trim())
                                  .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#") && (l.StartsWith("http://") || l.StartsWith("https://")))
                                  .ToList();
                
                if (!proxies.Any() && validateOnly) AnsiConsole.MarkupLine($"[yellow]✗ Proxies:[/yellow] File '{Path.GetFileName(fileToLoad)}' found, but contains no valid proxy lines.");
                
                return proxies;
            }
            catch (Exception ex)
            {
                 if (validateOnly) AnsiConsole.MarkupLine($"[red]✗ Proxies:[/red] Failed to read '{Path.GetFileName(fileToLoad)}': {ex.Message.EscapeMarkup()}");
                 else AnsiConsole.MarkupLine($"[red]FATAL: Failed to read proxies from '{fileToLoad.EscapeMarkup()}': {ex.Message.EscapeMarkup()}[/]");
                throw;
            }
        }

        public static TokenState GetState()
        {
            if (!File.Exists(StateFile))
            {
                return new TokenState();
            }
            try
            {
                string json = File.ReadAllText(StateFile);
                return JsonSerializer.Deserialize<TokenState>(json) ?? new TokenState();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warn: Could not read state file, resetting. Error: {ex.Message.EscapeMarkup()}[/]");
                return new TokenState();
            }
        }

        public static void SaveState(TokenState state)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(state, options);
                File.WriteAllText(StateFile, json);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]FATAL: Could not save state file to '{StateFile.EscapeMarkup()}'.[/]");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        public static TokenEntry GetCurrentToken()
        {
            if (_tokens.Count == 0) ReloadAllConfigs();
            if (_tokens.Count == 0) throw new Exception("No tokens loaded.");
            
            var state = GetState();
            int index = state.CurrentIndex;
            if (index < 0 || index >= _tokens.Count)
            {
                index = 0;
                state.CurrentIndex = 0;
                SaveState(state);
            }
            return _tokens[index];
        }

        public static TokenEntry SwitchToNextToken()
        {
            if (_tokens.Count == 0) ReloadAllConfigs();
            if (_tokens.Count == 0) throw new Exception("No tokens loaded.");
            
            var state = GetState();
            int nextIndex = (state.CurrentIndex + 1) % _tokens.Count;
            state.CurrentIndex = nextIndex;
            state.ActiveCodespaceName = null; // Hapus codespace aktif saat ganti token
            SaveState(state);
            return _tokens[nextIndex];
        }
        
        public static (string Index, string Total) GetTokenIndexDisplay()
        {
             if (_tokens.Count == 0) ReloadAllConfigs();
             return ((GetState().CurrentIndex + 1).ToString(), _tokens.Count.ToString());
        }
        
        public static (string Index, string Total) GetProxyIndexDisplay()
        {
             if (_tokens.Count == 0) ReloadAllConfigs();
             int tokenProxyIndex = GetState().CurrentIndex % (_proxies.Count > 0 ? _proxies.Count : 1);
             return ((tokenProxyIndex + 1).ToString(), _proxies.Count.ToString());
        }
        
        public static bool IsProxyGloballyEnabled()
        {
            return _proxyEnabled;
        }

        public static bool ToggleProxy()
        {
            _proxyEnabled = !_proxyEnabled;
            return _proxyEnabled;
        }

        public static bool RotateProxyForToken(TokenEntry token)
        {
            if (_proxies.Count <= 1) return false; // Tidak bisa rotate
            
            _currentProxyIndex = (_currentProxyIndex + 1) % _proxies.Count;
            token.Proxy = _proxies[_currentProxyIndex];
            
            AnsiConsole.MarkupLine($"[dim]Rotated proxy. New proxy index: {_currentProxyIndex}[/]");
            return true;
        }

        public static HttpClient CreateHttpClient(TokenEntry token)
        {
            HttpClientHandler handler = new HttpClientHandler();
            if (_proxyEnabled && !string.IsNullOrEmpty(token.Proxy))
            {
                handler.Proxy = new System.Net.WebProxy(token.Proxy);
                handler.UseProxy = true;
            }
            
            HttpClient client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
            client.DefaultRequestHeaders.Add("User-Agent", "Orchestrator-TUI/4.0");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            return client;
        }
        
        public static string GetLocalPath()
        {
            if (string.IsNullOrEmpty(_localPath))
            {
                _localPath = LoadLocalPath(false);
            }
            return _localPath;
        }
    }
}
