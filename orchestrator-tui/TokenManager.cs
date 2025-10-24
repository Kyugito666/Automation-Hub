using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public class TokenManager
{
    private static readonly string ConfigPath = "../config/github_tokens.json";
    private static readonly string StatePath = "../.token-state.json";
    private static TokenConfig? _config;
    private static TokenState? _state;

    public static void Initialize()
    {
        LoadConfig();
        LoadState();
    }

    private static void LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR: {ConfigPath} tidak ditemukan![/]");
            AnsiConsole.MarkupLine("[yellow]Buat file dengan format:[/]");
            AnsiConsole.MarkupLine(@"[dim]{
  ""tokens"": [
    {""token"": ""ghp_xxx"", ""proxy"": ""http://proxy1:port""},
    {""token"": ""ghp_yyy"", ""proxy"": ""http://proxy2:port""}
  ],
  ""owner"": ""username"",
  ""repo"": ""automation-hub""
}[/]");
            _config = new TokenConfig();
            return;
        }

        var json = File.ReadAllText(ConfigPath);
        _config = JsonSerializer.Deserialize<TokenConfig>(json) ?? new TokenConfig();
        
        AnsiConsole.MarkupLine($"[green]Loaded {_config.Tokens.Count} GitHub tokens[/]");
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
        if (_config == null || _config.Tokens.Count == 0)
        {
            throw new Exception("No tokens configured");
        }

        var current = _config.Tokens[_state!.CurrentIndex];
        return (current.Token, current.Proxy, _config.Owner, _config.Repo);
    }

    public static void SwitchToNextToken()
    {
        if (_config == null || _config.Tokens.Count == 0) return;

        _state!.CurrentIndex = (_state.CurrentIndex + 1) % _config.Tokens.Count;
        SaveState();

        var current = _config.Tokens[_state.CurrentIndex];
        AnsiConsole.MarkupLine($"[yellow]Switched to token #{_state.CurrentIndex + 1}[/]");
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
            
            if (_config!.Tokens.Count > 1)
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

    public static void ShowStatus()
    {
        if (_config == null) return;

        var table = new Table().Title("GitHub Tokens Status");
        table.AddColumn("Index");
        table.AddColumn("Token");
        table.AddColumn("Proxy");
        table.AddColumn("Active");

        for (int i = 0; i < _config.Tokens.Count; i++)
        {
            var token = _config.Tokens[i];
            var isActive = i == _state!.CurrentIndex ? "[green]✓[/]" : "";
            var tokenDisplay = token.Token.Length > 20 
                ? token.Token[..10] + "..." + token.Token[^7..] 
                : token.Token;
            
            table.AddRow(
                (i + 1).ToString(),
                tokenDisplay,
                token.Proxy ?? "-",
                isActive
            );
        }

        AnsiConsole.Write(table);

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

public class TokenConfig
{
    [JsonPropertyName("tokens")]
    public List<TokenEntry> Tokens { get; set; } = new();

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;
}

public class TokenEntry
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("proxy")]
    public string? Proxy { get; set; }
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
