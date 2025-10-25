using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class TokenManager
{
    private static readonly string ConfigRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config"));
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string TokensPath = Path.Combine(ConfigRoot, "github_tokens.txt");
    private static readonly string MasterProxyListPath = Path.Combine(ProjectRoot, "proxysync", "proxy.txt");
    private static readonly string SuccessProxyListPath = Path.Combine(ProjectRoot, "proxysync", "success_proxy.txt");
    private static readonly string StatePath = Path.Combine(ProjectRoot, ".token-state.json");
    private static readonly string TokenCachePath = Path.Combine(ProjectRoot, ".token-cache.json");

    private static List<TokenEntry> _tokens = new();
    private static List<string> _availableProxies = new();
    private static TokenState _state = new();
    private static Dictionary<string, string> _tokenCache = new();
    private static Dictionary<string, Queue<string>> _proxyPool = new();

    public static void Initialize() { /* ... */ LoadTokens(); LoadProxyList(); LoadState(); LoadTokenCache(); AssignProxiesAndUsernames(); InitializeProxyPool(); }
    public static void ReloadAllConfigs() { /* ... */ AnsiConsole.MarkupLine("[bold yellow]Reloading...[/]"); _tokens.Clear(); _availableProxies.Clear(); _tokenCache.Clear(); _state = new TokenState(); _proxyPool.Clear(); Initialize(); AnsiConsole.MarkupLine("[bold green]✓ Refreshed.[/]"); }
    private static void LoadTokenCache() { /* ... */ if (!File.Exists(TokenCachePath)) return; try { var json = File.ReadAllText(TokenCachePath); _tokenCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); AnsiConsole.MarkupLine($"[dim]Loaded {_tokenCache.Count} usernames from cache[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn: Load cache fail: {ex.Message}[/]"); _tokenCache = new(); } }
    public static void SaveTokenCache(Dictionary<string, string> cache) { /* ... */ try { _tokenCache = cache; var json = JsonSerializer.Serialize(_tokenCache, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(TokenCachePath, json); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error save cache: {ex.Message}[/]"); } }
    private static void LoadTokens() { /* ... */ if (!File.Exists(TokensPath)) { /* Error msg */ return; } var lines = File.ReadAllLines(TokensPath); if (lines.Length < 3) { /* Error msg */ return; } var owner = lines[0].Trim(); var repo = lines[1].Trim(); var tokens = lines[2].Split(',', StringSplitOptions.RemoveEmptyEntries); _tokens = tokens.Select(t => new TokenEntry { Token = t.Trim(), Owner = owner, Repo = repo }).ToList(); AnsiConsole.MarkupLine($"[green]Loaded {_tokens.Count} tokens for {owner}/{repo}[/]"); }
    private static void LoadProxyList() { /* ... */ string proxyFileToLoad = File.Exists(SuccessProxyListPath) ? SuccessProxyListPath : MasterProxyListPath; if (File.Exists(proxyFileToLoad)) { try { _availableProxies = File.ReadAllLines(proxyFileToLoad).Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")).Distinct().ToList(); /* Log loaded */ } catch (Exception ex) { /* Log error load */ _availableProxies.Clear(); } } else { /* Log file not found */ _availableProxies.Clear(); } }
    private static void AssignProxiesAndUsernames() { /* ... */ if (!_tokens.Any()) return; for (int i = 0; i < _tokens.Count; i++) { if (_availableProxies.Any()) { _tokens[i].Proxy = _availableProxies[i % _availableProxies.Count]; } else { _tokens[i].Proxy = null; } if (_tokenCache.TryGetValue(_tokens[i].Token, out var u)) { _tokens[i].Username = u; } } }
    private static void InitializeProxyPool() { /* ... */ _proxyPool.Clear(); if (!_availableProxies.Any()) return; foreach (var t in _tokens) { var s = _availableProxies.OrderBy(x => Guid.NewGuid()).ToList(); _proxyPool[t.Token] = new Queue<string>(s); } AnsiConsole.MarkupLine($"[dim]Initialized proxy pool for {_proxyPool.Count} tokens.[/]"); }
    public static bool RotateProxyForToken(TokenEntry currentTokenEntry) { /* ... */ if (!_proxyPool.TryGetValue(currentTokenEntry.Token, out var pool) || pool.Count == 0) { AnsiConsole.MarkupLine($"[yellow]🔄 Resetting proxy pool for token {MaskToken(currentTokenEntry.Token)}[/]"); if (!_availableProxies.Any()) return false; var s = _availableProxies.OrderBy(x => Guid.NewGuid()).ToList(); pool = new Queue<string>(s); _proxyPool[currentTokenEntry.Token] = pool; if (pool.Count > 0) { currentTokenEntry.Proxy = pool.Peek(); AnsiConsole.MarkupLine($"[dim]   Pool reset. Next: {MaskProxy(currentTokenEntry.Proxy)}[/]"); return true; } else { return false; } } var nextProxy = pool.Dequeue(); currentTokenEntry.Proxy = nextProxy; AnsiConsole.MarkupLine($"[yellow]🔁 Proxy rotated to: {MaskProxy(nextProxy)} ({pool.Count} left)[/]"); return true; }
    private static void LoadState() { /* ... */ try { if (File.Exists(StatePath)) { var json = File.ReadAllText(StatePath); _state = JsonSerializer.Deserialize<TokenState>(json) ?? new TokenState(); } } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error load state: {ex.Message}. Reset.[/]"); _state = new TokenState(); } if (_state.CurrentIndex >= _tokens.Count) { _state.CurrentIndex = 0; } }
    public static TokenState GetState() => _state;
    public static void SaveState(TokenState state) { _state = state; var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }); try { File.WriteAllText(StatePath, json); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error save state: {ex.Message}[/]"); } }
    public static TokenEntry GetCurrentToken() { if (!_tokens.Any()) throw new Exception("No tokens"); if (_state.CurrentIndex >= _tokens.Count) _state.CurrentIndex = 0; return _tokens[_state.CurrentIndex]; }
    public static List<TokenEntry> GetAllTokenEntries() => _tokens; // Nama method yang benar
    public static Dictionary<string, string> GetUsernameCache() => _tokenCache; // Nama method yang benar

    public static TokenEntry SwitchToNextToken() {
        if (!_tokens.Any() || _tokens.Count == 1) { /* Log */ return GetCurrentToken(); }
        _state.CurrentIndex = (_state.CurrentIndex + 1) % _tokens.Count;
        _state.ActiveCodespaceName = null; SaveState(_state);
        var current = _tokens[_state.CurrentIndex];
        AnsiConsole.MarkupLine($"[bold yellow]🔁 Token Rotated[/]: → #{_state.CurrentIndex + 1} (@{current.Username ?? "???"})");
        if (!string.IsNullOrEmpty(current.Proxy)) { AnsiConsole.MarkupLine($"[dim]   Proxy: {MaskProxy(current.Proxy)}[/]"); } // Panggil public
        return current;
    }

    public static HttpClient CreateHttpClient(TokenEntry token) {
         var handler = new HttpClientHandler();
         if (!string.IsNullOrEmpty(token.Proxy)) { try { handler.Proxy = new WebProxy(token.Proxy); handler.UseProxy = true; } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Proxy invalid {MaskProxy(token.Proxy)}: {ex.Message}[/]"); } } // Panggil public
        var client = new HttpClient(handler); client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}"); client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/3.1"); client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json"); return client;
    }

    public static string MaskToken(string token) { return token.Length > 10 ? token[..4] + "..." + token[^4..] : token; }

    // --- PERBAIKAN DI SINI ---
    public static string MaskProxy(string proxy) // <<< PASTIKAN PUBLIC
    // --- AKHIR PERBAIKAN ---
    {
         try { if (Uri.TryCreate(proxy, UriKind.Absolute, out var uri)) { return $"{uri.Scheme}://{uri.Host}:{uri.Port}"; } var parts = proxy.Split(':'); if (parts.Length >= 2) { string hostPart = parts.Length > 2 ? parts[^2].Split('@').Last() : parts[^2]; string portPart = parts[^1]; return $"???://{hostPart}:{portPart}"; } return "masked-proxy"; } catch { return "invalid-proxy-format"; }
    }

    public static void ShowStatus() { /* ... */ if (!_tokens.Any()) { /* Log */ return; } /* Log Owner/Repo/Codespace */ var table = new Table().Title("Tokens Status").Expand(); table.AddColumn("Idx"); table.AddColumn("Token"); table.AddColumn("User"); table.AddColumn("Proxy"); table.AddColumn("Active"); for (int i = 0; i < _tokens.Count; i++) { var t = _tokens[i]; var act = i == _state.CurrentIndex ? "[g✓[/]" : ""; var tokD = MaskToken(t.Token); var proxD = !string.IsNullOrEmpty(t.Proxy) ? MaskProxy(t.Proxy) : "[g none[/]"; table.AddRow((i + 1).ToString(), tokD, t.Username ?? "[g???[/]", proxD, act); } AnsiConsole.Write(table); AnsiConsole.MarkupLine($"[dim] Proxies available: {_availableProxies.Count}[/]"); }
}

// --- Kelas Model ---
public class TokenEntry { public string Token{get;set;}=string.Empty; public string? Proxy{get;set;} public string? Username{get;set;} public string Owner{get;set;}=string.Empty; public string Repo{get;set;}=string.Empty; }
public class TokenState { [JsonPropertyName("current_index")] public int CurrentIndex{get;set;}=0; [JsonPropertyName("active_codespace_name")] public string? ActiveCodespaceName{get;set;}=null; }
