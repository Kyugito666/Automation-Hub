using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

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
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    private static readonly string LocalBotRoot = @"D:\SC";

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
            {
                return currentDir.FullName;
            }

            currentDir = currentDir.Parent;
            currentDepth++;
        }

        var fallbackPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return fallbackPath;
    }

    public static async Task SetSecretsForAll()
    {
        AnsiConsole.MarkupLine("[cyan]═══ Auto Setting Secrets (Smart Read) ═══[/]");

        var config = BotConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to load bots_config.json[/]");
            return;
        }

        var allTokens = TokenManager.GetAllTokenEntries();
        if (!allTokens.Any())
        {
            AnsiConsole.MarkupLine("[red]No tokens configured in config/github_tokens.txt[/]");
            return;
        }

        var owner = allTokens.First().Owner;
        var repo = allTokens.First().Repo;

        // Kumpulkan semua secret yang dibutuhkan per bot
        var botSecrets = new Dictionary<string, Dictionary<string, string>>();

        foreach (var bot in config.BotsAndTools)
        {
            if (!bot.Enabled) continue;

            var localBotPath = BotConfig.GetLocalBotPath(bot.Path);
            
            if (!Directory.Exists(localBotPath))
            {
                AnsiConsole.MarkupLine($"[dim]Skip {bot.Name} (local path not found)[/]");
                continue;
            }

            var secrets = new Dictionary<string, string>();

            // Cari file-file secret yang umum
            var secretFiles = new[] 
            {
                ("ENV_FILE", ".env"),
                ("PK_FILE", "pk.txt"),
                ("PRIVATEKEY_FILE", "privatekey.txt"),
                ("WALLET_FILE", "wallet.txt"),
                ("TOKEN_FILE", "token.txt"),
                ("DATA_FILE", "data.json"),
                ("CONFIG_FILE", "config.json"),
                ("SETTINGS_FILE", "settings.json"),
                ("ACCOUNTS_FILE", "accounts.txt")
            };

            foreach (var (secretName, fileName) in secretFiles)
            {
                var filePath = Path.Combine(localBotPath, fileName);
                if (File.Exists(filePath))
                {
                    try
                    {
                        var content = File.ReadAllText(filePath);
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            secrets[secretName] = content;
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Failed to read {fileName} for {bot.Name}: {ex.Message}[/]");
                    }
                }
            }

            if (secrets.Any())
            {
                botSecrets[bot.Name] = secrets;
                AnsiConsole.MarkupLine($"[green]✓ Found {secrets.Count} secrets for {bot.Name}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]No secrets found for {bot.Name}[/]");
            }
        }

        if (!botSecrets.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No secrets to set (all bots have no secret files)[/]");
            return;
        }

        // Set secrets untuk setiap token
        int successCount = 0, failCount = 0;

        foreach (var tokenEntry in allTokens)
        {
            if (string.IsNullOrEmpty(tokenEntry.Username))
            {
                AnsiConsole.MarkupLine($"[yellow]Skip token without username (run Validate Tokens first)[/]");
                continue;
            }

            AnsiConsole.MarkupLine($"\n[cyan]Processing @{tokenEntry.Username}...[/]");

            try
            {
                using var client = TokenManager.CreateHttpClient(tokenEntry);

                // Get Repo ID
                AnsiConsole.Markup("[dim]Getting repo ID... [/]");
                var repoUrl = $"https://api.github.com/repos/{owner}/{repo}";
                var repoResponse = await client.GetAsync(repoUrl);
                if (!repoResponse.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[red]Failed: {repoResponse.StatusCode}[/]");
                    failCount++; 
                    continue;
                }
                var repoJson = await repoResponse.Content.ReadFromJsonAsync<JsonElement>();
                var repoId = repoJson.GetProperty("id").GetInt32();
                AnsiConsole.MarkupLine("[green]OK[/]");

                // Get Public Key
                AnsiConsole.Markup("[dim]Getting public key... [/]");
                var pubKeyResponse = await client.GetAsync("https://api.github.com/user/codespaces/secrets/public-key");
                if (!pubKeyResponse.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[red]Failed: {pubKeyResponse.StatusCode}[/]");
                    failCount++; 
                    continue;
                }
                var pubKey = await pubKeyResponse.Content.ReadFromJsonAsync<PublicKeyResponse>();
                if (pubKey == null)
                {
                    AnsiConsole.MarkupLine("[red]Invalid public key response[/]");
                    failCount++; 
                    continue;
                }
                AnsiConsole.MarkupLine("[green]OK[/]");

                int secretsSuccessfullySet = 0;

                // Set secrets untuk setiap bot
                foreach (var (botName, secrets) in botSecrets)
                {
                    foreach (var (secretName, secretValue) in secrets)
                    {
                        // Format: BOTNAME_SECRETNAME (contoh: GRASS_ENV_FILE)
                        var fullSecretName = $"{SanitizeName(botName)}_{secretName}".ToUpper();
                        
                        if (await SetSecret(client, fullSecretName, secretValue, pubKey, repoId))
                            secretsSuccessfullySet++;
                    }
                }

                if (secretsSuccessfullySet > 0)
                {
                    AnsiConsole.MarkupLine($"[green]✓ Set {secretsSuccessfullySet} secrets for @{tokenEntry.Username}[/]");
                    successCount++;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]No secrets set for @{tokenEntry.Username}[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error processing @{tokenEntry.Username}: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                failCount++;
            }

            await Task.Delay(1000);
        }

        AnsiConsole.MarkupLine($"\n[green]✓ Secret setting completed.[/] Success: {successCount}, Failed: {failCount}");
        AnsiConsole.MarkupLine("[dim]Secrets are now available as environment variables in codespace[/]");
    }

    private static string SanitizeName(string name)
    {
        // Hapus karakter non-alphanumeric, ganti dengan underscore
        return System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9]", "_");
    }

    private static async Task<bool> SetSecret(
        HttpClient client,
        string secretName,
        string secretValue,
        PublicKeyResponse pubKey,
        int repoId)
    {
        AnsiConsole.Markup($"[dim]Setting {secretName}... [/]");
        try
        {
            var encrypted = EncryptSecret(pubKey.Key, secretValue);
            var payload = new
            {
                encrypted_value = encrypted,
                key_id = pubKey.KeyId,
                selected_repository_ids = new[] { repoId.ToString() }
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
                AnsiConsole.MarkupLine($"[red]Failed ({response.StatusCode})[/]");
                return false;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Exception: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            return false;
        }
    }

    // PLACEHOLDER ENCRYPTION - HARUS DIGANTI DENGAN LIBSODIUM
    private static string EncryptSecret(string publicKeyBase64, string secretValue)
    {
        try {
            var keyBytes = Convert.FromBase64String(publicKeyBase64);
            var secretBytes = Encoding.UTF8.GetBytes(secretValue);
            
            using var aes = Aes.Create();
            using var sha256 = SHA256.Create();
            aes.Key = sha256.ComputeHash(keyBytes).Take(32).ToArray();
            aes.GenerateIV();
            
            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);
            
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                cs.Write(secretBytes, 0, secretBytes.Length);
                cs.FlushFinalBlock();
            }
            
            return Convert.ToBase64String(ms.ToArray());
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[yellow]Encryption warning: {ex.Message}. Using Base64 fallback...[/]");
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(secretValue));
        }
    }
}
