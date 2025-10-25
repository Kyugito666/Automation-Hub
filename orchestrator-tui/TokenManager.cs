using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace OrchestratorV2;

public class TokenEntry
{
    public string Token { get; set; } = "";
    public string? Proxy { get; set; }
    public string? Username { get; set; }
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
}

public class TokenState
{
    [JsonPropertyName("current_index")]
    public int CurrentIndex { get; set; } = 0;

    [JsonPropertyName("active_codespace_name")]
    public string? ActiveCodespaceName { get; set; }
}

public static class TokenManager
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string TokensPath = Path.Combine(ProjectRoot, "config", "github_tokens.txt");
    private static readonly string ProxyPath = Path.Combine(ProjectRoot, "config", "proxy.txt");
    private static readonly string StatePath = Path.Combine(ProjectRoot, ".token-state.json");
    private static readonly string CachePath = Path.Combine(ProjectRoot, ".token-cache.json");

    private static List<TokenEntry> _tokens = new();
    private static List<string> _proxies = new();
    private static TokenState _state = new();
    private static Dictionary<string, string> _cache = new();

    private static string GetProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "config")) &&
                File.Exists(Path.Combine(current.FullName, ".gitignore")))
                return current.FullName;
            current = current.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    public static void Initialize()
    {
        LoadTokens();
        LoadProxies();
        LoadState();
        LoadCache();
        AssignProxies();
    }

    private static void LoadTokens()
    {
        if (!File.Exists(TokensPath))
        {
            AnsiConsole.MarkupLine($"[red]FATAL: {TokensPath} not found[/]");
            Environment.Exit(1);
        }

        var lines = File.ReadAllLines(TokensPath);
        if (lines.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]FATAL: Invalid format (need 3 lines)[/]");
            Environment.Exit(1);
        }

        var owner = lines[0].Trim();
        var repo = lines[1].Trim();
        var tokens = lines[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        _tokens = tokens.Select(t => new TokenEntry
        {
            Token = t,
            Owner = owner,
            Repo = repo
        }).ToList();

        AnsiConsole.MarkupLine($"[green]✓ {_tokens.Count} tokens loaded ({owner}/{repo})[/]");
    }

    private static void LoadProxies()
    {
        if (!File.Exists(ProxyPath))
        {
            AnsiConsole.MarkupLine("[yellow]⚠️  No proxy.txt found. Running without proxy.[/]");
            return;
        }

        _proxies = File.ReadAllLines(ProxyPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
            .ToList();

        AnsiConsole.MarkupLine($"[green]✓ {_proxies.Count} proxies loaded[/]");
    }

    private static void LoadState()
    {
        if (File.Exists(StatePath))
        {
            var json = File.ReadAllText(StatePath);
            _state = JsonSerializer.Deserialize<TokenState>(json) ?? new();
        }
    }

    private static void LoadCache()
    {
        if (File.Exists(CachePath))
        {
            var json = File.ReadAllText(CachePath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
    }

    private static void AssignProxies()
    {
        if (!_proxies.Any()) return;

        for (int i = 0; i < _tokens.Count; i++)
        {
            _tokens[i].Proxy = _proxies[i % _proxies.Count];
            if (_cache.TryGetValue(_tokens[i].Token, out var username))
                _tokens[i].Username = username;
        }
    }

    public static void SaveState() =>
        File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));

    public static void SaveCache() =>
        File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));

    public static TokenEntry GetCurrentToken()
    {
        if (_state.CurrentIndex >= _tokens.Count) _state.CurrentIndex = 0;
        return _tokens[_state.CurrentIndex];
    }

    public static void SwitchToNextToken()
    {
        _state.CurrentIndex = (_state.CurrentIndex + 1) % _tokens.Count;
        _state.ActiveCodespaceName = null;
        SaveState();
        var current = GetCurrentToken();
        AnsiConsole.MarkupLine($"[yellow]🔁 Switched to token #{_state.CurrentIndex + 1}: @{current.Username ?? "???"}[/]");
    }

    public static List<TokenEntry> GetAllTokens() => _tokens;
    public static TokenState GetState() => _state;
    public static Dictionary<string, string> GetCache() => _cache;

    public static HttpClient CreateHttpClient(TokenEntry token)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(token.Proxy))
        {
            handler.Proxy = new WebProxy(token.Proxy);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/2.0");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    public static string MaskToken(string token) =>
        token.Length > 20 ? $"{token[..10]}...{token[^7..]}" : token;
}
