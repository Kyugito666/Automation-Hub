using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public class BotConfig
{
    // Path relatif dari executable TUI
    private static readonly string ConfigFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config", "bots_config.json"));

    [JsonPropertyName("bots_and_tools")]
    public List<BotEntry> BotsAndTools { get; set; } = new();

    public static BotConfig? Load()
    {
        if (!File.Exists(ConfigFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: File konfig '{ConfigFile}' tidak ditemukan.[/]");
            return null;
        }
        try
        {
            var json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<BotConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
             AnsiConsole.MarkupLine($"[red]Error parsing {ConfigFile}: {ex.Message}[/]");
             return null;
        }
         catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Error reading {ConfigFile}: {ex.Message}[/]");
             return null;
        }
    }
}

public class BotEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("repo_url")]
    public string RepoUrl { get; set; } = string.Empty;
    
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    
    // Properti ini tidak lagi relevan karena eksekusi di remote
    // Tapi biarkan saja untuk kompatibilitas jika masih dipakai di Debug Local
    [JsonIgnore]
    public bool IsBot => Path.Contains("/privatekey/") || Path.Contains("/token/");
}
