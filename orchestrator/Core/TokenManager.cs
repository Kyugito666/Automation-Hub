using System.Net;
using System.Security.Cryptography; 
using System.Text; 
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace Orchestrator.Core
{
    public class TokenEntry { public string Token{get;set;}=""; public string? Proxy{get;set;} public string? Username{get;set;} public string Owner{get;set;}=""; public string Repo{get;set;}=""; }
    public class TokenState { [JsonPropertyName("current_index")] public int CurrentIndex{get;set;}=0; [JsonPropertyName("active_codespace_name")] public string? ActiveCodespaceName{get;set;}=null; }

    public static class TokenManager
    {
        private static readonly string ProjectRoot = GetProjectRoot(); 
        private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
        private static readonly string ProxySyncRoot = Path.Combine(ProjectRoot, "proxysync"); 

        private static readonly string TokensPath = Path.Combine(ConfigRoot, "github_tokens.txt");
        private static readonly string SuccessProxyListPath = Path.Combine(ProxySyncRoot, "success_proxy.txt");
        private static readonly string FallbackProxyListPath = Path.Combine(ProxySyncRoot, "proxy.txt");
        private static readonly string StatePath = Path.Combine(ProjectRoot, ".token-state.json");
        private static readonly string TokenCachePath = Path.Combine(ProjectRoot, ".token-cache.json");

        private static List<TokenEntry> _tokens = new();
        private static List<string> _availableProxies = new();
        private static TokenState _state = new();
        private static Dictionary<string, string> _tokenCache = new();

        // === INI PERBAIKANNYA ===
        // 1. Tambah flag global, defaultnya 'true' (pakai proxy)
        private static bool _proxiesGloballyEnabled = true;
        // === AKHIR PERBAIKAN ===

        private const int DEFAULT_HTTP_TIMEOUT_SEC = 60;

        // === INI PERBAIKANNYA ===
        // 2. Tambah setter untuk flag global
        public static void SetProxyUsage(bool enabled)
        {
            _proxiesGloballyEnabled = enabled;
            if (enabled)
            {
                AnsiConsole.MarkupLine("[green]✓ Proxy global [bold]DIAKTIFKAN[/] untuk sesi loop ini.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠ Proxy global [bold]DINONAKTIFKAN[/] untuk sesi loop ini.[/]");
            }
        }
        
        // 3. Tambah getter untuk flag global
        public static bool IsProxyGloballyEnabled() => _proxiesGloballyEnabled;
        // === AKHIR PERBAIKAN ===

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

        public static void Initialize() { LoadTokens(); LoadProxyList(); LoadState(); LoadTokenCache(); AssignProxiesAndUsernames(); }

        public static void ReloadAllConfigs() {
            AnsiConsole.MarkupLine("[yellow]Reloading ALL configs (Tokens, Proxies, State)...[/]");
            _tokens.Clear();
            _availableProxies.Clear();
            _tokenCache.Clear();
            _state = new TokenState();
            Initialize();
            AnsiConsole.MarkupLine("[green]All configs refreshed.[/]");
        }

        public static void ReloadProxyListAndReassign() {
            AnsiConsole.MarkupLine("[yellow]Reloading proxy list and reassigning...[/]");
            _availableProxies.Clear();
            LoadProxyList(); 
            AssignProxiesToExistingTokens();
            AnsiConsole.MarkupLine("[green]Proxy list refreshed and reassigned.[/]");
        }

        private static void AssignProxiesToExistingTokens() {
            if (!_tokens.Any()) return;
            AnsiConsole.MarkupLine($"[dim]   Assigning {_availableProxies.Count} proxies to {_tokens.Count} existing tokens...[/]");
            for (int i = 0; i < _tokens.Count; i++) {
                if (_availableProxies.Any()) {
                    _tokens[i].Proxy = _availableProxies[i % _availableProxies.Count];
                } else {
                    _tokens[i].Proxy = null;
                }
            }
        }

        private static void LoadTokenCache() { if (!File.Exists(TokenCachePath)) return; try { var json = File.ReadAllText(TokenCachePath); _tokenCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); AnsiConsole.MarkupLine($"[dim]Loaded {_tokenCache.Count} usernames from cache[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn: Load cache fail: {ex.Message.EscapeMarkup()}[/]"); _tokenCache = new(); } }
        public static void SaveTokenCache(Dictionary<string, string> cache) { try { _tokenCache = cache; var json = JsonSerializer.Serialize(_tokenCache, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(TokenCachePath, json); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err save cache: {ex.Message.EscapeMarkup()}[/]"); } }
        private static void LoadTokens() { if (!File.Exists(TokensPath)) { AnsiConsole.MarkupLine($"[red]ERR: {TokensPath.EscapeMarkup()} not found![/]"); return; } var lines = File.ReadAllLines(TokensPath); if (lines.Length < 3) { AnsiConsole.MarkupLine("[red]ERR: github_tokens.txt format incorrect![/]"); return; } var owner = lines[0].Trim(); var repo = lines[1].Trim(); var tokensLine = lines[2].Trim(); if (string.IsNullOrEmpty(tokensLine)) { AnsiConsole.MarkupLine("[red]ERR: Baris 3 (tokens) di github_tokens.txt kosong![/]"); _tokens.Clear(); return; } var tokens = tokensLine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); if (!tokens.Any()) { AnsiConsole.MarkupLine("[red]ERR: Tidak ada token valid ditemukan di baris 3 github_tokens.txt![/]"); _tokens.Clear(); return; } _tokens = tokens.Select(t => new TokenEntry { Token = t, Owner = owner, Repo = repo }).ToList(); AnsiConsole.MarkupLine($"[green]Loaded {_tokens.Count} tokens for {owner.EscapeMarkup()}/{repo.EscapeMarkup()}[/]"); }

        private static void LoadProxyList() {
            string proxyFileToLoad = File.Exists(SuccessProxyListPath) && new FileInfo(SuccessProxyListPath).Length > 0
                ? SuccessProxyListPath
                : File.Exists(FallbackProxyListPath) && new FileInfo(FallbackProxyListPath).Length > 0
                    ? FallbackProxyListPath
                    : ""; 

            if (string.IsNullOrEmpty(proxyFileToLoad)) {
                AnsiConsole.MarkupLine("[red]CRITICAL: No proxy file found or proxy files are empty in 'proxysync/' folder![/]");
                AnsiConsole.MarkupLine($"[dim]   Expected: '{SuccessProxyListPath.EscapeMarkup()}' or '{FallbackProxyListPath.EscapeMarkup()}'[/]");
                AnsiConsole.MarkupLine("[yellow]Run ProxySync first (via Menu 3 or Menu 1 loop)![/]");
                _availableProxies.Clear();
                return;
            }

            try {
                _availableProxies = File.ReadAllLines(proxyFileToLoad)
                                        .Select(l => l.Trim())
                                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                                        .Distinct() 
                                        .ToList();

                if (!_availableProxies.Any()) {
                    AnsiConsole.MarkupLine($"[yellow]Warning: Proxy file '{Path.GetFileName(proxyFileToLoad)}' loaded but contains no valid proxies.[/]");
                } else if (proxyFileToLoad == SuccessProxyListPath) {
                    AnsiConsole.MarkupLine($"[green]✓ {_availableProxies.Count} tested proxies loaded from proxysync/success_proxy.txt[/]");
                } else { 
                    AnsiConsole.MarkupLine($"[yellow]⚠ Using fallback proxysync/proxy.txt ({_availableProxies.Count} proxies)[/]");
                    AnsiConsole.MarkupLine("[yellow]  Consider running ProxySync test for better reliability![/]");
                }
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error loading proxies from '{Path.GetFileName(proxyFileToLoad)}': {ex.Message.EscapeMarkup()}[/]");
                _availableProxies.Clear();
            }
        }

        private static void AssignProxiesAndUsernames() { if (!_tokens.Any()) return; AssignProxiesToExistingTokens(); foreach(var tokenEntry in _tokens) { if (_tokenCache.TryGetValue(tokenEntry.Token, out var u)) { tokenEntry.Username = u; } } }
        private static void LoadState() { try { if (File.Exists(StatePath)) { var json = File.ReadAllText(StatePath); _state = JsonSerializer.Deserialize<TokenState>(json) ?? new TokenState(); AnsiConsole.MarkupLine($"[dim]Loaded state: Idx={_state.CurrentIndex}, CS='{_state.ActiveCodespaceName ?? "N/A"}'[/]");} else { _state = new TokenState(); AnsiConsole.MarkupLine("[dim]No state file.[/]");} } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err load state: {ex.Message.EscapeMarkup()}. Reset.[/]"); _state = new TokenState(); } if (_state.CurrentIndex >= _tokens.Count && _tokens.Any()) { AnsiConsole.MarkupLine($"[yellow]Warn: Index reset (0).[/]"); _state.CurrentIndex = 0; } }
        public static TokenState GetState() => _state;
        public static void SaveState(TokenState state) { _state = state; var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }); try { File.WriteAllText(StatePath, json); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err save state: {ex.Message.EscapeMarkup()}[/]"); } }
        public static TokenEntry GetCurrentToken() { if (!_tokens.Any()) throw new InvalidOperationException("No tokens configured."); if (_state.CurrentIndex >= _tokens.Count || _state.CurrentIndex < 0) { AnsiConsole.MarkupLine($"[yellow]Warn: Invalid index. Reset (0).[/]"); _state.CurrentIndex = 0; SaveState(_state); } return _tokens[_state.CurrentIndex]; }
        public static List<TokenEntry> GetAllTokenEntries() => _tokens;
        public static Dictionary<string, string> GetUsernameCache() => _tokenCache;
        public static bool RotateProxyForToken(TokenEntry currentTokenEntry) { 
            if (!_availableProxies.Any()) { 
                AnsiConsole.MarkupLine("[red]No proxies available for rotation.[/]"); 
                return false; 
            } 
            
            string? oldProxy = currentTokenEntry.Proxy;
            string? oldAccount = ExtractProxyAccount(oldProxy);
            
            // Ambil semua proxy yang sedang dipakai token lain
            var proxiesInUse = _tokens.Where(t => t.Token != currentTokenEntry.Token && !string.IsNullOrEmpty(t.Proxy))
                                      .Select(t => t.Proxy!)
                                      .ToHashSet();
            
            // STRATEGI 1: Cari proxy dengan AKUN BERBEDA yang tidak sedang dipakai
            var proxiesWithDifferentAccount = _availableProxies
                .Where(p => {
                    string? account = ExtractProxyAccount(p);
                    return account != null && account != oldAccount && !proxiesInUse.Contains(p);
                })
                .ToList();
            
            string? newProxy = null;
            
            if (proxiesWithDifferentAccount.Any()) {
                newProxy = proxiesWithDifferentAccount.OrderBy(x => Guid.NewGuid()).FirstOrDefault();
                AnsiConsole.MarkupLine($"[green]Rotating to different proxy account:[/] {MaskProxy(oldProxy)} -> {MaskProxy(newProxy)}");
            }
            // STRATEGI 2: Cari proxy dengan akun sama tapi IP berbeda (fallback)
            else {
                var proxiesWithSameAccount = _availableProxies
                    .Where(p => p != oldProxy && !proxiesInUse.Contains(p))
                    .ToList();
                
                if (proxiesWithSameAccount.Any()) {
                    newProxy = proxiesWithSameAccount.OrderBy(x => Guid.NewGuid()).FirstOrDefault();
                    AnsiConsole.MarkupLine($"[yellow]No different accounts available. Rotating IP only:[/] {MaskProxy(oldProxy)} -> {MaskProxy(newProxy)}");
                } else {
                    // STRATEGI 3: Pakai proxy yang sedang dipakai token lain (last resort)
                    newProxy = _availableProxies
                        .Where(p => p != oldProxy)
                        .OrderBy(x => Guid.NewGuid())
                        .FirstOrDefault() ?? _availableProxies.FirstOrDefault();
                    
                    if (newProxy != null) {
                        AnsiConsole.MarkupLine($"[yellow]All proxies in use. Sharing proxy:[/] {MaskProxy(oldProxy)} -> {MaskProxy(newProxy)}");
                    }
                }
            }
            
            if (newProxy == null) { 
                AnsiConsole.MarkupLine("[red]FATAL: No alternative proxies found.[/]"); 
                return false; 
            }
            
            currentTokenEntry.Proxy = newProxy; 
            return true; 
        }
        
        private static string? ExtractProxyAccount(string? proxyUrl) {
            if (string.IsNullOrEmpty(proxyUrl)) return null;
            
            try {
                // Format: protocol://username:password@host:port
                if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)) {
                    return uri.UserInfo; // Returns "username:password"
                }
                
                // Format: username:password@host:port (tanpa protocol)
                var parts = proxyUrl.Split('@');
                if (parts.Length == 2) {
                    return parts[0]; // Returns "username:password"
                }
            } catch {
                // Ignore parse errors
            }
            
            return null;
        }
        public static TokenEntry SwitchToNextToken() { if (!_tokens.Any() || _tokens.Count == 1) { AnsiConsole.MarkupLine("[yellow]Only 1 token.[/]"); return GetCurrentToken(); } _state.CurrentIndex = (_state.CurrentIndex + 1) % _tokens.Count; _state.ActiveCodespaceName = null; SaveState(_state); var current = _tokens[_state.CurrentIndex]; var username = current.Username ?? "unknown"; AnsiConsole.MarkupLine($"[yellow]Token Rotated: -> #{_state.CurrentIndex + 1} (@{username.EscapeMarkup()})[/]"); if (!string.IsNullOrEmpty(current.Proxy)) AnsiConsole.MarkupLine($"[dim]Proxy: {MaskProxy(current.Proxy)}[/]"); return current; }

        public static HttpClient CreateHttpClient(TokenEntry token) {
            var handler = new HttpClientHandler();
            
            // === INI PERBAIKANNYA ===
            // 4. Cek flag global SEBELUM set proxy
            if (_proxiesGloballyEnabled && !string.IsNullOrEmpty(token.Proxy)) {
            // === AKHIR PERBAIKAN ===
                try {
                    var proxyUri = new Uri(token.Proxy, UriKind.Absolute);
                    var webProxy = new WebProxy(proxyUri);
                    if (!string.IsNullOrEmpty(proxyUri.UserInfo)) {
                        var credentials = proxyUri.UserInfo.Split(':', 2);
                        if (credentials.Length == 2) {
                            webProxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(credentials[0]), Uri.UnescapeDataString(credentials[1]));
                            webProxy.UseDefaultCredentials = false; 
                        } else {
                            AnsiConsole.MarkupLine($"[yellow] (Proxy credential format issue for {MaskProxy(token.Proxy)}? Trying without auth)[/]");
                            webProxy.UseDefaultCredentials = true; 
                         }
                    } else {
                        webProxy.UseDefaultCredentials = true; 
                    }
                    handler.Proxy = webProxy;
                    handler.UseProxy = true;
                } catch (UriFormatException ex) { AnsiConsole.MarkupLine($"[red]Proxy format invalid {MaskProxy(token.Proxy)}: {ex.Message.EscapeMarkup()}. Disabling proxy.[/]"); handler.Proxy = null; handler.UseProxy = false;
                } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error setting proxy {MaskProxy(token.Proxy)}: {ex.Message.EscapeMarkup()}. Disabling proxy.[/]"); handler.Proxy = null; handler.UseProxy = false; }
            } else { handler.Proxy = null; handler.UseProxy = false; }

            var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(DEFAULT_HTTP_TIMEOUT_SEC) };
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub/4.1"); 
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return httpClient;
        }

        public static string MaskToken(string t) { if (string.IsNullOrEmpty(t)) return "N/A"; return t.Length > 10 ? t[..4] + "..." + t[^4..] : t; }
        public static string MaskProxy(string? p) { if (string.IsNullOrEmpty(p)) return "[grey]none[/]"; try { if (Uri.TryCreate(p, UriKind.Absolute, out var u)) { return $"{u.Scheme}://{u.Host}:{u.Port}"; } var ps = p.Split('@'); if (ps.Length == 2) { var hp = ps[1].Split(':'); if (hp.Length >= 2) return $"proxy://{hp[0]}:{hp[1]}"; } else { var hp = p.Split(':'); if(hp.Length >= 2) return $"proxy://{hp[0]}:{hp[1]}"; } return "[grey]masked[/]"; } catch { return "[red]invalid[/]"; } }
        public static void ShowStatus() { if (!_tokens.Any()) { AnsiConsole.MarkupLine("[yellow]No tokens.[/]"); return; } var owner = _tokens.FirstOrDefault()?.Owner ?? "N/A"; var repo = _tokens.FirstOrDefault()?.Repo ?? "N/A"; var activeCs = _state.ActiveCodespaceName ?? "[grey]None[/]"; AnsiConsole.MarkupLine($"[cyan]Owner :[/] [yellow]{owner.EscapeMarkup()}[/]"); AnsiConsole.MarkupLine($"[cyan]Repo  :[/] [yellow]{repo.EscapeMarkup()}[/]"); AnsiConsole.MarkupLine($"[cyan]Active CS:[/] [yellow]{activeCs.EscapeMarkup()}[/]"); var table = new Table().Title("Tokens Status").Expand(); table.AddColumn("Idx"); table.AddColumn("Token"); table.AddColumn("User"); table.AddColumn("Proxy"); table.AddColumn("Active"); for (int i = 0; i < _tokens.Count; i++) { var t = _tokens[i]; var act = i == _state.CurrentIndex ? "[bold green]>>>[/]" : ""; var tokD = MaskToken(t.Token); var userD = t.Username ?? "[grey]unknown[/]"; var proxD = MaskProxy(t.Proxy); table.AddRow((i+1).ToString(), tokD, userD, proxD, act); } AnsiConsole.Write(table); AnsiConsole.MarkupLine($"[dim]Proxies loaded: {_availableProxies.Count}[/]"); }
    }
}
