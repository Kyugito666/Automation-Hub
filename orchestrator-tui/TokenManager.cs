using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class TokenManager
{
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
    private static Dictionary<string, Queue<string>> _proxyPool = new();

    private const int DEFAULT_HTTP_TIMEOUT_SEC = 60;

    public static void Initialize() { LoadTokens(); LoadProxyList(); LoadState(); LoadTokenCache(); AssignProxiesAndUsernames(); InitializeProxyPool(); }
    public static void ReloadAllConfigs() { AnsiConsole.MarkupLine("[yellow]Reloading configs...[/]"); _tokens.Clear(); _availableProxies.Clear(); _tokenCache.Clear(); _state = new TokenState(); _proxyPool.Clear(); Initialize(); AnsiConsole.MarkupLine("[green]Configs refreshed.[/]"); }

    private static void LoadTokenCache() { if (!File.Exists(TokenCachePath)) return; try { var json = File.ReadAllText(TokenCachePath); _tokenCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); AnsiConsole.MarkupLine($"[dim]Loaded {_tokenCache.Count} usernames from cache[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn: Load cache fail: {ex.Message.EscapeMarkup()}[/]"); _tokenCache = new(); } }
    public static void SaveTokenCache(Dictionary<string, string> cache) { try { _tokenCache = cache; var json = JsonSerializer.Serialize(_tokenCache, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(TokenCachePath, json); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err save cache: {ex.Message.EscapeMarkup()}[/]"); } }
    private static void LoadTokens() { if (!File.Exists(TokensPath)) { AnsiConsole.MarkupLine($"[red]ERR: {TokensPath.EscapeMarkup()} not found![/]"); return; } var lines = File.ReadAllLines(TokensPath); if (lines.Length < 3) { AnsiConsole.MarkupLine("[red]ERR: github_tokens.txt format incorrect![/]"); return; } var owner = lines[0].Trim(); var repo = lines[1].Trim(); var tokens = lines[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); _tokens = tokens.Select(t => new TokenEntry { Token = t, Owner = owner, Repo = repo }).ToList(); AnsiConsole.MarkupLine($"[green]Loaded {_tokens.Count} tokens for {owner.EscapeMarkup()}/{repo.EscapeMarkup()}[/]"); }
    private static void LoadProxyList() { string proxyFileToLoad = File.Exists(SuccessProxyListPath) ? SuccessProxyListPath : File.Exists(FallbackProxyListPath) ? FallbackProxyListPath : ""; if (string.IsNullOrEmpty(proxyFileToLoad)) { AnsiConsole.MarkupLine("[red]CRITICAL: No proxy file![/]"); AnsiConsole.MarkupLine("[yellow]Run ProxySync first![/]"); _availableProxies.Clear(); return; } try { _availableProxies = File.ReadAllLines(proxyFileToLoad).Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")).Distinct().ToList(); if (proxyFileToLoad == SuccessProxyListPath) AnsiConsole.MarkupLine($"[green]✓ {_availableProxies.Count} tested proxies from success_proxy.txt[/]"); else { AnsiConsole.MarkupLine($"[yellow]⚠ Using fallback proxy.txt ({_availableProxies.Count} proxies)[/]"); AnsiConsole.MarkupLine("[yellow]  Run ProxySync for tested proxies![/]"); } } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err load proxies: {ex.Message.EscapeMarkup()}[/]"); _availableProxies.Clear(); } }
    private static void AssignProxiesAndUsernames() { if (!_tokens.Any()) return; for (int i = 0; i < _tokens.Count; i++) { if (_availableProxies.Any()) _tokens[i].Proxy = _availableProxies[i % _availableProxies.Count]; else _tokens[i].Proxy = null; if (_tokenCache.TryGetValue(_tokens[i].Token, out var u)) _tokens[i].Username = u; } }
    private static void InitializeProxyPool() { _proxyPool.Clear(); if (!_availableProxies.Any()) return; foreach (var t in _tokens) { var s = _availableProxies.OrderBy(x => Guid.NewGuid()).ToList(); _proxyPool[t.Token] = new Queue<string>(s); } AnsiConsole.MarkupLine($"[dim]Initialized proxy pool for {_proxyPool.Count} tokens.[/]"); }
    private static void LoadState() { try { if (File.Exists(StatePath)) { var json = File.ReadAllText(StatePath); _state = JsonSerializer.Deserialize<TokenState>(json) ?? new TokenState(); } } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err load state: {ex.Message.EscapeMarkup()}. Reset.[/]"); _state = new TokenState(); } if (_state.CurrentIndex >= _tokens.Count && _tokens.Any()) _state.CurrentIndex = 0; }
    public static TokenState GetState() => _state;
    public static void SaveState(TokenState state) { _state = state; var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }); try { File.WriteAllText(StatePath, json); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err save state: {ex.Message.EscapeMarkup()}[/]"); } }
    public static TokenEntry GetCurrentToken() { if (!_tokens.Any()) throw new Exception("No tokens configured"); if (_state.CurrentIndex >= _tokens.Count || _state.CurrentIndex < 0) _state.CurrentIndex = 0; return _tokens[_state.CurrentIndex]; }
    public static List<TokenEntry> GetAllTokenEntries() => _tokens;
    public static Dictionary<string, string> GetUsernameCache() => _tokenCache;

    public static bool RotateProxyForToken(TokenEntry currentTokenEntry) {
        if (!_proxyPool.TryGetValue(currentTokenEntry.Token, out var pool) || pool.Count == 0) {
            AnsiConsole.MarkupLine($"[yellow]Reset proxy pool for token {MaskToken(currentTokenEntry.Token)}[/]");
            if (!_availableProxies.Any()) { AnsiConsole.MarkupLine("[red]No proxies to reset pool.[/]"); return false; }
            var s = _availableProxies.OrderBy(x => Guid.NewGuid()).ToList(); pool = new Queue<string>(s); _proxyPool[currentTokenEntry.Token] = pool;
            if (pool.Count > 0) {
                 // === FIX CS8600 Warning ===
                 // Peek() bisa return null jika queue kosong, tapi kita udah cek pool.Count > 0
                 // Tambahkan null check atau assertion untuk meyakinkan compiler
                 string? nextProxy = pool.Peek();
                 if (nextProxy != null) {
                    currentTokenEntry.Proxy = nextProxy;
                    AnsiConsole.MarkupLine($"[dim]Pool reset. Next: {MaskProxy(currentTokenEntry.Proxy)}[/]"); return true;
                 } else {
                     AnsiConsole.MarkupLine("[red]Pool reset logic error (Peek returned null?).[/]"); return false; // Seharusnya tidak terjadi
                 }
                 // === AKHIR FIX ===
            } else { AnsiConsole.MarkupLine("[red]Pool reset fail (no proxies).[/]"); return false; }
        }
        var nextProxyRotate = pool.Dequeue(); currentTokenEntry.Proxy = nextProxyRotate;
        AnsiConsole.MarkupLine($"[yellow]Proxy rotated to: {MaskProxy(nextProxyRotate)} ({pool.Count} left)[/]"); return true;
    }

    public static TokenEntry SwitchToNextToken() { if (!_tokens.Any() || _tokens.Count == 1) { AnsiConsole.MarkupLine("[yellow]Only 1 token.[/]"); return GetCurrentToken(); } _state.CurrentIndex = (_state.CurrentIndex + 1) % _tokens.Count; _state.ActiveCodespaceName = null; SaveState(_state); var current = _tokens[_state.CurrentIndex]; var username = current.Username ?? "unknown"; AnsiConsole.MarkupLine($"[yellow]Token Rotated: -> #{_state.CurrentIndex + 1} (@{username.EscapeMarkup()})[/]"); if (!string.IsNullOrEmpty(current.Proxy)) AnsiConsole.MarkupLine($"[dim]Proxy: {MaskProxy(current.Proxy)}[/]"); return current; }

    // === PERBAIKAN LOGIKA PARSING PROXY ===
    public static HttpClient CreateHttpClient(TokenEntry token) {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(token.Proxy)) {
            AnsiConsole.Markup($"[dim]   Using proxy {MaskProxy(token.Proxy)}[/]");
            try {
                // Langsung parse sebagai Uri
                var proxyUri = new Uri(token.Proxy, UriKind.Absolute);
                var webProxy = new WebProxy(proxyUri);

                // Cek jika ada user info (user:pass)
                if (!string.IsNullOrEmpty(proxyUri.UserInfo)) {
                    var credentials = proxyUri.UserInfo.Split(':', 2);
                    if (credentials.Length == 2) {
                        webProxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(credentials[0]), Uri.UnescapeDataString(credentials[1]));
                        webProxy.UseDefaultCredentials = false;
                        AnsiConsole.MarkupLine(" with credentials");
                    } else {
                        AnsiConsole.MarkupLine("[yellow] (Credentials format issue? Using proxy without auth)[/]");
                        webProxy.UseDefaultCredentials = true; // Coba tanpa auth jika format aneh
                    }
                } else {
                    webProxy.UseDefaultCredentials = true; // Proxy tanpa user/pass
                     AnsiConsole.MarkupLine(""); // Baris baru jika tanpa kredensial
                }
                handler.Proxy = webProxy;
                handler.UseProxy = true;
            } catch (UriFormatException ex) {
                AnsiConsole.MarkupLine($"[red]Proxy format invalid {MaskProxy(token.Proxy)}: {ex.Message.EscapeMarkup()}. Disabling proxy.[/]");
                handler.Proxy = null; handler.UseProxy = false;
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error setting proxy {MaskProxy(token.Proxy)}: {ex.Message.EscapeMarkup()}. Disabling proxy.[/]");
                handler.Proxy = null; handler.UseProxy = false;
            }
        } else {
            handler.Proxy = null; handler.UseProxy = false;
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(DEFAULT_HTTP_TIMEOUT_SEC) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/4.0"); // Versi naik
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }
    // === AKHIR PERBAIKAN ===

    public static string MaskToken(string token) { if (string.IsNullOrEmpty(token)) return "invalid"; return token.Length > 10 ? token[..4] + "..." + token[^4..] : token; }

    // === PERBAIKAN MASKING PROXY ===
    public static string MaskProxy(string? proxy) {
        if (string.IsNullOrEmpty(proxy)) return "no-proxy";
        try {
            // Coba parse sebagai Uri
            if (Uri.TryCreate(proxy, UriKind.Absolute, out var uri)) {
                // Tampilkan scheme://host:port (tanpa user:pass)
                return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            }
            // Fallback jika bukan URI valid (seharusnya tidak terjadi jika ProxySync benar)
            var parts = proxy.Split('@');
            if (parts.Length == 2) { // Ada user:pass@...
                var hostPort = parts[1].Split(':');
                if (hostPort.Length >= 2) return $"proxy://{hostPort[0]}:{hostPort[1]}";
            } else { // Tidak ada user:pass
                 var hostPort = proxy.Split(':');
                 if(hostPort.Length >= 2) return $"proxy://{hostPort[0]}:{hostPort[1]}";
            }
            return "masked-proxy"; // Gagal masking
        } catch { return "invalid-proxy-format"; }
    }
    // === AKHIR PERBAIKAN ===


    public static void ShowStatus() { if (!_tokens.Any()) { AnsiConsole.MarkupLine("[yellow]No tokens.[/]"); return; } var owner = _tokens.FirstOrDefault()?.Owner ?? "N/A"; var repo = _tokens.FirstOrDefault()?.Repo ?? "N/A"; var activeCs = _state.ActiveCodespaceName ?? "N/A"; AnsiConsole.MarkupLine($"[cyan]Owner:[/] [yellow]{owner.EscapeMarkup()}[/]"); AnsiConsole.MarkupLine($"[cyan]Repo:[/] [yellow]{repo.EscapeMarkup()}[/]"); AnsiConsole.MarkupLine($"[cyan]Active CS:[/] [yellow]{activeCs.EscapeMarkup()}[/]"); var table = new Table().Title("Tokens Status").Expand(); table.AddColumn("Idx"); table.AddColumn("Token"); table.AddColumn("User"); table.AddColumn("Proxy"); table.AddColumn("Active"); for (int i = 0; i < _tokens.Count; i++) { var t = _tokens[i]; var act = i == _state.CurrentIndex ? "[green]Active[/]" : ""; var tokD = MaskToken(t.Token); var userD = t.Username ?? "[grey]unknown[/]"; var proxD = MaskProxy(t.Proxy); table.AddRow((i+1).ToString(), tokD, userD, proxD, act); } AnsiConsole.Write(table); AnsiConsole.MarkupLine($"[dim]Proxies available: {_availableProxies.Count}[/]"); }
}

public class TokenEntry { public string Token { get; set; } = string.Empty; public string? Proxy { get; set; } public string? Username { get; set; } public string Owner { get; set; } = string.Empty; public string Repo { get; set; } = string.Empty; }
public class TokenState { [JsonPropertyName("current_index")] public int CurrentIndex { get; set; } = 0; [JsonPropertyName("active_codespace_name")] public string? ActiveCodespaceName { get; set; } = null; }

