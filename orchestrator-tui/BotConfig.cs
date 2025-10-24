using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public class BotConfig
{
    private const string ConfigFile = "../config/bots_config.json";

    [JsonPropertyName("bots_and_tools")]
    public List<BotEntry> BotsAndTools { get; set; } = new();

    public static BotConfig? Load()
    {
        if (!File.Exists(ConfigFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: File konfig '{ConfigFile}' tidak ditemukan.[/]");
            return null;
        }
        var json = File.ReadAllText(ConfigFile);
        return JsonSerializer.Deserialize<BotConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
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
    
    [JsonIgnore]
    public bool IsBot => Path.Contains("/privatekey/") || Path.Contains("/token/");
}
