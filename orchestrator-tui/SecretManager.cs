using Spectre.Console;
using System.IO.Compression;
using System.Text;

namespace Orchestrator;

public static class SecretManager
{
    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string LocalBotRoot = @"D:\SC";
    private static readonly string TempArchivePath = Path.Combine(Path.GetTempPath(), "bot-secrets.tar.gz");

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
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[cyan]   Deploy Secrets via SSH (Direct Upload)[/]");
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");

        var currentToken = TokenManager.GetCurrentToken();
        var state = TokenManager.GetState();

        if (string.IsNullOrEmpty(currentToken.Username))
        {
            AnsiConsole.MarkupLine("[red]✗ Active token has no username![/]");
            AnsiConsole.MarkupLine("[yellow]→ Run Menu 2 -> Validate Tokens first.[/]");
            return;
        }

        var activeCodespace = state.ActiveCodespaceName;
        if (string.IsNullOrEmpty(activeCodespace))
        {
            AnsiConsole.MarkupLine("[red]✗ No active codespace found![/]");
            AnsiConsole.MarkupLine("[yellow]→ Run Menu 1 to create codespace first.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[yellow]Target:[/] [cyan]@{currentToken.Username}[/] → [green]{activeCodespace}[/]");
        AnsiConsole.MarkupLine($"[dim]Proxy: {TokenManager.MaskProxy(currentToken.Proxy)}[/]");

        var config = BotConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]✗ Failed to load bots_config.json[/]");
            return;
        }

        var secretFiles = new Dictionary<string, string>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[yellow]Scanning bot secrets...[/]", async ctx =>
            {
                foreach (var bot in config.BotsAndTools)
                {
                    if (!bot.Enabled) continue;

                    ctx.Status($"[yellow]Scanning {bot.Name}...[/]");
                    
                    var localBotPath = BotConfig.GetLocalBotPath(bot.Path);
                    if (!Directory.Exists(localBotPath)) continue;

                    var files = DetectSecretFilesInDirectory(localBotPath);
                    foreach (var file in files)
                    {
                        var relativePath = Path.Combine(bot.Path, Path.GetFileName(file)).Replace('\\', '/');
                        secretFiles[relativePath] = file;
                        AnsiConsole.MarkupLine($"[green]✓[/] [dim]{relativePath}[/]");
                    }
                }
                await Task.Delay(100);
            });

        if (!secretFiles.Any())
        {
            AnsiConsole.MarkupLine("[yellow]○ No secret files found in D:\\SC[/]");
            return;
        }

        var totalSize = secretFiles.Sum(kvp => new FileInfo(kvp.Value).Length);
        
        AnsiConsole.MarkupLine($"\n[cyan]───────────────────────────────────────────────────────────────[/]");
        AnsiConsole.MarkupLine($"[yellow]Files:[/] [cyan]{secretFiles.Count}[/] | [yellow]Size:[/] [cyan]{totalSize / 1024.0:F1} KB[/]");
        AnsiConsole.MarkupLine($"[cyan]───────────────────────────────────────────────────────────────[/]");
        
        if (!AnsiConsole.Confirm("\n[yellow]Upload secrets to codespace?[/]", false))
        {
            AnsiConsole.MarkupLine("[yellow]✗ Cancelled by user.[/]");
            return;
        }

        try
        {
            AnsiConsole.MarkupLine("\n[cyan]Step 1/3:[/] Creating archive...");
            CreateTarGz(secretFiles, TempArchivePath);
            AnsiConsole.MarkupLine($"[green]✓[/] Archive: [dim]{new FileInfo(TempArchivePath).Length / 1024.0:F1} KB[/]");

            AnsiConsole.MarkupLine("\n[cyan]Step 2/3:[/] Uploading via SSH...");
            string remotePath = "/tmp/bot-secrets.tar.gz";
            string scpArgs = $"codespace cp -c {activeCodespace} \"{TempArchivePath}\" remote:{remotePath}";
            await ShellHelper.RunGhCommand(currentToken, scpArgs, 300000);
            AnsiConsole.MarkupLine($"[green]✓[/] Uploaded to [dim]{remotePath}[/]");

            AnsiConsole.MarkupLine("\n[cyan]Step 3/3:[/] Extracting in codespace...");
            string extractCmd = $"cd /workspaces/automation-hub && tar -xzf {remotePath} && rm {remotePath}";
            string sshArgs = $"codespace ssh -c {activeCodespace} -- \"{extractCmd}\"";
            await ShellHelper.RunGhCommand(currentToken, sshArgs, 120000);
            AnsiConsole.MarkupLine($"[green]✓[/] Extracted to [dim]/workspaces/automation-hub/[/]");

            File.Delete(TempArchivePath);

            AnsiConsole.MarkupLine("\n[cyan]═══════════════════════════════════════════════════════════════[/]");
            AnsiConsole.MarkupLine($"[green]✓ Secrets deployed successfully![/]");
            AnsiConsole.MarkupLine($"[dim]Uploaded {secretFiles.Count} files ({totalSize / 1024.0:F1} KB)[/]");
            AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]✗ Deployment failed: {ex.Message}[/]");
            if (File.Exists(TempArchivePath))
            {
                try { File.Delete(TempArchivePath); } catch { }
            }
        }
    }

    private static void CreateTarGz(Dictionary<string, string> files, string outputPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bot-secrets-temp");
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var kvp in files)
            {
                var targetPath = Path.Combine(tempDir, kvp.Key);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                    Directory.CreateDirectory(targetDir);
                File.Copy(kvp.Value, targetPath, true);
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            string tarArgs = $"-czf \"{outputPath}\" -C \"{tempDir}\" .";
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = tarArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            process?.WaitForExit();
            if (process?.ExitCode != 0)
            {
                throw new Exception("tar command failed");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
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
}
