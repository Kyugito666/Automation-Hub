using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Orchestrator;

/// <summary>
/// Pack local secrets (tokens, keys, wallets) untuk dikirim ke GitHub Actions
/// SECURITY: Detects dan blocks blockchain private keys
/// </summary>
public static class SecretPacker
{
    /// <summary>
    /// Patterns untuk blockchain private keys (NEVER send to remote)
    /// </summary>
    private static readonly Regex[] DANGEROUS_PATTERNS = new[]
    {
        new Regex(@"^(0x)?[0-9a-fA-F]{64}$", RegexOptions.Compiled), // Ethereum private key
        new Regex(@"^[5KL][1-9A-HJ-NP-Za-km-z]{50,51}$", RegexOptions.Compiled), // Bitcoin WIF
        new Regex(@"^xprv[0-9A-Za-z]{107,108}$", RegexOptions.Compiled), // BIP32 extended private key
        new Regex(@"\b(private[_\s]?key|secret[_\s]?key|priv[_\s]?key)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), // Keyword detection
    };

    /// <summary>
    /// File patterns yang AMAN untuk dikirim
    /// </summary>
    private static readonly string[] SAFE_PATTERNS = new[]
    {
        // Token files (revokable)
        "data.txt",
        "tokens.txt", 
        "token.txt",
        "query.txt",
        "queries.txt",
        "accounts.txt",
        "init_data.txt",
        "query_id.txt",
        
        // Config files (API keys - revokable)
        "config.json",
        "settings.json",
        
        // Subdirs
        "data/accounts.txt",
        "data/tokens.txt",
        "config/accounts.txt"
    };

    /// <summary>
    /// File patterns yang BERBAHAYA (akan ditolak)
    /// </summary>
    private static readonly string[] DANGEROUS_FILE_PATTERNS = new[]
    {
        "privateKeys.txt",
        "private_keys.txt",
        "privkey.txt",
        "keys.txt",
        "wallet.txt",
        "wallets.txt",
        "seed.txt",
        "mnemonic.txt",
        ".env" // Bisa contain sensitive keys
    };

    /// <summary>
    /// Check jika content mengandung blockchain private key
    /// </summary>
    private static bool ContainsDangerousSecret(string content)
    {
        var lines = content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));

        foreach (var line in lines)
        {
            foreach (var pattern in DANGEROUS_PATTERNS)
            {
                if (pattern.IsMatch(line))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Scan folder bot untuk cari file sensitive (FILTERED)
    /// </summary>
    public static Dictionary<string, string> ScanBotSecrets(string botPath, string botType)
    {
        var secrets = new Dictionary<string, string>();
        var blocked = new List<string>();
        
        foreach (var pattern in SAFE_PATTERNS)
        {
            var fullPath = Path.Combine(botPath, pattern);
            
            if (File.Exists(fullPath))
            {
                try
                {
                    var content = File.ReadAllText(fullPath);
                    
                    // Skip empty/comment-only files
                    var lines = content.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                        .ToList();
                    
                    if (!lines.Any()) continue;

                    // SECURITY CHECK: Detect dangerous content
                    if (ContainsDangerousSecret(content))
                    {
                        AnsiConsole.MarkupLine($"[red]  ✗ BLOCKED: {pattern} (contains private key pattern)[/]");
                        blocked.Add(pattern);
                        continue;
                    }
                    
                    secrets[pattern] = content;
                    AnsiConsole.MarkupLine($"[green]  ✓ Safe: {pattern} ({lines.Count} lines)[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]  Warning: Can't read {pattern}: {ex.Message}[/]");
                }
            }
        }

        // Check dangerous file patterns (explicit block)
        foreach (var pattern in DANGEROUS_FILE_PATTERNS)
        {
            var fullPath = Path.Combine(botPath, pattern);
            if (File.Exists(fullPath))
            {
                AnsiConsole.MarkupLine($"[red]  ✗ BLOCKED: {pattern} (dangerous file type)[/]");
                blocked.Add(pattern);
            }
        }

        if (blocked.Any())
        {
            AnsiConsole.MarkupLine($"\n[yellow]⚠️  {blocked.Count} file(s) blocked for security reasons[/]");
            AnsiConsole.MarkupLine("[dim]Private keys should NEVER be sent to GitHub Actions[/]");
            AnsiConsole.MarkupLine("[dim]Use local execution for blockchain bots (Menu 5: Debug)[/]");
        }
        
        return secrets;
    }
    
    /// <summary>
    /// Encode secrets ke base64 JSON
    /// </summary>
    public static string PackSecrets(Dictionary<string, string> secrets)
    {
        if (!secrets.Any())
        {
            return string.Empty;
        }
        
        var json = JsonSerializer.Serialize(secrets);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes);
    }
    
    /// <summary>
    /// Interactive: Tanya user mau inject secrets atau tidak
    /// </summary>
    public static string? PromptForSecrets(BotEntry bot)
    {
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return null;
        }
        
        AnsiConsole.MarkupLine($"\n[cyan]Scanning for secrets in {bot.Name}...[/]");
        var secrets = ScanBotSecrets(botPath, bot.Type);
        
        if (!secrets.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No SAFE files found (tokens.txt, data.txt, etc.)[/]");
            AnsiConsole.MarkupLine("[red]WARNING: Bot will likely fail without credentials[/]");
            
            // Check if bot is blockchain type
            bool isBlockchainBot = bot.Path.Contains("/privatekey/");
            
            if (isBlockchainBot)
            {
                AnsiConsole.MarkupLine("\n[bold red]⚠️  BLOCKCHAIN BOT DETECTED[/]");
                AnsiConsole.MarkupLine("[yellow]This bot likely needs private keys[/]");
                AnsiConsole.MarkupLine("\n[cyan]Options:[/]");
                AnsiConsole.MarkupLine("[dim]1. Use encrypted injection (RECOMMENDED)[/]");
                AnsiConsole.MarkupLine("[dim]2. Run locally only (Menu 5: Debug)[/]");
                
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("\n[yellow]How to proceed?[/]")
                        .AddChoices(new[]
                        {
                            "1. Setup encrypted injection",
                            "2. Cancel (run locally)",
                            "3. Continue WITHOUT keys (will fail)"
                        }));
                
                if (choice.StartsWith("1"))
                {
                    return SetupEncryptedInjection(bot);
                }
                else if (choice.StartsWith("2"))
                {
                    return null;
                }
                // else continue without keys
            }
            
            var createManual = AnsiConsole.Confirm("Open bot folder to add SAFE files (tokens/data)?", false);
            
            if (createManual)
            {
                ShellHelper.RunInNewTerminal(
                    System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "explorer" : "nautilus",
                    $"\"{botPath}\"",
                    null
                );
                AnsiConsole.MarkupLine("[yellow]Press Enter after adding files...[/]");
                Console.ReadLine();
                
                // Re-scan
                secrets = ScanBotSecrets(botPath, bot.Type);
                
                if (!secrets.Any())
                {
                    AnsiConsole.MarkupLine("[red]Still no safe files found. Continuing without secrets.[/]");
                    return null;
                }
            }
            else
            {
                return null;
            }
        }
        
        // Check for dangerous files (offer encryption)
        bool hasDangerousFiles = CheckForDangerousFiles(botPath);
        
        if (hasDangerousFiles)
        {
            AnsiConsole.MarkupLine("\n[yellow]⚠️  Private key files detected[/]");
            
            var encryptChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]How to handle private keys?[/]")
                    .AddChoices(new[]
                    {
                        "1. Encrypt & inject (AES-256)",
                        "2. Skip private keys (send safe files only)",
                        "3. Cancel (run locally)"
                    }));
            
            if (encryptChoice.StartsWith("1"))
            {
                return SetupEncryptedInjection(bot);
            }
            else if (encryptChoice.StartsWith("3"))
            {
                return null;
            }
            // else: continue with safe files only
        }
        
        // Preview secrets (masked)
        var table = new Table().Title($"Found {secrets.Count} SAFE Secret Files");
        table.AddColumn("File");
        table.AddColumn("Lines");
        table.AddColumn("Preview");
        table.AddColumn("Type");
        
        foreach (var (file, content) in secrets)
        {
            var lines = content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var preview = lines.FirstOrDefault() ?? "";
            
            // Mask sensitive data
            if (preview.Length > 20)
            {
                preview = preview[..10] + "..." + preview[^7..];
            }

            string type = file.Contains("token") || file.Contains("query") 
                ? "[green]Token[/]" 
                : "[cyan]Config[/]";
            
            table.AddRow(file, lines.Count.ToString(), $"[dim]{preview}[/]", type);
        }
        
        AnsiConsole.Write(table);
        
        AnsiConsole.MarkupLine("\n[green]✓ All files passed security check[/]");
        AnsiConsole.MarkupLine("[dim]Private keys were blocked automatically[/]");
        
        var confirm = AnsiConsole.Confirm("\nInject these SAFE secrets to remote bot?", true);
        
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[yellow]Secrets NOT injected. Bot may fail.[/]");
            return null;
        }
        
        return PackSecrets(secrets);
    }

    /// <summary>
    /// Check jika ada file private key
    /// </summary>
    private static bool CheckForDangerousFiles(string botPath)
    {
        foreach (var pattern in DANGEROUS_FILE_PATTERNS)
        {
            if (File.Exists(Path.Combine(botPath, pattern)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Setup encrypted injection untuk private keys
    /// </summary>
    private static string? SetupEncryptedInjection(BotEntry bot)
    {
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        
        AnsiConsole.MarkupLine("\n[cyan]═══════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[bold cyan]ENCRYPTED INJECTION SETUP[/]");
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════[/]");
        
        // Test encryption
        if (!SecretEncryptor.HasEncryptionKey())
        {
            AnsiConsole.MarkupLine("\n[yellow]First-time setup required[/]");
        }
        
        if (!SecretEncryptor.TestEncryption())
        {
            AnsiConsole.MarkupLine("[red]Encryption test failed. Aborting.[/]");
            return null;
        }
        
        // Scan ALL files (including dangerous)
        var allSecrets = ScanAllFiles(botPath);
        
        if (!allSecrets.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No files found to encrypt[/]");
            return null;
        }
        
        // Preview
        var table = new Table().Title($"Files to Encrypt ({allSecrets.Count})");
        table.AddColumn("File");
        table.AddColumn("Lines");
        table.AddColumn("Type");
        
        foreach (var (file, content) in allSecrets)
        {
            var lines = content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Count();
            bool isDangerous = DANGEROUS_FILE_PATTERNS.Contains(file) || 
                               ContainsDangerousSecret(content);
            
            string type = isDangerous 
                ? "[red]Private Key[/]" 
                : "[green]Token[/]";
            
            table.AddRow(file, lines.ToString(), type);
        }
        
        AnsiConsole.Write(table);
        
        var confirm = AnsiConsole.Confirm("\n[yellow]Encrypt & inject these files?[/]", true);
        
        if (!confirm)
        {
            return null;
        }
        
        // Encrypt
        AnsiConsole.MarkupLine("\n[cyan]Encrypting secrets...[/]");
        
        try
        {
            var key = SecretEncryptor.GetOrCreateKey();
            var encrypted = SecretEncryptor.EncryptSecrets(allSecrets, key);
            
            AnsiConsole.MarkupLine("[green]✓ Encryption complete[/]");
            AnsiConsole.MarkupLine($"[dim]Payload size: {encrypted.Length} chars[/]");
            AnsiConsole.MarkupLine($"[dim]Preview: {encrypted[..Math.Min(60, encrypted.Length)]}...[/]");
            
            return encrypted;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Encryption failed: {ex.Message}[/]");
            return null;
        }
    }

    /// <summary>
    /// Scan ALL files (tanpa filter, untuk encrypted mode)
    /// </summary>
    private static Dictionary<string, string> ScanAllFiles(string botPath)
    {
        var secrets = new Dictionary<string, string>();
        
        // Gabungan safe + dangerous patterns
        var allPatterns = SAFE_PATTERNS.Concat(DANGEROUS_FILE_PATTERNS).Distinct();
        
        foreach (var pattern in allPatterns)
        {
            var fullPath = Path.Combine(botPath, pattern);
            
            if (File.Exists(fullPath))
            {
                try
                {
                    var content = File.ReadAllText(fullPath);
                    
                    var lines = content.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                        .ToList();
                    
                    if (lines.Any())
                    {
                        secrets[pattern] = content;
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]  Warning: Can't read {pattern}: {ex.Message}[/]");
                }
            }
        }
        
        return secrets;
    }
}
