using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public class TokenManager
{
    private static readonly string TokensPath = "../config/github_tokens.txt";
    private static readonly string StatePath = "../.token-state.json";
    private static readonly string ProxyListPath = "../proxysync/proxy.txt";
    private static readonly string TokenCachePath = "../.token-cache.json"; 

    private static List<TokenEntry> _tokens = new();
    private static List<string> _proxyList = new();
    private static TokenState? _state;
    private static string _owner = "";
    private static string _repo = "";
    
    private static Dictionary<string, string> _tokenCache = new(); 

    public static void Initialize()
    {
        LoadTokens();
        LoadProxyList();
        LoadState();
        LoadTokenCache(); 
        AssignProxiesAndUsernames(); 
    }

    // ==========================================================
    // METHOD BARU UNTUK REFRESH
    // ==========================================================
    public static void ReloadAllConfigs()
    {
        AnsiConsole.MarkupLine("[bold yellow]Reloading all configuration files...[/]");

        // 1. Bersihkan semua data yang di-cache di memori
        _tokens.Clear();
        _proxyList.Clear();
        _tokenCache.Clear();
        _owner = "";
        _repo = "";
        _state = null; // Akan memaksa LoadState() membaca ulang

        // 2. Panggil ulang semua method Inisialisasi
        Initialize();

        AnsiConsole.MarkupLine("[bold green]✓ Konfigurasi berhasil di-refresh.[/]");
    }
    // ==========================================================

    private static void LoadTokenCache() 
    {
        if (File.Exists(TokenCachePath))
        {
            try
            {
                var json = File.ReadAllText(TokenCachePath);
                _tokenCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) 
                              ?? new Dictionary<string, string>();
                AnsiConsole.MarkupLine($"[green]Loaded {_tokenCache.Count} usernames from cache[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Gagal memuat token cache: {ex.Message}[/]");
                _tokenCache = new Dictionary<string, string>();
            }
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
            AnsiConsole.MarkupLine("[dim]Line 1: owner[/]");
            AnsiConsole.MarkupLine("[dim]Line 2: repo[/]");
            AnsiConsole.MarkupLine("[dim]Line 3: token1,token2,token3[/]");
            return;
        }

        var lines = File.ReadAllLines(TokensPath);
        
        if (lines.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]ERROR: github_tokens.txt format salah![/]");
            return;
        }

        _owner = lines[0].Trim();
        _repo = lines[1].Trim();
        
        var tokens = lines[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
        _tokens = tokens.Select(t => new TokenEntry { Token = t.Trim() }).ToList();
        
        AnsiConsole.MarkupLine($"[green]Loaded {_tokens.Count} tokens for {_owner}/{_repo}[/]");
    }

    private static void LoadProxyList()
    {
        if (File.Exists(ProxyListPath))
        {
            _proxyList = File.ReadAllLines(ProxyListPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            
            AnsiConsole.MarkupLine($"[green]Loaded {_proxyList.Count} proxies from ProxySync[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]ProxySync proxy.txt not found[/]");
        }
    }

    private static void AssignProxiesAndUsernames() 
    {
        if (!_tokens.Any()) return;

        for (int i = 0; i < _tokens.Count; i++)
        {
            // Assign proxy
            if (_proxyList.Any())
            {
                var proxyIndex = i % _proxyList.Count;
                _tokens[i].Proxy = _proxyList[proxyIndex];
            }
            
            // Assign username from cache
            if (_tokenCache.TryGetValue(_tokens[i].Token, out var username))
            {
                _tokens[i].Username = username;
            }
        }
    }

    private static void LoadState()
    {
        if (File.Exists(StatePath))
        {
            var json = File.ReadAllText(StatePath);
            _state = JsonSerializer.Deserialize<TokenState>(json);
        }

        _state ??= new TokenState
        {
            CurrentIndex = 0,
            History = new Dictionary<string, BotExecutionState>()
        };
    }

    private static void SaveState()
    {
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StatePath, json);
    }

    public static (string token, string? proxy, string owner, string repo) GetCurrentToken()
    {
        if (!_tokens.Any())
        {
            throw new Exception("No tokens configured");
        }

        var current = _tokens[_state!.CurrentIndex];
        return (current.Token, current.Proxy, _owner, _repo);
    }
    
    // Helper untuk CollaboratorManager
    public static (List<TokenEntry> tokens, string owner, string repo) GetAllTokenEntries()
    {
        return (_tokens, _owner, _repo);
    }

    // Helper untuk CollaboratorManager
    public static Dictionary<string, string> GetUsernameCache()
    {
        return _tokenCache;
    }

    public static void SwitchToNextToken()
    {
        if (!_tokens.Any()) return;

        _state!.CurrentIndex = (_state.CurrentIndex + 1) % _tokens.Count;
        SaveState();

        var current = _tokens[_state.CurrentIndex];
        AnsiConsole.MarkupLine($"[yellow]Switched to token #{_state.CurrentIndex + 1} ({current.Username ?? "???"})[/]");
        if (!string.IsNullOrEmpty(current.Proxy))
        {
            AnsiConsole.MarkupLine($"[dim]Proxy: {current.Proxy}[/]");
        }
    }

    public static bool HandleRateLimitError(Exception ex)
    {
        if (ex.Message.Contains("rate limit") || ex.Message.Contains("403") || ex.Message.Contains("429"))
        {
            AnsiConsole.MarkupLine("[red]Rate limit detected![/]");
            
            if (_tokens.Count > 1)
            {
                SwitchToNextToken();
                return true;
            }
            else
            {
                AnsiConsole.MarkupLine("[red]No alternate tokens available[/]");
                return false;
            }
        }
        return false;
    }

    public static void SaveBotState(string botName, int step, Dictionary<string, string> capturedInputs)
    {
        _state!.History[botName] = new BotExecutionState
        {
            LastStep = step,
            CapturedInputs = capturedInputs,
            LastExecuted = DateTime.UtcNow,
            TokenIndex = _state.CurrentIndex
        };
        SaveState();
    }

    public static BotExecutionState? GetBotState(string botName)
    {
        return _state!.History.ContainsKey(botName) ? _state.History[botName] : null;
    }

    public static void ClearBotState(string botName)
    {
        if (_state!.History.ContainsKey(botName))
        {
            _state.History.Remove(botName);
            SaveState();
        }
    }

    public static HttpClient CreateHttpClient()
    {
        var (token, proxy, _, _) = GetCurrentToken();
        return CreateHttpClient(token, proxy);
    }
    
    public static HttpClient CreateHttpClient(TokenEntry entry)
    {
        return CreateHttpClient(entry.Token, entry.Proxy);
    }

    private static HttpClient CreateHttpClient(string token, string? proxy)
    {
        var handler = new HttpClientHandler();
        
        if (!string.IsNullOrEmpty(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/2.0");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        
        return client;
    }

    public static void ReloadProxies()
    {
        AnsiConsole.MarkupLine("[cyan]Reloading proxies from ProxySync...[/]");
        LoadProxyList();
        AssignProxiesAndUsernames(); 
        AnsiConsole.MarkupLine("[green]Proxies reloaded and assigned to tokens[/]");
    }
    
    public static string MaskToken(string token) 
    {
        return token.Length > 20 
            ? token[..10] + "..." + token[^7..] 
            : token;
    }

    public static void ShowStatus()
    {
        if (!_tokens.Any()) return;

        var table = new Table().Title("GitHub Tokens Status");
        table.AddColumn("Index");
        table.AddColumn("Token");
        table.AddColumn("Username"); 
        table.AddColumn("Proxy");
        table.AddColumn("Active");

        for (int i = 0; i < _tokens.Count; i++)
        {
            var token = _tokens[i];
            var isActive = i == _state!.CurrentIndex ? "[green]✓[/]" : "";
            var tokenDisplay = MaskToken(token.Token);
            
            var proxyDisplay = token.Proxy ?? "[yellow]no proxy[/]";
            if (!string.IsNullOrEmpty(token.Proxy) && token.Proxy.Length > 40)
            {
                proxyDisplay = token.Proxy[..37] + "...";
            }
            
            table.AddRow(
                (i + 1).ToString(),
                tokenDisplay,
                token.Username ?? "[grey]???[/]", 
                proxyDisplay,
                isActive
            );
        }

        AnsiConsole.Write(table);

        if (_proxyList.Any())
        {
            AnsiConsole.MarkupLine($"\n[dim]Total proxies from ProxySync: {_proxyList.Count}[/]");
        }

        if (_state!.History.Any())
        {
            AnsiConsole.MarkupLine("\n[cyan]Execution History:[/]");
            var historyTable = new Table();
            historyTable.AddColumn("Bot");
            historyTable.AddColumn("Last Step");
            historyTable.AddColumn("Token Used");
            historyTable.AddColumn("Last Run");

            foreach (var (botName, state) in _state.History.OrderByDescending(x => x.Value.LastExecuted))
            {
                historyTable.AddRow(
                    botName,
                    state.LastStep.ToString(),
                    $"#{state.TokenIndex + 1}",
                    state.LastExecuted.ToString("MM-dd HH:mm")
                );
            }

            AnsiConsole.Write(historyTable);
        }
    }
}

public class TokenEntry
{
    public string Token { get; set; } = string.Empty;
    public string? Proxy { get; set; }
    public string? Username { get; set; } 
}

public class TokenState
{
    [JsonPropertyName("current_index")]
    public int CurrentIndex { get; set; }

    [JsonPropertyName("history")]
    public Dictionary<string, BotExecutionState> History { get; set; } = new();
}

public class BotExecutionState
{
    [JsonPropertyName("last_step")]
    public int LastStep { get; set; }

    [JsonPropertyName("captured_inputs")]
    public Dictionary<string, string> CapturedInputs { get; set; } = new();

    [JsonPropertyName("last_executed")]
    public DateTime LastExecuted { get; set; }

    [JsonPropertyName("token_index")]
    public int TokenIndex { get; set; }
}

public class GitHubConfig
{
    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    [JsonPropertyName("repo")]
    public string? Repo { get; set; }
}
