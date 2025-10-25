using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class TokenManager
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    
    private static readonly string TokensPath = Path.Combine(ConfigRoot, "github_tokens.txt");
    private static readonly string ProxyListPath = Path.Combine(ProjectRoot, "proxysync", "proxy.txt");
    
    private static readonly string StatePath = Path.Combine(ProjectRoot, ".token-state.json");
    private static readonly string TokenCachePath = Path.Combine(ProjectRoot, ".token-cache.json"); 

    private static List<TokenEntry> _tokens = new();
    private static List<string> _proxyList = new();
    private static TokenState _state = new();
    private static Dictionary<string, string> _tokenCache = new(); 

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
        
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    public static void Initialize()
    {
        AnsiConsole.MarkupLine($"[dim]Project Root: {ProjectRoot}[/]");
        AnsiConsole.MarkupLine($"[dim]Config Root: {ConfigRoot}[/]");
        
        LoadTokens();
        LoadProxyList();
        LoadState();
        LoadTokenCache(); 
        AssignProxiesAndUsernames();
    }

    public static void ReloadAllConfigs()
    {
        AnsiConsole.MarkupLine("[bold yellow]Reloading all configuration files...[/]");
        _tokens.Clear();
        _proxyList.Clear();
        _tokenCache.Clear();
        _state = new TokenState();
        
        Initialize();
        AnsiConsole.MarkupLine("[bold green]✓ Konfigurasi berhasil di-refresh.[/]");
    }

    private static void LoadTokenCache() 
    {
        if (!File.Exists(TokenCachePath)) return;
        try
        {
            var json = File.ReadAllText(TokenCachePath);
            _tokenCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) 
                          ?? new Dictionary<string, string>();
            AnsiConsole.MarkupLine($"[dim]Loaded {_tokenCache.Count} usernames from cache[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Gagal memuat token cache: {ex.Message}[/]");
            _tokenCache = new Dictionary<string, string>();
        }
    }

    public static void SaveTokenCache(Dictionary<string, string> cache) 
    {
        try
        {
            _tokenCache = cache;
            var json = JsonSerializer.Serialize(_tokenCache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(TokenCachePath, json);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error saving token cache: {ex.Message}[/]");
        }
    }

    private static void LoadTokens()
    {
        if (!File.Exists(TokensPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR: {TokensPath} tidak ditemukan![/]");
            AnsiConsole.MarkupLine("[yellow]Buat file dengan format:[/]");
            AnsiConsole.MarkupLine("[dim]Line 1: owner (misal: Kyugito666)[/]");
            AnsiConsole.MarkupLine("[dim]Line 2: repo (misal: automation-hub)[/]");
            AnsiConsole.MarkupLine("[dim]Line 3: token1,token2,token3[/]");
            
            try
            {
                Directory.CreateDirectory(ConfigRoot);
                File.WriteAllLines(TokensPath, new[]
                {
                    "YourGitHubUsername",
                    "automation-hub",
                    "ghp_YourToken1,ghp_YourToken2"
                });
                AnsiConsole.MarkupLine($"[green]✓ Template file dibuat di: {TokensPath}[/]");
                AnsiConsole.MarkupLine("[yellow]Silakan edit file tersebut dengan data Anda![/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Gagal membuat template: {ex.Message}[/]");
            }
            return;
        }

        var lines = File.ReadAllLines(TokensPath);
        
        if (lines.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]ERROR: github_tokens.txt format salah![/]");
            AnsiConsole.MarkupLine($"[yellow]File hanya punya {lines.Length} baris, butuh minimal 3[/]");
            return;
        }

        var owner = lines[0].Trim();
        var repo = lines[1].Trim();
        
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]ERROR: Owner atau Repo kosong![/]");
            return;
        }
        
        if (string.IsNullOrWhiteSpace(lines[2]))
        {
            AnsiConsole.MarkupLine("[red]ERROR: Baris 3 (tokens) kosong![/]");
            return;
        }
        
        var tokens = lines[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        
        if (tokens.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR: Tidak ada token valid di baris 3![/]");
            return;
        }
        
        _tokens = tokens.Select(t => new TokenEntry 
        { 
            Token = t.Trim(),
            Owner = owner,
            Repo = repo
        }).ToList();
        
        AnsiConsole.MarkupLine($"[green]✓ Loaded {_tokens.Count} tokens for {owner}/{repo}[/]");
    }

    private static void LoadProxyList()
    {
        if (File.Exists(ProxyListPath))
        {
            _proxyList = File.ReadAllLines(ProxyListPath)
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                .ToList();
            
            AnsiConsole.MarkupLine($"[dim]Loaded {_proxyList.Count} proxies from ProxySync[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: {ProxyListPath} not found. Running without proxies.[/]");
        }
    }

    private static void AssignProxiesAndUsernames() 
    {
        if (!_tokens.Any()) return;

        for (int i = 0; i < _tokens.Count; i++)
        {
            if (_proxyList.Any())
            {
                var proxyIndex = i % _proxyList.Count;
                _tokens[i].Proxy = _proxyList[proxyIndex];
            }
            
            if (_tokenCache.TryGetValue(_tokens[i].Token, out var username))
            {
                _tokens[i].Username = username;
            }
        }
    }

    private static void LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                _state = JsonSerializer.Deserialize<TokenState>(json) ?? new TokenState();
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error loading state: {ex.Message}. Resetting state.[/]");
            _state = new TokenState();
        }
        
        if (_state.CurrentIndex >= _tokens.Count)
        {
            _state.CurrentIndex = 0;
        }
    }

    public static TokenState GetState()
    {
        return _state;
    }

    public static void SaveState(TokenState state)
    {
        _state = state;
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StatePath, json);
    }

    public static TokenEntry GetCurrentToken()
    {
        if (!_tokens.Any())
        {
            throw new Exception("No tokens configured in github_tokens.txt");
        }
        
        if (_state.CurrentIndex >= _tokens.Count)
        {
            _state.CurrentIndex = 0;
        }

        return _tokens[_state.CurrentIndex];
    }
    
    public static List<TokenEntry> GetAllTokenEntries()
    {
        return _tokens;
    }
    
    public static Dictionary<string, string> GetUsernameCache()
    {
        return _tokenCache;
    }

    public static TokenEntry SwitchToNextToken()
    {
        if (!_tokens.Any() || _tokens.Count == 1)
        {
            AnsiConsole.MarkupLine("[yellow]Hanya 1 token tersedia, tidak bisa rotasi.[/]");
            return GetCurrentToken();
        }

        _state.CurrentIndex = (_state.CurrentIndex + 1) % _tokens.Count;
        SaveState(_state);

        var current = _tokens[_state.CurrentIndex];
        AnsiConsole.MarkupLine($"[bold yellow]🔁 Token Rotated[/]: Now using Token #{_state.CurrentIndex + 1} (@{current.Username ?? "???"})");
        
        _state.ActiveCodespaceName = null;
        
        if (!string.IsNullOrEmpty(current.Proxy))
        {
            AnsiConsole.MarkupLine($"[dim]   Proxy: {MaskProxy(current.Proxy)}[/]");
        }
        return current;
    }

    public static HttpClient CreateHttpClient(TokenEntry token)
    {
        var handler = new HttpClientHandler();
        
        if (!string.IsNullOrEmpty(token.Proxy))
        {
            try
            {
                handler.Proxy = new WebProxy(token.Proxy);
                handler.UseProxy = true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Invalid proxy format: {token.Proxy}. Error: {ex.Message}[/]");
            }
        }

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/3.0");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        
        return client;
    }
    
    public static string MaskToken(string token) 
    {
        return token.Length > 20 
            ? token[..10] + "..." + token[^7..] 
            : token;
    }
    
    private static string MaskProxy(string proxy)
    {
        try
        {
            var uri = new Uri(proxy);
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        }
        catch
        {
            return "invalid-proxy-format";
        }
    }

    public static void ShowStatus()
    {
        if (!_tokens.Any()) 
        {
            AnsiConsole.MarkupLine("[red]No tokens loaded![/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold cyan]Owner:[/][yellow] {_tokens.FirstOrDefault()?.Owner ?? "N/A"}[/]");
        AnsiConsole.MarkupLine($"[bold cyan]Repo:[/][yellow] {_tokens.FirstOrDefault()?.Repo ?? "N/A"}[/]");
        AnsiConsole.MarkupLine($"[bold cyan]Active Codespace:[/][yellow] {_state.ActiveCodespaceName ?? "N/A"}[/]");

        var table = new Table().Title("GitHub Tokens Status").Expand();
        table.AddColumn("Index");
        table.AddColumn("Token");
        table.AddColumn("Username"); 
        table.AddColumn("Proxy");
        table.AddColumn("Active");

        for (int i = 0; i < _tokens.Count; i++)
        {
            var token = _tokens[i];
            var isActive = i == _state.CurrentIndex ? "[green]✓[/]" : "";
            var tokenDisplay = MaskToken(token.Token);
            var proxyDisplay = !string.IsNullOrEmpty(token.Proxy) ? MaskProxy(token.Proxy) : "[grey]none[/]";
            
            table.AddRow(
                (i + 1).ToString(),
                tokenDisplay,
                token.Username ?? "[grey]???[/]", 
                proxyDisplay,
                isActive
            );
        }
        AnsiConsole.Write(table);
    }
}

public class TokenEntry
{
    public string Token { get; set; } = string.Empty;
    public string? Proxy { get; set; }
    public string? Username { get; set; } 
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
}

public class TokenState
{
    [JsonPropertyName("current_index")]
    public int CurrentIndex { get; set; } = 0;

    [JsonPropertyName("active_codespace_name")]
    public string? ActiveCodespaceName { get; set; } = null;
}
