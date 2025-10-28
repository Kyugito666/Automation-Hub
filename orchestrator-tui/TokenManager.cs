using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions; // <- Tambah ini untuk Regex
using Spectre.Console;

namespace Orchestrator;

public static class TokenManager
{
    private static readonly string ConfigRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config"));
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string TokensPath = Path.Combine(ConfigRoot, "github_tokens.txt");
    
    // Prioritas success_proxy.txt (hasil test yang pasti bekerja)
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

    public static void Initialize() {
        LoadTokens(); 
        LoadProxyList(); 
        LoadState(); 
        LoadTokenCache(); 
        AssignProxiesAndUsernames(); 
        InitializeProxyPool();
    }
    
    public static void ReloadAllConfigs() {
        AnsiConsole.MarkupLine("[yellow]Reloading configurations...[/]"); 
        _tokens.Clear(); 
        _availableProxies.Clear(); 
        _tokenCache.Clear(); 
        _state = new TokenState(); 
        _proxyPool.Clear(); 
        Initialize(); 
        AnsiConsole.MarkupLine("[green]Configurations refreshed.[/]");
    }

    private static void LoadTokenCache() {
        if (!File.Exists(TokenCachePath)) return;
        try {
            var json = File.ReadAllText(TokenCachePath); 
            _tokenCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            AnsiConsole.MarkupLine($"[dim]Loaded {_tokenCache.Count} usernames from cache[/]");
        } catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to load cache: {ex.Message.EscapeMarkup()}[/]"); 
            _tokenCache = new(); 
        }
    }
    
    public static void SaveTokenCache(Dictionary<string, string> cache) {
        try { 
            _tokenCache = cache; 
            var json = JsonSerializer.Serialize(_tokenCache, new JsonSerializerOptions { WriteIndented = true }); 
            File.WriteAllText(TokenCachePath, json); 
        } catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]Error saving cache: {ex.Message.EscapeMarkup()}[/]"); 
        }
    }
    
    private static void LoadTokens() {
        if (!File.Exists(TokensPath)) { 
            AnsiConsole.MarkupLine($"[red]ERROR: {TokensPath.EscapeMarkup()} not found![/]"); 
            return; 
        }
        
        var lines = File.ReadAllLines(TokensPath); 
        if (lines.Length < 3) { 
            AnsiConsole.MarkupLine("[red]ERROR: github_tokens.txt format incorrect![/]"); 
            return; 
        }
        
        var owner = lines[0].Trim(); 
        var repo = lines[1].Trim(); 
        var tokens = lines[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        
        _tokens = tokens.Select(t => new TokenEntry { Token = t, Owner = owner, Repo = repo }).ToList();
        
        AnsiConsole.MarkupLine($"[green]Loaded {_tokens.Count} tokens for {owner.EscapeMarkup()}/{repo.EscapeMarkup()}[/]");
    }
    
    private static void LoadProxyList() {
        // Prioritas success_proxy.txt
        string proxyFileToLoad = File.Exists(SuccessProxyListPath) && new FileInfo(SuccessProxyListPath).Length > 0 ? SuccessProxyListPath : 
                                 File.Exists(FallbackProxyListPath) ? FallbackProxyListPath : null;
        
        if (proxyFileToLoad == null) {
            AnsiConsole.MarkupLine("[red]CRITICAL: Tidak ada file proxy yang tersedia![/]");
            AnsiConsole.MarkupLine("[yellow]Jalankan 'auto-start.sh' di remote atau Menu 3 lokal.[/]");
            _availableProxies.Clear();
            return;
        }
        
        try { 
            _availableProxies = File.ReadAllLines(proxyFileToLoad)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#") && l.StartsWith("http://")) // Pastikan format http
                .Distinct()
                .ToList();
            
            if (proxyFileToLoad == SuccessProxyListPath) {
                AnsiConsole.MarkupLine($"[green]✓ {_availableProxies.Count} tested proxies dari success_proxy.txt[/]");
            } else {
                AnsiConsole.MarkupLine($"[yellow]⚠ Pakai fallback proxy.txt ({_availableProxies.Count} proxies)[/]");
                AnsiConsole.MarkupLine("[yellow]  Jalankan 'auto-start.sh' di remote untuk proxy yang sudah ditest![/]");
            }
            if(_availableProxies.Count == 0) {
                 AnsiConsole.MarkupLine($"[red]ERROR: File proxy '{Path.GetFileName(proxyFileToLoad)}' tidak berisi proxy valid (format http://...).[/]");
            }

        } catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]Error loading proxies: {ex.Message.EscapeMarkup()}[/]"); 
            _availableProxies.Clear(); 
        }
    }
    
    private static void AssignProxiesAndUsernames() {
        if (!_tokens.Any()) return;
        
        for (int i = 0; i < _tokens.Count; i++) {
            if (_availableProxies.Any()) { 
                _tokens[i].Proxy = _availableProxies[i % _availableProxies.Count]; 
            } else { 
                _tokens[i].Proxy = null; 
            }
            
            if (_tokenCache.TryGetValue(_tokens[i].Token, out var u)) { 
                _tokens[i].Username = u; 
            }
        }
    }
    
    private static void InitializeProxyPool() {
        _proxyPool.Clear(); 
        if (!_availableProxies.Any()) return;
        
        foreach (var t in _tokens) { 
            var s = _availableProxies.OrderBy(x => Guid.NewGuid()).ToList(); 
            _proxyPool[t.Token] = new Queue<string>(s); 
        }
        
        AnsiConsole.MarkupLine($"[dim]Initialized proxy pool for {_proxyPool.Count} tokens.[/]");
    }
    
    private static void LoadState() {
        try { 
            if (File.Exists(StatePath)) { 
                var json = File.ReadAllText(StatePath); 
                _state = JsonSerializer.Deserialize<TokenState>(json) ?? new TokenState(); 
            } 
        } catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]Error loading state: {ex.Message.EscapeMarkup()}. Reset.[/]"); 
            _state = new TokenState(); 
        }
        
        if (_state.CurrentIndex >= _tokens.Count && _tokens.Any()) { 
            _state.CurrentIndex = 0; 
        }
    }

    public static TokenState GetState() => _state;
    
    public static void SaveState(TokenState state) {
        _state = state; 
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        
        try { 
            File.WriteAllText(StatePath, json); 
        } catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]Error saving state: {ex.Message.EscapeMarkup()}[/]"); 
        }
    }

    public static TokenEntry GetCurrentToken() {
        if (!_tokens.Any()) throw new Exception("No tokens configured");
        if (_state.CurrentIndex >= _tokens.Count || _state.CurrentIndex < 0) _state.CurrentIndex = 0; // Tambah cek < 0
        return _tokens[_state.CurrentIndex];
    }
    
    public static List<TokenEntry> GetAllTokenEntries() => _tokens;
    public static Dictionary<string, string> GetUsernameCache() => _tokenCache;

    public static bool RotateProxyForToken(TokenEntry currentTokenEntry) {
        if (!_proxyPool.TryGetValue(currentTokenEntry.Token, out var pool) || pool.Count == 0) {
            AnsiConsole.MarkupLine($"[yellow]Resetting proxy pool for token {MaskToken(currentTokenEntry.Token)}[/]");
            
            if (!_availableProxies.Any()) { 
                AnsiConsole.MarkupLine("[red]No available proxies to reset pool.[/]"); 
                return false; 
            }
            
            var s = _availableProxies.OrderBy(x => Guid.NewGuid()).ToList(); 
            pool = new Queue<string>(s); 
            _proxyPool[currentTokenEntry.Token] = pool;
            
            if (pool.Count > 0) { 
                // Peek() dulu untuk update currentTokenEntry.Proxy
                // Dequeue() baru saat CreateHttpClient() dipanggil lagi
                currentTokenEntry.Proxy = pool.Peek(); 
                AnsiConsole.MarkupLine($"[dim]Pool reset. Next proxy will be: {MaskProxy(currentTokenEntry.Proxy)}[/]"); 
                return true; 
            } else { 
                AnsiConsole.MarkupLine("[red]Pool reset failed (no proxies).[/]"); 
                return false; 
            }
        }
        
        // Dequeue proxy lama, enqueue lagi ke belakang
        var oldProxy = pool.Dequeue();
        pool.Enqueue(oldProxy);
        
        // Ambil proxy baru dari depan
        var nextProxy = pool.Peek(); 
        currentTokenEntry.Proxy = nextProxy;
        AnsiConsole.MarkupLine($"[yellow]Proxy rotated to: {MaskProxy(nextProxy)} ({pool.Count} available)[/]");
        return true;
    }

    public static TokenEntry SwitchToNextToken() {
        if (!_tokens.Any() || _tokens.Count == 1) { 
            AnsiConsole.MarkupLine("[yellow]Only 1 token, cannot rotate.[/]"); 
            return GetCurrentToken(); 
        }
        
        _state.CurrentIndex = (_state.CurrentIndex + 1) % _tokens.Count;
        _state.ActiveCodespaceName = null; 
        SaveState(_state);
        
        var current = _tokens[_state.CurrentIndex];
        var username = current.Username ?? "unknown";
        
        AnsiConsole.MarkupLine($"[yellow]Token Rotated: -> #{_state.CurrentIndex + 1} (@{username.EscapeMarkup()})[/]");
        
        // Reset proxy pool index untuk token baru ini
        if (_proxyPool.TryGetValue(current.Token, out var pool) && pool.Any())
        {
             current.Proxy = pool.Peek(); // Ambil proxy pertama dari pool-nya
             AnsiConsole.MarkupLine($"[dim]Proxy: {MaskProxy(current.Proxy)}[/]"); 
        }
        else {
             AnsiConsole.MarkupLine($"[yellow]Warning: No proxy pool for token {MaskToken(current.Token)}[/]");
        }
        
        return current;
    }

    // === PERBAIKAN LOGIKA PARSING PROXY ===
    public static HttpClient CreateHttpClient(TokenEntry token) {
        var handler = new HttpClientHandler();
        
        if (!string.IsNullOrEmpty(token.Proxy) && token.Proxy.StartsWith("http://")) {
            try {
                // Gunakan Regex untuk mengekstrak user, pass, host, port
                // Contoh: http://user:pass@1.2.3.4:8080
                var match = Regex.Match(token.Proxy, @"^http://(?:([^:@]+):([^@]+)@)?([^:]+):(\d+)$");

                if (match.Success)
                {
                    string host = match.Groups[3].Value;
                    string port = match.Groups[4].Value;
                    string user = match.Groups[1].Value; // Bisa kosong
                    string pass = match.Groups[2].Value; // Bisa kosong
                    
                    // Buat Uri hanya dengan host dan port untuk WebProxy
                    var proxyUriOnlyHost = new Uri($"http://{host}:{port}");
                    var webProxy = new WebProxy(proxyUriOnlyHost);

                    // Jika ada user & pass, set Credentials
                    if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                    {
                        webProxy.Credentials = new NetworkCredential(user, pass);
                        webProxy.UseDefaultCredentials = false;
                        AnsiConsole.MarkupLine($"[dim]   Using proxy {MaskProxy(token.Proxy)} with credentials[/]");
                    }
                    else
                    {
                        webProxy.UseDefaultCredentials = true; // Atau false jika proxy tanpa auth
                        AnsiConsole.MarkupLine($"[dim]   Using proxy {MaskProxy(token.Proxy)} without credentials[/]");
                    }

                    handler.Proxy = webProxy;
                    handler.UseProxy = true;
                }
                else
                {
                    throw new FormatException("Regex did not match expected proxy format.");
                }
            } 
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[red]Proxy parsing ERROR {MaskProxy(token.Proxy)}: {ex.Message.EscapeMarkup()}. Disabling proxy for this request.[/]"); 
                handler.Proxy = null; 
                handler.UseProxy = false; 
            }
        } else { 
            if (!string.IsNullOrEmpty(token.Proxy)) {
                 AnsiConsole.MarkupLine($"[yellow]Warning: Invalid proxy format detected (must start with http://): {MaskProxy(token.Proxy)}. Disabling proxy.[/]");
            } else {
                 AnsiConsole.MarkupLine("[dim]   No proxy assigned for this token.[/]");
            }
            handler.Proxy = null; 
            handler.UseProxy = false; 
        }
        
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(DEFAULT_HTTP_TIMEOUT_SEC) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}"); 
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/3.3"); // Versi naik
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        
        return client;
    }
    // === AKHIR PERBAIKAN ===


    public static string MaskToken(string token) {
        if (string.IsNullOrEmpty(token)) return "invalid-token";
        return token.Length > 10 ? token[..4] + "..." + token[^4..] : token;
    }

    // === PERBAIKAN MASKING PROXY ===
    public static string MaskProxy(string? proxy) {
        if (string.IsNullOrEmpty(proxy)) return "no-proxy";
        
        try { 
             // Coba parse pakai Uri class
             if (Uri.TryCreate(proxy, UriKind.Absolute, out var uri)) { 
                 // Tampilkan skema, host, port. User info disembunyikan.
                 return $"{uri.Scheme}://{uri.Host}:{uri.Port}"; 
             }
             // Fallback jika Uri.TryCreate gagal (format aneh)
             return "masked-proxy-invalid-format"; 
        } catch { 
            return "error-masking-proxy"; 
        }
    }
    // === AKHIR PERBAIKAN ===


    public static void ShowStatus() {
        if (!_tokens.Any()) { 
            AnsiConsole.MarkupLine("[yellow]No tokens loaded.[/]"); 
            return; 
        }
        
        var owner = _tokens.FirstOrDefault()?.Owner ?? "N/A";
        var repo = _tokens.FirstOrDefault()?.Repo ?? "N/A";
        var activeCs = _state.ActiveCodespaceName ?? "N/A";
        
        AnsiConsole.MarkupLine($"[cyan]Owner:[/] [yellow]{owner.EscapeMarkup()}[/]"); 
        AnsiConsole.MarkupLine($"[cyan]Repo:[/] [yellow]{repo.EscapeMarkup()}[/]"); 
        AnsiConsole.MarkupLine($"[cyan]Active Codespace:[/] [yellow]{activeCs.EscapeMarkup()}[/]");
        
        var table = new Table().Title("Tokens Status").Expand(); 
        table.AddColumn("Idx"); 
        table.AddColumn("Token"); 
        table.AddColumn("User"); 
        table.AddColumn("Current Proxy"); // Nama kolom diubah
        table.AddColumn("Active");
        
        for (int i = 0; i < _tokens.Count; i++) { 
            var t = _tokens[i]; 
            var act = i == _state.CurrentIndex ? "[green]Active[/]" : ""; 
            var tokD = MaskToken(t.Token); 
            var userD = t.Username ?? "[grey]unknown[/]";
            var proxD = MaskProxy(t.Proxy); // Gunakan fungsi MaskProxy yang baru
            
            table.AddRow((i+1).ToString(), tokD, userD, proxD, act); 
        }
        
        AnsiConsole.Write(table); 
        AnsiConsole.MarkupLine($"[dim]Proxies available for rotation: {_availableProxies.Count}[/]");
    }
}

public class TokenEntry { 
    public string Token { get; set; } = string.Empty; 
    public string? Proxy { get; set; } 
    public string? Username { get; set; } 
    public string Owner { get; set; } = string.Empty; 
    public string Repo { get; set; } = string.Empty; 
}

public class TokenState { 
    [JsonPropertyName("current_index")] 
    public int CurrentIndex { get; set; } = 0; 
    
    [JsonPropertyName("active_codespace_name")] 
    public string? ActiveCodespaceName { get; set; } = null; 
}
