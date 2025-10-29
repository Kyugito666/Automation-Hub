using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
// ... (using lain) ...
using Spectre.Console;

namespace Orchestrator;

public static class TokenManager
{
    // ... (Path variables, _tokens, _availableProxies, _state, _tokenCache tetap sama) ...
    private static readonly string ConfigRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", "..", "config"));
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", ".."));
    private static readonly string TokensPath = Path.Combine(ConfigRoot, "github_tokens.txt");
    private static readonly string SuccessProxyListPath = Path.Combine(ProjectRoot, "proxysync", "success_proxy.txt");
    private static readonly string FallbackProxyListPath = Path.Combine(ProjectRoot, "proxysync", "proxy.txt");
    private static readonly string StatePath = Path.Combine(ProjectRoot, ".token-state.json");
    private static readonly string TokenCachePath = Path.Combine(ProjectRoot, ".token-cache.json");

    private static List<TokenEntry> _tokens = new();
    private static List<string> _availableProxies = new();
    private static TokenState _state = new();
    private static Dictionary<string, string> _tokenCache = new();

    private const int DEFAULT_HTTP_TIMEOUT_SEC = 60;

    // Initialize tetap panggil semua load
    public static void Initialize() { LoadTokens(); LoadProxyList(); LoadState(); LoadTokenCache(); AssignProxiesAndUsernames(); }

    // ReloadAllConfigs HARUS reset state juga (buat kasus config token berubah total)
    public static void ReloadAllConfigs() {
        AnsiConsole.MarkupLine("[yellow]Reloading ALL configs (Tokens, Proxies, State)...[/]");
        _tokens.Clear();
        _availableProxies.Clear();
        _tokenCache.Clear(); // Cache juga perlu di-clear kalo token berubah
        _state = new TokenState(); // Reset state ke awal
        Initialize(); // Panggil ulang semua load
        AnsiConsole.MarkupLine("[green]All configs refreshed.[/]");
    }

    // === FUNGSI BARU: Hanya reload proxy & reassign ===
    public static void ReloadProxyListAndReassign() {
        AnsiConsole.MarkupLine("[yellow]Reloading proxy list and reassigning...[/]");
        _availableProxies.Clear(); // Kosongkan list proxy lama
        LoadProxyList(); // Baca ulang success_proxy.txt / proxy.txt
        AssignProxiesToExistingTokens(); // Assign ulang proxy ke token yang sudah ada
        AnsiConsole.MarkupLine("[green]Proxy list refreshed and reassigned.[/]");
    }
    // === AKHIR FUNGSI BARU ===

    // Helper baru untuk assign proxy tanpa load token ulang
    private static void AssignProxiesToExistingTokens() {
        if (!_tokens.Any()) return;
        AnsiConsole.MarkupLine($"[dim]   Assigning {_availableProxies.Count} proxies to {_tokens.Count} existing tokens...[/]");
        for (int i = 0; i < _tokens.Count; i++) {
            if (_availableProxies.Any()) {
                _tokens[i].Proxy = _availableProxies[i % _availableProxies.Count];
            } else {
                _tokens[i].Proxy = null; // Hapus proxy jika list baru kosong
            }
        }
        // Tidak perlu panggil InitializeProxyPool karena pool lama udah dihapus
    }


    // ... (LoadTokenCache, SaveTokenCache, LoadTokens, LoadProxyList tetap sama) ...
     private static void LoadTokenCache() { if (!File.Exists(TokenCachePath)) return; try { var json = File.ReadAllText(TokenCachePath); _tokenCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); AnsiConsole.MarkupLine($"[dim]Loaded {_tokenCache.Count} usernames from cache[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn: Load cache fail: {ex.Message.EscapeMarkup()}[/]"); _tokenCache = new(); } }
    public static void SaveTokenCache(Dictionary<string, string> cache) { try { _tokenCache = cache; var json = JsonSerializer.Serialize(_tokenCache, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(TokenCachePath, json); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err save cache: {ex.Message.EscapeMarkup()}[/]"); } }
    private static void LoadTokens() { if (!File.Exists(TokensPath)) { AnsiConsole.MarkupLine($"[red]ERR: {TokensPath.EscapeMarkup()} not found![/]"); return; } var lines = File.ReadAllLines(TokensPath); if (lines.Length < 3) { AnsiConsole.MarkupLine("[red]ERR: github_tokens.txt format incorrect![/]"); return; } var owner = lines[0].Trim(); var repo = lines[1].Trim(); var tokens = lines[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); _tokens = tokens.Select(t => new TokenEntry { Token = t, Owner = owner, Repo = repo }).ToList(); AnsiConsole.MarkupLine($"[green]Loaded {_tokens.Count} tokens for {owner.EscapeMarkup()}/{repo.EscapeMarkup()}[/]"); }
    private static void LoadProxyList() { string proxyFileToLoad = File.Exists(SuccessProxyListPath) && new FileInfo(SuccessProxyListPath).Length > 0 ? SuccessProxyListPath : File.Exists(FallbackProxyListPath) && new FileInfo(FallbackProxyListPath).Length > 0 ? FallbackProxyListPath : ""; if (string.IsNullOrEmpty(proxyFileToLoad)) { AnsiConsole.MarkupLine("[red]CRITICAL: No proxy file found or proxy files are empty![/]"); AnsiConsole.MarkupLine("[yellow]Run ProxySync first![/]"); _availableProxies.Clear(); return; } try { _availableProxies = File.ReadAllLines(proxyFileToLoad).Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")).Distinct().ToList(); if (!_availableProxies.Any()) { AnsiConsole.MarkupLine($"[yellow]Warning: Proxy file '{Path.GetFileName(proxyFileToLoad)}' loaded but contains no valid proxies.[/]"); } else if (proxyFileToLoad == SuccessProxyListPath) AnsiConsole.MarkupLine($"[green]✓ {_availableProxies.Count} tested proxies loaded from success_proxy.txt[/]"); else { AnsiConsole.MarkupLine($"[yellow]⚠ Using fallback proxy.txt ({_availableProxies.Count} proxies)[/]"); AnsiConsole.MarkupLine("[yellow]  Consider running ProxySync test for better reliability![/]"); } } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error loading proxies from '{Path.GetFileName(proxyFileToLoad)}': {ex.Message.EscapeMarkup()}[/]"); _availableProxies.Clear(); } }


    // AssignProxiesAndUsernames hanya dipanggil di Initialize awal
    private static void AssignProxiesAndUsernames() {
         if (!_tokens.Any()) return;
         AssignProxiesToExistingTokens(); // Panggil helper
         // Assign username dari cache
         foreach(var tokenEntry in _tokens) {
              if (_tokenCache.TryGetValue(tokenEntry.Token, out var u)) {
                   tokenEntry.Username = u;
              }
         }
     }


    // ... (LoadState, GetState, SaveState, GetCurrentToken, GetAllTokenEntries, GetUsernameCache tetap sama) ...
     private static void LoadState() { try { if (File.Exists(StatePath)) { var json = File.ReadAllText(StatePath); _state = JsonSerializer.Deserialize<TokenState>(json) ?? new TokenState(); AnsiConsole.MarkupLine($"[dim]Loaded state: Index={_state.CurrentIndex}, Codespace='{_state.ActiveCodespaceName ?? "None"}'[/]");} else { _state = new TokenState(); AnsiConsole.MarkupLine("[dim]No state file found, starting fresh.[/]");} } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error loading state: {ex.Message.EscapeMarkup()}. Resetting state.[/]"); _state = new TokenState(); } if (_state.CurrentIndex >= _tokens.Count && _tokens.Any()) { AnsiConsole.MarkupLine($"[yellow]Warning: Saved index ({_state.CurrentIndex}) out of bounds ({_tokens.Count} tokens). Resetting to 0.[/]"); _state.CurrentIndex = 0; } }
    public static TokenState GetState() => _state;
    public static void SaveState(TokenState state) { _state = state; var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }); try { File.WriteAllText(StatePath, json); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error saving state: {ex.Message.EscapeMarkup()}[/]"); } }
    public static TokenEntry GetCurrentToken() { if (!_tokens.Any()) throw new InvalidOperationException("No tokens configured in github_tokens.txt"); if (_state.CurrentIndex >= _tokens.Count || _state.CurrentIndex < 0) { AnsiConsole.MarkupLine($"[yellow]Warning: Current index ({_state.CurrentIndex}) is invalid. Resetting to 0.[/]"); _state.CurrentIndex = 0; SaveState(_state); } return _tokens[_state.CurrentIndex]; }
    public static List<TokenEntry> GetAllTokenEntries() => _tokens;
    public static Dictionary<string, string> GetUsernameCache() => _tokenCache;


    // Logika RotateProxyForToken tetap sama (versi pintar tanpa pool)
    public static bool RotateProxyForToken(TokenEntry currentTokenEntry)
    {
        if (!_availableProxies.Any()) { AnsiConsole.MarkupLine("[red]No proxies available to rotate.[/]"); return false; }
        string? oldProxy = currentTokenEntry.Proxy;
        var proxiesInUseByOthers = _tokens.Where(t => t.Token != currentTokenEntry.Token && !string.IsNullOrEmpty(t.Proxy)).Select(t => t.Proxy!).ToHashSet();
        string? newProxy = _availableProxies.FirstOrDefault(p => !proxiesInUseByOthers.Contains(p) && p != oldProxy);
        if (newProxy == null) {
            AnsiConsole.MarkupLine("[yellow]All proxies currently assigned. Picking random fallback (excluding current)...[/]");
            newProxy = _availableProxies.Where(p => p != oldProxy).OrderBy(x => Guid.NewGuid()).FirstOrDefault() ?? _availableProxies.FirstOrDefault(); // Fallback ke acak / yg pertama
        }
        if (newProxy == null) { AnsiConsole.MarkupLine("[red]FATAL: No proxies found in list after filtering.[/]"); return false; }
        AnsiConsole.MarkupLine($"[yellow]Proxy rotated: {MaskProxy(oldProxy)} -> {MaskProxy(newProxy)}[/]");
        currentTokenEntry.Proxy = newProxy;
        return true;
    }

    // Logika SwitchToNextToken tetap sama
    public static TokenEntry SwitchToNextToken() { if (!_tokens.Any() || _tokens.Count == 1) { AnsiConsole.MarkupLine("[yellow]Only 1 token configured or no tokens loaded.[/]"); return GetCurrentToken(); } _state.CurrentIndex = (_state.CurrentIndex + 1) % _tokens.Count; _state.ActiveCodespaceName = null; SaveState(_state); var current = _tokens[_state.CurrentIndex]; var username = current.Username ?? "unknown"; AnsiConsole.MarkupLine($"[yellow]Token Rotated: -> #{_state.CurrentIndex + 1} (@{username.EscapeMarkup()})[/]"); if (!string.IsNullOrEmpty(current.Proxy)) AnsiConsole.MarkupLine($"[dim]Proxy assigned: {MaskProxy(current.Proxy)}[/]"); return current; }

    // Logika CreateHttpClient tetap sama
     public static HttpClient CreateHttpClient(TokenEntry token) {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(token.Proxy)) {
            // AnsiConsole.Markup($"[dim]   Using proxy {MaskProxy(token.Proxy)}[/]"); // Kurangi verbosity
            try {
                var proxyUri = new Uri(token.Proxy, UriKind.Absolute);
                var webProxy = new WebProxy(proxyUri);
                if (!string.IsNullOrEmpty(proxyUri.UserInfo)) {
                    var credentials = proxyUri.UserInfo.Split(':', 2);
                    if (credentials.Length == 2) {
                        webProxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(credentials[0]), Uri.UnescapeDataString(credentials[1]));
                        webProxy.UseDefaultCredentials = false;
                        // AnsiConsole.MarkupLine(" with credentials"); // Kurangi verbosity
                    } else { /* AnsiConsole.MarkupLine("[yellow] (Credentials format issue? Using proxy without auth)[/]"); */ webProxy.UseDefaultCredentials = true; }
                } else { webProxy.UseDefaultCredentials = true; /* AnsiConsole.MarkupLine(""); */ }
                handler.Proxy = webProxy;
                handler.UseProxy = true;
            } catch (UriFormatException ex) { AnsiConsole.MarkupLine($"[red]Proxy format invalid {MaskProxy(token.Proxy)}: {ex.Message.EscapeMarkup()}. Disabling proxy.[/]"); handler.Proxy = null; handler.UseProxy = false;
            } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error setting proxy {MaskProxy(token.Proxy)}: {ex.Message.EscapeMarkup()}. Disabling proxy.[/]"); handler.Proxy = null; handler.UseProxy = false; }
        } else { handler.Proxy = null; handler.UseProxy = false; }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(DEFAULT_HTTP_TIMEOUT_SEC) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/4.1"); // Versi naik
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    // MaskToken dan MaskProxy tetap sama
    public static string MaskToken(string token) { if (string.IsNullOrEmpty(token)) return "invalid"; return token.Length > 10 ? token[..4] + "..." + token[^4..] : token; }
    public static string MaskProxy(string? proxy) { if (string.IsNullOrEmpty(proxy)) return "[grey]no-proxy[/]"; try { if (Uri.TryCreate(proxy, UriKind.Absolute, out var uri)) { return $"{uri.Scheme}://{uri.Host}:{uri.Port}"; } var parts = proxy.Split('@'); if (parts.Length == 2) { var hostPort = parts[1].Split(':'); if (hostPort.Length >= 2) return $"proxy://{hostPort[0]}:{hostPort[1]}"; } else { var hostPort = proxy.Split(':'); if(hostPort.Length >= 2) return $"proxy://{hostPort[0]}:{hostPort[1]}"; } return "[grey]masked[/]"; } catch { return "[red]invalid-format[/]"; } }

    // ShowStatus tetap sama
     public static void ShowStatus() { if (!_tokens.Any()) { AnsiConsole.MarkupLine("[yellow]No tokens configured.[/]"); return; } var owner = _tokens.FirstOrDefault()?.Owner ?? "N/A"; var repo = _tokens.FirstOrDefault()?.Repo ?? "N/A"; var activeCs = _state.ActiveCodespaceName ?? "[grey]None[/]"; AnsiConsole.MarkupLine($"[cyan]Owner       :[/] [yellow]{owner.EscapeMarkup()}[/]"); AnsiConsole.MarkupLine($"[cyan]Repo        :[/] [yellow]{repo.EscapeMarkup()}[/]"); AnsiConsole.MarkupLine($"[cyan]Active CS   :[/] [yellow]{activeCs.EscapeMarkup()}[/]"); var table = new Table().Title("Tokens Status").Expand(); table.AddColumn("Idx"); table.AddColumn("Token"); table.AddColumn("User"); table.AddColumn("Proxy"); table.AddColumn("Active"); for (int i = 0; i < _tokens.Count; i++) { var t = _tokens[i]; var act = i == _state.CurrentIndex ? "[bold green]>>>[/]" : ""; var tokD = MaskToken(t.Token); var userD = t.Username ?? "[grey]unknown[/]"; var proxD = MaskProxy(t.Proxy); table.AddRow((i+1).ToString(), tokD, userD, proxD, act); } AnsiConsole.Write(table); AnsiConsole.MarkupLine($"[dim]Proxies available in list: {_availableProxies.Count}[/]"); }

} // Akhir class TokenManager

// Definisi class TokenEntry dan TokenState tetap sama
public class TokenEntry { public string Token { get; set; } = string.Empty; public string? Proxy { get; set; } public string? Username { get; set; } public string Owner { get; set; } = string.Empty; public string Repo { get; set; } = string.Empty; }
public class TokenState { [JsonPropertyName("current_index")] public int CurrentIndex { get; set; } = 0; [JsonPropertyName("active_codespace_name")] public string? ActiveCodespaceName { get; set; } = null; }
