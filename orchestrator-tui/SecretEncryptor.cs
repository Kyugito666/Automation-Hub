using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace Orchestrator;

/// <summary>
/// AES-256-GCM encryption untuk private keys
/// Key management via GitHub Secrets
/// </summary>
public static class SecretEncryptor
{
    private const int KEY_SIZE = 32; // 256 bits
    private const int IV_SIZE = 12;  // 96 bits (GCM standard)
    private const int TAG_SIZE = 16; // 128 bits authentication tag

    /// <summary>
    /// Generate encryption key (one-time setup)
    /// </summary>
    public static string GenerateKey()
    {
        using var rng = RandomNumberGenerator.Create();
        var key = new byte[KEY_SIZE];
        rng.GetBytes(key);
        return Convert.ToBase64String(key);
    }

    /// <summary>
    /// Encrypt sensitive content dengan AES-256-GCM
    /// Returns: base64(IV + Ciphertext + Tag)
    /// </summary>
    public static string Encrypt(string plaintext, string keyBase64)
    {
        try
        {
            var key = Convert.FromBase64String(keyBase64);
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

            using var aes = new AesGcm(key);
            
            // Generate random IV (nonce)
            var iv = new byte[IV_SIZE];
            RandomNumberGenerator.Fill(iv);
            
            // Allocate buffers
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TAG_SIZE];
            
            // Encrypt
            aes.Encrypt(iv, plaintextBytes, ciphertext, tag);
            
            // Combine: IV + Ciphertext + Tag
            var result = new byte[IV_SIZE + ciphertext.Length + TAG_SIZE];
            Buffer.BlockCopy(iv, 0, result, 0, IV_SIZE);
            Buffer.BlockCopy(ciphertext, 0, result, IV_SIZE, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, IV_SIZE + ciphertext.Length, TAG_SIZE);
            
            return Convert.ToBase64String(result);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Encryption error: {ex.Message}[/]");
            throw;
        }
    }

    /// <summary>
    /// Decrypt encrypted content
    /// </summary>
    public static string Decrypt(string encryptedBase64, string keyBase64)
    {
        try
        {
            var key = Convert.FromBase64String(keyBase64);
            var combined = Convert.FromBase64String(encryptedBase64);
            
            // Extract components
            var iv = new byte[IV_SIZE];
            var ciphertext = new byte[combined.Length - IV_SIZE - TAG_SIZE];
            var tag = new byte[TAG_SIZE];
            
            Buffer.BlockCopy(combined, 0, iv, 0, IV_SIZE);
            Buffer.BlockCopy(combined, IV_SIZE, ciphertext, 0, ciphertext.Length);
            Buffer.BlockCopy(combined, IV_SIZE + ciphertext.Length, tag, 0, TAG_SIZE);
            
            // Decrypt
            using var aes = new AesGcm(key);
            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(iv, ciphertext, tag, plaintext);
            
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Decryption error: {ex.Message}[/]");
            throw;
        }
    }

    /// <summary>
    /// Check if encryption key exists in config
    /// </summary>
    public static bool HasEncryptionKey()
    {
        var keyFile = "../.encryption-key";
        return File.Exists(keyFile);
    }

    /// <summary>
    /// Get or create encryption key
    /// </summary>
    public static string GetOrCreateKey()
    {
        var keyFile = "../.encryption-key";
        
        if (File.Exists(keyFile))
        {
            try
            {
                var key = File.ReadAllText(keyFile).Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    return key;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Can't read key file: {ex.Message}[/]");
            }
        }
        
        // Generate new key
        AnsiConsole.MarkupLine("[yellow]No encryption key found. Generating new key...[/]");
        var newKey = GenerateKey();
        
        try
        {
            File.WriteAllText(keyFile, newKey);
            AnsiConsole.MarkupLine($"[green]✓ Key saved to: {keyFile}[/]");
            AnsiConsole.MarkupLine("[red]IMPORTANT: Add this key to GitHub Secrets![/]");
            AnsiConsole.MarkupLine($"[yellow]Key: {newKey}[/]");
            AnsiConsole.MarkupLine("\n[dim]Steps:[/]");
            AnsiConsole.MarkupLine("[dim]1. Go to GitHub repo → Settings → Secrets → Actions[/]");
            AnsiConsole.MarkupLine("[dim]2. New repository secret[/]");
            AnsiConsole.MarkupLine("[dim]3. Name: SECRETS_ENCRYPTION_KEY[/]");
            AnsiConsole.MarkupLine("[dim]4. Value: (paste key above)[/]");
            AnsiConsole.MarkupLine("\n[yellow]Press Enter to continue...[/]");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error saving key: {ex.Message}[/]");
        }
        
        return newKey;
    }

    /// <summary>
    /// Encrypt secrets dictionary (per-file encryption)
    /// </summary>
    public static string EncryptSecrets(Dictionary<string, string> secrets, string key)
    {
        if (!secrets.Any())
        {
            return string.Empty;
        }

        // Encrypt each file content
        var encrypted = new Dictionary<string, string>();
        
        foreach (var (filename, content) in secrets)
        {
            encrypted[filename] = Encrypt(content, key);
        }
        
        // Serialize encrypted dict
        var json = JsonSerializer.Serialize(encrypted);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Test encryption/decryption (for validation)
    /// </summary>
    public static bool TestEncryption()
    {
        try
        {
            var key = GetOrCreateKey();
            var testData = "0xabcd1234test_private_key";
            
            var encrypted = Encrypt(testData, key);
            var decrypted = Decrypt(encrypted, key);
            
            bool success = testData == decrypted;
            
            if (success)
            {
                AnsiConsole.MarkupLine("[green]✓ Encryption test passed[/]");
                AnsiConsole.MarkupLine($"[dim]  Original: {testData[..20]}...[/]");
                AnsiConsole.MarkupLine($"[dim]  Encrypted: {encrypted[..40]}...[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✗ Encryption test failed[/]");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Encryption test error: {ex.Message}[/]");
            return false;
        }
    }
}
