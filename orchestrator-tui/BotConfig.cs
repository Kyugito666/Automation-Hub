using System.Text.Json.Serialization;

namespace Orchestrator;

public class BotConfig
{
    [JsonPropertyName("bots_and_tools")]
    public List<BotEntry> BotsAndTools { get; set; } = new();
}

public class BotEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("repo_url")]
    public string RepoUrl { get; set; } = string.Empty;
}
