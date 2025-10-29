using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public class BotConfig
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigFile = Path.Combine(ProjectRoot, "config", "bots_config.json");
    private static readonly string LocalBotRoot = @"D:\SC\MyProject\SC";

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
    
    public static string GetLocalBotPath(string configPath)
    {
        var lastSegment = configPath.Split('/', '\\').Last();
        
        string targetFolder;
        if (configPath.Contains("privatekey", StringComparison.OrdinalIgnoreCase))
        {
            targetFolder = Path.Combine(LocalBotRoot, "PrivateKey");
        }
        else if (configPath.Contains("token", StringComparison.OrdinalIgnoreCase))
        {
            targetFolder = Path.Combine(LocalBotRoot, "Token");
        }
        else
        {
            targetFolder = LocalBotRoot;
        }
        
        var localDir = new DirectoryInfo(targetFolder);
        if (!localDir.Exists)
        {
            AnsiConsole.MarkupLine($"[red]ERROR: Folder tidak ditemukan: {targetFolder}[/]");
            return Path.Combine(targetFolder, lastSegment);
        }
        
        var matchingDir = localDir.GetDirectories()
            .FirstOrDefault(d => d.Name.Equals(lastSegment, StringComparison.OrdinalIgnoreCase));
        
        if (matchingDir != null)
        {
            return matchingDir.FullName;
        }
        
        AnsiConsole.MarkupLine($"[yellow]WARN: Folder '{lastSegment}' tidak ditemukan di {targetFolder}[/]");
        return Path.Combine(targetFolder, lastSegment);
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
