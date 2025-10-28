using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Sodium;

namespace Orchestrator;

public class PublicKeyResponse
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "";
}

public static class SecretManager
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string LocalBotRoot = @"D:\SC";

    private static readonly string[] CommonSecretPatterns = new[]
    {
        "*.env",
        "pk.txt", "privatekey.txt", "private_key.txt",
        "token.txt", "tokens.txt", "token.json", "tokens.json",
        "data.txt", "data.json",
        "wallet.txt", "wallets.txt", "wallet.json",
        "accounts.txt", "accounts.json", "account.json",
        "cookies.txt", "cookie.txt",
        "bearer.txt", "bearer.json",
        "mnemonics.txt", "mnemonic.txt", "seed.txt",
        "auth.txt", "auth.json",
        "credentials.txt", "credentials.json"
    };

    private static string GetProjectRoot()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        int maxDepth = 10;
        int currentDepth = 0;

        while (currentDir != null && currentDepth < maxDepth)
        {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");

            if (Directory.Exists(configDir) && File.Exists(gitignore))
                return currentDir.FullName;

            currentDir = currentDir.Parent;
            currentDepth++;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    public static async Task SetSecretsForActiveToken()
    {
        AnsiConsole.MarkupLine("[cyan]═══ Set Secrets (SMART - Auto Detect) ═══[/]");

        var currentToken = TokenManager.GetCurrentToken();
        var state = TokenManager.GetState();

        if (string.IsNullOrEmpty(currentToken.Username))
        {
            AnsiConsole.MarkupLine("[red]Active token belum punya username![/]");
            AnsiConsole.MarkupLine("[yellow]Run Menu 2 -> Validate Tokens dulu.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]Active Token: #{state.CurrentIndex + 1} - @{currentToken.Username}[/]");
        AnsiConsole.MarkupLine($"[dim]Proxy: {TokenManager.MaskProxy(currentToken.Proxy)}[/]");

        var config = BotConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to load bots_config.json[/]");
            return;
        }

        var botSecretMappings = new Dictionary<string, List<SecretMapping>>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[yellow]Analyzing bots...[/]", async ctx =>
            {
                foreach (var bot in config.BotsAndTools)
                {
                    if (!bot.Enabled) continue;

                    ctx.Status($"[yellow]Analyzing {bot.Name}...[/]");
                    
                    var secretMappings = await AnalyzeBotSecrets(bot);
                    
                    if (secretMappings.Any())
                    {
                        botSecretMappings[bot.Name] = secretMappings;
                        AnsiConsole.MarkupLine($"[green]✓ {bot.Name}: Found {secretMappings.Count} secret file(s)[/]");
                        
                        foreach (var mapping in secretMappings)
                            AnsiConsole.MarkupLine($"[dim]   - {mapping.FileName} ({mapping.FileSize} bytes)[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[dim]○ {bot.Name}: No secrets found[/]");
                    }
                }
            });

        if (!botSecretMappings.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No secrets to set (no matching files found in D:\\SC)[/]");
            return;
        }

        int totalSecrets = botSecretMappings.Sum(kvp => kvp.Value.Count);
        
        AnsiConsole.MarkupLine($"\n[yellow]═══ SUMMARY ═══[/]");
        AnsiConsole.MarkupLine($"Target: [cyan]@{currentToken.Username}[/]");
        AnsiConsole.MarkupLine($"Bots: [cyan]{botSecretMappings.Count}[/]");
        AnsiConsole.MarkupLine($"Total Secrets: [cyan]{totalSecrets}[/]");
        
        if (!AnsiConsole.Confirm("\n[yellow]Proceed to set secrets?[/]", false))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled by user.[/]");
            return;
        }

        var owner = currentToken.Owner;
        var repo = currentToken.Repo;

        try
        {
            using var client = TokenManager.CreateHttpClient(currentToken);

            AnsiConsole.Markup("[dim]Getting repo ID... [/]");
            var repoUrl = $"https://api.github.com/repos/{owner}/{repo}";
            var repoResponse = await client.GetAsync(repoUrl);
            if (!repoResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Failed: {repoResponse.StatusCode}[/]");
                return;
            }
            var repoJson = await repoResponse.Content.ReadFromJsonAsync<JsonElement>();
            var repoId = repoJson.GetProperty("id").GetInt32();
            AnsiConsole.MarkupLine("[green]OK[/]");

            AnsiConsole.Markup("[dim]Getting public key... [/]");
            var pubKeyResponse = await client.GetAsync("https://api.github.com/user/codespaces/secrets/public-key");
            if (!pubKeyResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Failed: {pubKeyResponse.StatusCode}[/]");
                return;
            }
            var pubKey = await pubKeyResponse.Content.ReadFromJsonAsync<PublicKeyResponse>();
            if (pubKey == null)
            {
                AnsiConsole.MarkupLine("[red]Invalid public key response[/]");
                return;
            }
            AnsiConsole.MarkupLine("[green]OK[/]");

            int successCount = 0;
            int failCount = 0;

            foreach (var (botName, mappings) in botSecretMappings)
            {
                AnsiConsole.MarkupLine($"\n[cyan]Processing {botName}...[/]");
                
                foreach (var mapping in mappings)
                {
                    var secretName = $"{SanitizeName(botName)}_{SanitizeName(mapping.FileName)}".ToUpper();
                    
                    if (await SetSecret(client, secretName, mapping.Content, pubKey, repoId))
                        successCount++;
                    else
                        failCount++;
                    
                    await Task.Delay(500);
                }
            }

            AnsiConsole.MarkupLine($"\n[green]✓ Secret setting completed for @{currentToken.Username}[/]");
            AnsiConsole.MarkupLine($"   Success: [green]{successCount}[/], Failed: [red]{failCount}[/]");
            AnsiConsole.MarkupLine("[dim]Secrets will be available in codespace via auto-start.sh extraction[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            AnsiConsole.WriteException(ex);
        }
    }

    private static async Task<List<SecretMapping>> AnalyzeBotSecrets(BotEntry bot)
    {
        var mappings = new List<SecretMapping>();
        var localBotPath = BotConfig.GetLocalBotPath(bot.Path);
        
        if (!Directory.Exists(localBotPath))
            return mappings;

        var localSecretFiles = DetectSecretFilesInDirectory(localBotPath);
        
        foreach (var filePath in localSecretFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(filePath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    mappings.Add(new SecretMapping
                    {
                        FileName = Path.GetFileName(filePath),
                        Content = content,
                        FileSize = content.Length
                    });
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]   Warning: Failed to read {Path.GetFileName(filePath)}: {ex.Message}[/]");
            }
        }

        return mappings;
    }

    private static List<string> DetectSecretFilesInDirectory(string directory)
    {
        var foundFiles = new List<string>();

        try
        {
            foreach (var pattern in CommonSecretPatterns)
            {
                try
                {
                    var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                    foundFiles.AddRange(files);
                }
                catch { }
            }

            foundFiles = foundFiles.Distinct().Where(f => 
            {
                var fileName = Path.GetFileName(f).ToLowerInvariant();
                return !fileName.Contains("config") && !fileName.Contains("setting");
            }).ToList();
        }
        catch { }

        return foundFiles;
    }

    private static string SanitizeName(string name)
    {
        return System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9]", "_");
    }

    private static async Task<bool> SetSecret(
        HttpClient client,
        string secretName,
        string secretValue,
        PublicKeyResponse pubKey,
        int repoId)
    {
        AnsiConsole.Markup($"[dim]   Setting {secretName}... [/]");
        try
        {
            var encrypted = EncryptSecret(pubKey.Key, secretValue);
            var payload = new
            {
                encrypted_value = encrypted,
                key_id = pubKey.KeyId,
                selected_repository_ids = new[] { repoId }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            var response = await client.PutAsync(
                $"https://api.github.com/user/codespaces/secrets/{secretName}",
                content
            );

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine("[green]OK[/]");
                return true;
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]Failed ({response.StatusCode})[/]");
                if (!string.IsNullOrEmpty(errorBody))
                    AnsiConsole.MarkupLine($"[dim]   {errorBody.Split('\n').FirstOrDefault()}[/]");
                return false;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Err: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            return false;
        }
    }

    private static string EncryptSecret(string publicKeyBase64, string secretValue)
    {
        try
        {
            var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            var secretBytes = Encoding.UTF8.GetBytes(secretValue);
            
            var encryptedBytes = SealedPublicKeyBox.Create(secretBytes, publicKeyBytes);
            
            return Convert.ToBase64String(encryptedBytes);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Encryption error: {ex.Message}[/]");
            throw;
        }
    }

    private class SecretMapping
    {
        public string FileName { get; set; } = "";
        public string Content { get; set; } = "";
        public int FileSize { get; set; }
    }
}
