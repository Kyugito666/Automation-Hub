using System.Net.Http.Json;
using System.Security.Cryptography; // Perlu untuk Sodium replacement
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

// Kelas PublicKeyResponse untuk deserialisasi JSON
public class PublicKeyResponse
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "";
}

public static class SecretManager
{
    // Path relatif dari executable TUI ke root project
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ApiListPath = Path.Combine(ProjectRoot, "config", "apilist.txt");
    private static readonly string PrivateKeyDir = Path.Combine(ProjectRoot, "bots", "privatekey");
    private static readonly string TokenDir = Path.Combine(ProjectRoot, "bots", "token");

    // Helper untuk mencari root project (cari folder 'config' dan '.gitignore')
    private static string GetProjectRoot()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        int maxDepth = 10; int currentDepth = 0; // Batasi pencarian ke atas

        while (currentDir != null && currentDepth < maxDepth)
        {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");

            if (Directory.Exists(configDir) && File.Exists(gitignore))
            {
                return currentDir.FullName; // Ditemukan!
            }

            currentDir = currentDir.Parent;
            currentDepth++;
        }

        // Fallback jika tidak ketemu
        var fallbackPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        AnsiConsole.MarkupLine($"[yellow]Warning: Tidak bisa auto-detect project root. Menggunakan fallback: {fallbackPath}[/]");
        return fallbackPath;
    }


    public static async Task SetSecretsForAll()
    {
        AnsiConsole.MarkupLine("[cyan]--- Setting Secrets for All Accounts ---[/]");
        AnsiConsole.MarkupLine("[bold red]PERINGATAN: Logika enkripsi di fitur ini SALAH. Fitur ini TIDAK AKAN berfungsi.[/]");
        AnsiConsole.MarkupLine("[red]Fungsi 'EncryptSecret' perlu di-rewrite total menggunakan library LibSodium (misal: NSec.Cryptography).[/]");


        // Kumpulkan data rahasia dari folder bot
        var privateKeys = CollectPrivateKeys();
        var tokens = CollectTokens();
        var apiList = LoadApiList();

        if (!privateKeys.Any() && !tokens.Any() && !apiList.Any())
        {
            AnsiConsole.MarkupLine("[yellow]⚠️  No secrets to set (all source files/directories empty).[/]");
            return;
        }

        // --- PERBAIKAN COMPILE ERROR DI SINI ---
        // Ganti nama method GetAllTokens -> GetAllTokenEntries
        var allTokens = TokenManager.GetAllTokenEntries();
        // --- AKHIR PERBAIKAN ---

        if (!allTokens.Any())
        {
             AnsiConsole.MarkupLine("[red]❌ Tidak ada token utama yang terkonfigurasi di config/github_tokens.txt.[/]");
             return;
        }

        // Ambil info owner/repo dari token pertama (asumsi semua token untuk repo yang sama)
        var owner = allTokens.First().Owner;
        var repo = allTokens.First().Repo;

        int successCount = 0, failCount = 0;

        // Loop untuk setiap token PAT
        foreach (var tokenEntry in allTokens)
        {
             if (string.IsNullOrEmpty(tokenEntry.Username))
            {
                AnsiConsole.MarkupLine($"[yellow]⏭️  Skipping token without username (Run Validate Tokens first)[/]");
                continue; // Lewati jika username belum divalidasi
            }

            AnsiConsole.MarkupLine($"\n[cyan]Processing @{tokenEntry.Username}...[/]");

            try
            {
                // Gunakan HttpClient dengan proxy yang sesuai untuk token ini
                using var client = TokenManager.CreateHttpClient(tokenEntry);

                // 1. Dapatkan Repo ID
                AnsiConsole.Markup("[dim]   Getting repo ID... [/]");
                var repoUrl = $"https://api.github.com/repos/{owner}/{repo}";
                var repoResponse = await client.GetAsync(repoUrl);
                if (!repoResponse.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Failed: {repoResponse.StatusCode}[/]");
                    failCount++; continue; // Lanjut ke token berikutnya
                }
                var repoJson = await repoResponse.Content.ReadFromJsonAsync<JsonElement>();
                var repoId = repoJson.GetProperty("id").GetInt32();
                AnsiConsole.MarkupLine("[green]OK[/]");

                // 2. Dapatkan Public Key Codespace User
                AnsiConsole.Markup("[dim]   Getting public key... [/]");
                var pubKeyResponse = await client.GetAsync("https://api.github.com/user/codespaces/secrets/public-key");
                if (!pubKeyResponse.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Failed: {pubKeyResponse.StatusCode}[/]");
                    failCount++; continue;
                }
                var pubKey = await pubKeyResponse.Content.ReadFromJsonAsync<PublicKeyResponse>();
                if (pubKey == null)
                {
                    AnsiConsole.MarkupLine("[red]✗ Invalid public key response[/]");
                    failCount++; continue;
                }
                 AnsiConsole.MarkupLine("[green]OK[/]");

                int secretsSuccessfullySet = 0; // Hitung secret yang berhasil di-set per token

                // 3. Set secret PRIVATEKEY jika ada
                if (privateKeys.Any())
                {
                    var privateKeyValue = string.Join("\n", privateKeys); // Gabungkan semua jadi 1 string
                    if (await SetSecret(client, "PRIVATEKEY", privateKeyValue, pubKey, repoId))
                        secretsSuccessfullySet++;
                }

                // 4. Set secret TOKEN jika ada
                if (tokens.Any())
                {
                    var tokenValue = string.Join("\n", tokens); // Gabungkan semua jadi 1 string
                    if (await SetSecret(client, "TOKEN", tokenValue, pubKey, repoId))
                        secretsSuccessfullySet++;
                }

                // 5. Set secret APILIST jika ada
                if (apiList.Any())
                {
                    var apiListValue = string.Join("\n", apiList); // Gabungkan semua jadi 1 string
                    if (await SetSecret(client, "APILIST", apiListValue, pubKey, repoId))
                        secretsSuccessfullySet++;
                }

                // Cek apakah ada secret yang berhasil di-set
                if (secretsSuccessfullySet > 0)
                {
                    AnsiConsole.MarkupLine($"[green]✓ Set {secretsSuccessfullySet} secrets for @{tokenEntry.Username}[/]");
                    successCount++;
                }
                else
                {
                    // Ini bisa terjadi jika semua source (privatekey/, token/, apilist.txt) kosong
                    AnsiConsole.MarkupLine($"[yellow]✓ No applicable secrets found/set for @{tokenEntry.Username}[/]");
                    // Jangan hitung sebagai fail jika memang tidak ada yang perlu di-set
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Error processing @{tokenEntry.Username}: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                failCount++;
            }

            await Task.Delay(1000); // Jeda antar token untuk menghindari rate limit
        }

        AnsiConsole.MarkupLine($"\n[green]✓ Secret setting process completed.[/] Accounts processed successfully: {successCount}, Failed/Skipped: {failCount}");
    }

     // Helper untuk mengirim request PUT/Set secret
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
            var encrypted = EncryptSecret(pubKey.Key, secretValue); // Enkripsi value
            var payload = new
            {
                encrypted_value = encrypted,
                key_id = pubKey.KeyId,
                selected_repository_ids = new[] { repoId.ToString() } // Repo ID harus string di payload
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            // Endpoint API untuk set user-scoped codespace secret
            var response = await client.PutAsync(
                $"https://api.github.com/user/codespaces/secrets/{secretName}",
                content
            );

            // 201 (Created) atau 204 (No Content / Updated) dianggap sukses
            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine("[green]OK[/]");
                return true;
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]Failed ({response.StatusCode})[/]");
                AnsiConsole.MarkupLine($"[dim]      {errorBody.Split('\n').FirstOrDefault()}[/]");
                return false;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Exception: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            return false;
        }
    }

    // Helper untuk mengumpulkan private key dari folder bots/privatekey/*/accounts.txt
    private static List<string> CollectPrivateKeys()
    {
        if (!Directory.Exists(PrivateKeyDir)) {
            AnsiConsole.MarkupLine($"[yellow]⚠️  Directory {PrivateKeyDir} not found.[/]");
            return new List<string>();
        }
        var keys = new List<string>();
        try {
             var dirs = Directory.GetDirectories(PrivateKeyDir);
             foreach (var dir in dirs) {
                 var accountsFile = Path.Combine(dir, "accounts.txt");
                 if (File.Exists(accountsFile)) {
                     keys.AddRange(File.ReadAllLines(accountsFile)
                         .Select(l => l.Trim())
                         .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")));
                 }
             }
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error reading private keys: {ex.Message}[/]"); }
        if (keys.Any()) AnsiConsole.MarkupLine($"[dim]✓ Collected {keys.Count} private keys[/]"); else AnsiConsole.MarkupLine($"[dim]✓ No private keys found in {PrivateKeyDir}[/]");
        return keys;
    }

    // Helper untuk mengumpulkan token dari folder bots/token/*/data.txt
    private static List<string> CollectTokens()
    {
         if (!Directory.Exists(TokenDir)) {
             AnsiConsole.MarkupLine($"[yellow]⚠️  Directory {TokenDir} not found.[/]");
             return new List<string>();
         }
        var tokens = new List<string>();
        try {
             var dirs = Directory.GetDirectories(TokenDir);
             foreach (var dir in dirs) {
                 var dataFile = Path.Combine(dir, "data.txt");
                 if (File.Exists(dataFile)) {
                     tokens.AddRange(File.ReadAllLines(dataFile)
                         .Select(l => l.Trim())
                         .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")));
                 }
             }
         } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error reading tokens: {ex.Message}[/]"); }
        if (tokens.Any()) AnsiConsole.MarkupLine($"[dim]✓ Collected {tokens.Count} bot tokens[/]"); else AnsiConsole.MarkupLine($"[dim]✓ No bot tokens found in {TokenDir}[/]");
        return tokens;
    }

    // Helper untuk memuat URL API dari config/apilist.txt
    private static List<string> LoadApiList()
    {
        if (!File.Exists(ApiListPath)) {
            AnsiConsole.MarkupLine($"[yellow]⚠️  File {ApiListPath} not found.[/]");
            return new List<string>();
        }
        try {
             var lines = File.ReadAllLines(ApiListPath)
                 .Select(l => l.Trim())
                 .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                 .ToList();
             if (lines.Any()) AnsiConsole.MarkupLine($"[dim]✓ Loaded {lines.Count} API URLs[/]"); else AnsiConsole.MarkupLine($"[dim]✓ API list file is empty.[/]");
             return lines;
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error reading API list: {ex.Message}[/]"); return new List<string>(); }
    }

    // --- WARNING: FUNGSI INI SALAH SECARA KRIPTOGRAFI ---
    // GitHub memerlukan enkripsi libsodium "SealedBox" (asimetris).
    // Kode ini menggunakan AES (simetris) yang TIDAK AKAN DITERIMA oleh API.
    private static string EncryptSecret(string publicKeyBase64, string secretValue)
    {
        AnsiConsole.Markup("[red](Using WRONG placeholder encryption!) [/]");
        try {
            // Ini adalah implementasi yang salah, hanya sebagai placeholder
            // agar kode bisa di-compile.
            var keyBytes = Convert.FromBase64String(publicKeyBase64);
            var secretBytes = Encoding.UTF8.GetBytes(secretValue);
            
            // Logika AES ini tidak akan berfungsi dengan API GitHub.
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
             AnsiConsole.MarkupLine($"[red]Placeholder Encryption failed: {ex.Message}. Using Base64...[/]");
             return Convert.ToBase64String(Encoding.UTF8.GetBytes(secretValue));
        }
    }
}
