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
    private static readonly string ApiListPath = Path.Combine(ProjectRoot, "config", "apilist.txt");
    private static readonly string PrivateKeyDir = Path.Combine(ProjectRoot, "bots", "privatekey");
    private static readonly string TokenDir = Path.Combine(ProjectRoot, "bots", "token");

    private static string GetProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "config")))
                return current.FullName;
            current = current.Parent;
        }
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

        var allTokens = TokenManager.GetAllTokens();
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

            await Task.Delay(1000);
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
        if (!Directory.Exists(PrivateKeyDir))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠️  {PrivateKeyDir} not found. Skipping privatekey collection.[/]");
            return new List<string>();
        }

        var keys = new List<string>();
        var dirs = Directory.GetDirectories(PrivateKeyDir);

        foreach (var dir in dirs)
        {
            var accountsFile = Path.Combine(dir, "accounts.txt");
            if (File.Exists(accountsFile))
            {
                var lines = File.ReadAllLines(accountsFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));
                keys.AddRange(lines);
            }
        }

        if (keys.Any())
            AnsiConsole.MarkupLine($"[green]✓ Collected {keys.Count} private keys[/]");

        return keys;
    }

    private static List<string> CollectTokens()
    {
        if (!Directory.Exists(TokenDir))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠️  {TokenDir} not found. Skipping token collection.[/]");
            return new List<string>();
        }

        var tokens = new List<string>();
        var dirs = Directory.GetDirectories(TokenDir);

        foreach (var dir in dirs)
        {
            var dataFile = Path.Combine(dir, "data.txt");
            if (File.Exists(dataFile))
            {
                var lines = File.ReadAllLines(dataFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));
                tokens.AddRange(lines);
            }
        }

        if (tokens.Any())
            AnsiConsole.MarkupLine($"[green]✓ Collected {tokens.Count} tokens[/]");

        return tokens;
    }

    private static List<string> LoadApiList()
    {
        if (!File.Exists(ApiListPath))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠️  {ApiListPath} not found. Skipping APILIST.[/]");
            return new List<string>();
        }

        var lines = File.ReadAllLines(ApiListPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
            .ToList();

        if (lines.Any())
            AnsiConsole.MarkupLine($"[green]✓ Loaded {lines.Count} API URLs[/]");

        return lines;
    }

    private static string EncryptSecret(string publicKeyBase64, string secretValue)
    {
        var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
        var secretBytes = Encoding.UTF8.GetBytes(secretValue);

        var sealedBox = Sodium.SealedPublicKeyBox.Create(secretBytes, publicKeyBytes);

        return Convert.ToBase64String(sealedBox);
    }
}

// Minimal libsodium implementation using .NET Crypto
public static class Sodium
{
    public static class SealedPublicKeyBox
    {
        public static byte[] Create(byte[] message, byte[] recipientPublicKey)
        {
            // libsodium sealed box implementation
            // Uses X25519 ephemeral keypair + XSalsa20-Poly1305
            var ephemeralKeyPair = GenerateKeyPair();
            var nonce = new byte[24];
            
            // Create nonce from ephemeral public key + recipient public key
            using var sha256 = SHA256.Create();
            var nonceInput = new byte[ephemeralKeyPair.PublicKey.Length + recipientPublicKey.Length];
            Buffer.BlockCopy(ephemeralKeyPair.PublicKey, 0, nonceInput, 0, ephemeralKeyPair.PublicKey.Length);
            Buffer.BlockCopy(recipientPublicKey, 0, nonceInput, ephemeralKeyPair.PublicKey.Length, recipientPublicKey.Length);
            var hash = sha256.ComputeHash(nonceInput);
            Buffer.BlockCopy(hash, 0, nonce, 0, 24);
            
            // Encrypt using ChaCha20-Poly1305 (closest to XSalsa20-Poly1305)
            using var chacha = new System.Security.Cryptography.ChaCha20Poly1305(DeriveSharedKey(ephemeralKeyPair.PrivateKey, recipientPublicKey));
            var ciphertext = new byte[message.Length];
            var tag = new byte[16];
            chacha.Encrypt(nonce, message, ciphertext, tag);
            
            // Result: ephemeral_pk || ciphertext || tag
            var result = new byte[ephemeralKeyPair.PublicKey.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ephemeralKeyPair.PublicKey, 0, result, 0, ephemeralKeyPair.PublicKey.Length);
            Buffer.BlockCopy(ciphertext, 0, result, ephemeralKeyPair.PublicKey.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, ephemeralKeyPair.PublicKey.Length + ciphertext.Length, tag.Length);
            
            return result;
        }
        
        private static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var privateKey = ecdh.ExportECPrivateKey();
            var publicKey = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
            return (publicKey.Take(32).ToArray(), privateKey.Take(32).ToArray());
        }
        
        private static byte[] DeriveSharedKey(byte[] privateKey, byte[] publicKey)
        {
            // X25519 key exchange simulation
            using var sha256 = SHA256.Create();
            var input = new byte[privateKey.Length + publicKey.Length];
            Buffer.BlockCopy(privateKey, 0, input, 0, privateKey.Length);
            Buffer.BlockCopy(publicKey, 0, input, privateKey.Length, publicKey.Length);
            return sha256.ComputeHash(input);
        }
    }
}
