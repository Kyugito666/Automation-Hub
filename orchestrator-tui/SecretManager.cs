using System.Net.Http.Json;
using System.Security.Cryptography; // Perlu untuk Sodium replacement
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
    private static readonly string ApiListPath = Path.Combine(ProjectRoot, "config", "apilist.txt");
    private static readonly string PrivateKeyDir = Path.Combine(ProjectRoot, "bots", "privatekey");
    private static readonly string TokenDir = Path.Combine(ProjectRoot, "bots", "token");

    private static string GetProjectRoot()
    {
        // ... (Fungsi ini tetap sama) ...
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

        // Fallback jika tidak ketemu .gitignore + config/
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }


    public static async Task SetSecretsForAll()
    {
        AnsiConsole.MarkupLine("[cyan]--- Setting Secrets for All Accounts ---[/]");

        var privateKeys = CollectPrivateKeys();
        var tokens = CollectTokens();
        var apiList = LoadApiList();

        if (!privateKeys.Any() && !tokens.Any() && !apiList.Any())
        {
            AnsiConsole.MarkupLine("[yellow]⚠️  No secrets to set (all directories empty).[/]");
            return;
        }

        // --- PERBAIKAN DI SINI ---
        // Salah: var allTokens = TokenManager.GetAllTokens();
        // Benar:
        var allTokens = TokenManager.GetAllTokenEntries(); // <--- FIX NAMA METHOD
        // --- AKHIR PERBAIKAN ---

        if (!allTokens.Any())
        {
             AnsiConsole.MarkupLine("[red]❌ Tidak ada token utama yang terkonfigurasi.[/]");
             return;
        }

        var owner = allTokens.First().Owner;
        var repo = allTokens.First().Repo;

        int successCount = 0, failCount = 0;

        foreach (var tokenEntry in allTokens)
        {
             if (string.IsNullOrEmpty(tokenEntry.Username))
            {
                AnsiConsole.MarkupLine($"[yellow]⏭️  Skipping token without username[/]");
                continue;
            }

            AnsiConsole.MarkupLine($"\n[cyan]Processing @{tokenEntry.Username}...[/]");

            try
            {
                using var client = TokenManager.CreateHttpClient(tokenEntry);

                // Get repo ID
                var repoUrl = $"https://api.github.com/repos/{owner}/{repo}";
                var repoResponse = await client.GetAsync(repoUrl);
                if (!repoResponse.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Failed to get repo ID: {repoResponse.StatusCode}[/]");
                    failCount++;
                    continue;
                }

                var repoJson = await repoResponse.Content.ReadFromJsonAsync<JsonElement>();
                var repoId = repoJson.GetProperty("id").GetInt32();

                // Get public key
                var pubKeyResponse = await client.GetAsync("https://api.github.com/user/codespaces/secrets/public-key");
                if (!pubKeyResponse.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Failed to get public key: {pubKeyResponse.StatusCode}[/]");
                    failCount++;
                    continue;
                }

                var pubKey = await pubKeyResponse.Content.ReadFromJsonAsync<PublicKeyResponse>();
                if (pubKey == null)
                {
                    AnsiConsole.MarkupLine("[red]✗ Invalid public key response[/]");
                    failCount++;
                    continue;
                }

                int setCount = 0;

                // Set PRIVATEKEY secrets
                if (privateKeys.Any())
                {
                    var privateKeyValue = string.Join("\n", privateKeys);
                    if (await SetSecret(client, "PRIVATEKEY", privateKeyValue, pubKey, repoId))
                        setCount++;
                }

                // Set TOKEN secrets
                if (tokens.Any())
                {
                    var tokenValue = string.Join("\n", tokens);
                    if (await SetSecret(client, "TOKEN", tokenValue, pubKey, repoId))
                        setCount++;
                }

                // Set APILIST
                if (apiList.Any())
                {
                    var apiListValue = string.Join("\n", apiList);
                    if (await SetSecret(client, "APILIST", apiListValue, pubKey, repoId))
                        setCount++;
                }

                if (setCount > 0)
                {
                    AnsiConsole.MarkupLine($"[green]✓ Set {setCount} secrets for @{tokenEntry.Username}[/]");
                    successCount++;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠️  No secrets set for @{tokenEntry.Username}[/]");
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Error for @{tokenEntry.Username}: {ex.Message}[/]");
                failCount++;
            }

            await Task.Delay(1000); // Delay antar token
        }

        AnsiConsole.MarkupLine($"\n[green]✓ Done.[/] Success: {successCount}, Failed: {failCount}");
    }

     private static async Task<bool> SetSecret(
        HttpClient client,
        string secretName,
        string secretValue,
        PublicKeyResponse pubKey,
        int repoId)
    {
        // ... (Fungsi ini tetap sama) ...
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

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                AnsiConsole.MarkupLine($"   [green]✓[/] {secretName}");
                return true;
            }
            else
            {
                AnsiConsole.MarkupLine($"   [red]✗[/] {secretName}: {response.StatusCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"   [red]✗[/] {secretName}: {ex.Message}");
            return false;
        }
    }

    private static List<string> CollectPrivateKeys()
    {
        // ... (Fungsi ini tetap sama) ...
        if (!Directory.Exists(PrivateKeyDir))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠️  {PrivateKeyDir} not found. Skipping privatekey collection.[/]");
            return new List<string>();
        }

        var keys = new List<string>();
        try
        {
             var dirs = Directory.GetDirectories(PrivateKeyDir);
             foreach (var dir in dirs)
             {
                 var accountsFile = Path.Combine(dir, "accounts.txt");
                 if (File.Exists(accountsFile))
                 {
                     var lines = File.ReadAllLines(accountsFile)
                         .Select(l => l.Trim())
                         .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));
                     keys.AddRange(lines);
                 }
             }
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error collecting private keys: {ex.Message}[/]"); }

        if (keys.Any()) AnsiConsole.MarkupLine($"[green]✓ Collected {keys.Count} private keys[/]");
        return keys;
    }

    private static List<string> CollectTokens()
    {
        // ... (Fungsi ini tetap sama) ...
         if (!Directory.Exists(TokenDir))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠️  {TokenDir} not found. Skipping token collection.[/]");
            return new List<string>();
        }

        var tokens = new List<string>();
        try
        {
             var dirs = Directory.GetDirectories(TokenDir);
             foreach (var dir in dirs)
             {
                 var dataFile = Path.Combine(dir, "data.txt");
                 if (File.Exists(dataFile))
                 {
                     var lines = File.ReadAllLines(dataFile)
                         .Select(l => l.Trim())
                         .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));
                     tokens.AddRange(lines);
                 }
             }
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error collecting tokens: {ex.Message}[/]"); }

        if (tokens.Any()) AnsiConsole.MarkupLine($"[green]✓ Collected {tokens.Count} tokens[/]");
        return tokens;
    }

    private static List<string> LoadApiList()
    {
        // ... (Fungsi ini tetap sama) ...
        if (!File.Exists(ApiListPath))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠️  {ApiListPath} not found. Skipping APILIST.[/]");
            return new List<string>();
        }
        try
        {
             var lines = File.ReadAllLines(ApiListPath)
                 .Select(l => l.Trim())
                 .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                 .ToList();

             if (lines.Any()) AnsiConsole.MarkupLine($"[green]✓ Loaded {lines.Count} API URLs[/]");
             return lines;
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error loading API list: {ex.Message}[/]");
            return new List<string>();
        }
    }

    private static string EncryptSecret(string publicKeyBase64, string secretValue)
    {
        // ... (Fungsi ini tetap sama - MASIH PLACEHOLDER) ...
        try
        {
            var keyBytes = Convert.FromBase64String(publicKeyBase64);
            var secretBytes = Encoding.UTF8.GetBytes(secretValue);

            // Placeholder (TIDAK AMAN!)
            using var aes = Aes.Create();
            aes.Key = SHA256.HashData(keyBytes).Take(32).ToArray(); // Gunakan hash (sedikit lebih baik tapi tetap salah)
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                cs.Write(secretBytes, 0, secretBytes.Length);
                cs.FlushFinalBlock(); // Penting!
            }
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]PLACEHOLDER Encryption failed: {ex.Message}. Using Base64...[/]");
             return Convert.ToBase64String(Encoding.UTF8.GetBytes(secretValue));
        }
    }
}
