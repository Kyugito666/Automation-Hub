using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class CodespaceManager
{
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";
    private const int SSH_TIMEOUT_MS = 30000;
    private const int CREATE_TIMEOUT_MS = 600000;
    private const int START_TIMEOUT_MS = 300000;
    private const int SSH_RETRY_MAX = 10;
    private const int SSH_RETRY_DELAY_SEC = 30;

    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    // === PERBAIKAN: Hapus MasterProxyFile ===
    // Proxy sekarang 100% di-generate di remote.
    // private static readonly string MasterProxyFile = Path.Combine(ProjectRoot, "proxysync", "proxy.txt");

    // Daftar file rahasia yang akan di-upload dari D:\SC ke remote
    private static readonly string[] SecretFileNames = new[]
    {
        ".env",
        "pk.txt",
        "privatekey.txt",
        "wallet.txt",
        "token.txt",
        "data.json",
        "config.json",
        "settings.json"
    };

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

    // === PEROMBAKAN TOTAL LOGIKA ===
    public static async Task<string> EnsureHealthyCodespace(TokenEntry token)
    {
        AnsiConsole.MarkupLine("\n[cyan]Inspecting existing codespaces...[/]");
        string repoFullName = $"{token.Owner}/{token.Repo}";
        CodespaceInfo? existing = null;
        
        try
        {
            var (found, all) = await FindExistingCodespace(token);
            existing = found;

            if (existing != null)
            {
                AnsiConsole.MarkupLine($"[green]✓ Found existing runner:[/] [dim]{existing.Name} (State: {existing.State})[/]");

                if (existing.State == "Available")
                {
                    AnsiConsole.MarkupLine("[green]  ✓ Codespace 'Available'. Reusing.[/]");
                    // Tetap jalankan health check
                    if (await CheckSshHealthWithRetry(token, existing.Name))
                    {
                        return existing.Name;
                    }
                    AnsiConsole.MarkupLine("[red]  ✗ Health check FAILED. Deleting unhealthy codespace...[/]");
                }
                else if (existing.State == "Stopped")
                {
                    AnsiConsole.MarkupLine("[yellow]  Codespace 'Stopped'. Starting...[/]");
                    await StartCodespace(token, existing.Name);
                    AnsiConsole.MarkupLine("[green]  ✓ Codespace Started. Reusing.[/]");
                    return existing.Name;
                }
                else if (existing.State.Contains("Starting") || existing.State == "Queued")
                {
                    AnsiConsole.MarkupLine($"[yellow]  Codespace '{existing.State}'. Waiting...[/]");
                    if (!await WaitForSshReady(token, existing.Name))
                    {
                        AnsiConsole.MarkupLine("[red]  ✗ Gagal 'wake up'. Deleting...[/]");
                    }
                    else
                    {
                         AnsiConsole.MarkupLine("[green]  ✓ Codespace 'Available'. Reusing.[/]");
                         return existing.Name;
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]  Codespace state is '{existing.State}'. Deleting...[/]");
                }

                await DeleteCodespace(token, existing.Name);
                existing = null; // Tandai sebagai sudah dihapus
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No existing runner found.[/]");
            }
            
            // Cleanup stuck codespaces
            foreach (var cs in all.Where(cs => cs.Name != existing?.Name && cs.DisplayName == CODESPACE_DISPLAY_NAME))
            {
                AnsiConsole.MarkupLine($"[yellow]Deleting STUCK codespace: {cs.Name} (State: {cs.State})[/]");
                await DeleteCodespace(token, cs.Name);
            }

            // --- BUAT BARU ---
            AnsiConsole.MarkupLine($"[cyan]Creating new '{CODESPACE_DISPLAY_NAME}' ({MACHINE_TYPE})...[/]");
            AnsiConsole.MarkupLine("[dim]This may take several minutes...[/]");
            
            string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
            var newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS);
            
            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new Exception("Failed to create codespace (no name returned)");
            }
            
            AnsiConsole.MarkupLine($"[green]✓ Created: {newName}[/]");
            
            // Wait for SSH ready (critical!)
            if (!await WaitForSshReady(token, newName))
            {
                throw new Exception($"Codespace '{newName}' created but SSH never became ready");
            }
            
            // --- INI LOGIKA BARU: UPLOAD SEMUA DATA ---
            // Hanya dijalankan saat create baru
            AnsiConsole.MarkupLine("[cyan]New codespace detected. Uploading all bot data (pk.txt, .env, etc...)[/]");
            await UploadAllBotData(token, newName);
            
            return newName;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]FATAL: Gagal memastikan codespace: {ex.Message}[/]");
            if (ex.Message.Contains("403") || ex.Message.Contains("quota"))
            {
                 AnsiConsole.MarkupLine("[red]Ini kemungkinan besar masalah kuota. Coba rotasi token.[/]");
            }
            throw;
        }
    }
    // === AKHIR PEROMBAKAN LOGIKA ===
    
    /// <summary>
    /// FUNGSI BARU: Membangunkan codespace yang 'Stopped'
    /// </summary>
    private static async Task StartCodespace(TokenEntry token, string codespaceName)
    {
        string args = $"codespace start -c {codespaceName}";
        await ShellHelper.RunGhCommand(token, args, START_TIMEOUT_MS);
        
        // Setelah start, tunggu SSH ready
        if (!await WaitForSshReady(token, codespaceName))
        {
            throw new Exception($"Failed to start or SSH into {codespaceName}");
        }
    }

    /// <summary>
    /// Wait for SSH to be ready (adopted from nexus-orchestrator pattern)
    /// </summary>
    private static async Task<bool> WaitForSshReady(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[cyan]Waiting for SSH readiness on '{codespaceName}'...[/]");
        
        for (int attempt = 1; attempt <= SSH_RETRY_MAX; attempt++)
        {
            AnsiConsole.Markup($"[dim]  Attempt {attempt}/{SSH_RETRY_MAX}... [/]");
            
            try
            {
                string args = $"codespace ssh -c {codespaceName} -- echo 'ready'";
                string result = await ShellHelper.RunGhCommand(token, args, SSH_TIMEOUT_MS);
                
                if (result.Contains("ready"))
                {
                    AnsiConsole.MarkupLine("[green]SSH is ready[/]");
                    return true;
                }
                
                AnsiConsole.MarkupLine("[yellow]Not ready yet[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]SSH check failed: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            }
            
            if (attempt < SSH_RETRY_MAX)
            {
                AnsiConsole.MarkupLine($"[dim]  Waiting {SSH_RETRY_DELAY_SEC}s before next attempt...[/]");
                await Task.Delay(SSH_RETRY_DELAY_SEC * 1000);
            }
        }
        
        AnsiConsole.MarkupLine($"[red]Timeout: SSH did not become ready after {SSH_RETRY_MAX} attempts[/]");
        return false;
    }

    public static async Task UploadConfigs(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]Uploading CORE configs to codespace...[/]");
        string remoteDir = $"/workspaces/{token.Repo}/config";

        // Ensure remote config directory exists first
        AnsiConsole.Markup("[dim]  Creating remote config directory... [/]");
        try
        {
            string mkdirArgs = $"codespace ssh -c {codespaceName} -- mkdir -p {remoteDir}";
            await ShellHelper.RunGhCommand(token, mkdirArgs);
            AnsiConsole.MarkupLine("[green]OK[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: {ex.Message.Split('\n').FirstOrDefault()}[/]");
        }

        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "bots_config.json"), $"{remoteDir}/bots_config.json");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "apilist.txt"), $"{remoteDir}/apilist.txt");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "paths.txt"), $"{remoteDir}/paths.txt");
        
        // === PERBAIKAN: Hapus upload proxy.txt ===
        // await UploadFile(token, codespaceName, MasterProxyFile, $"{remoteDir}/proxy.txt");
    }
    
    // === FUNGSI BARU: Upload file rahasia (pk.txt, .env) dari D:\SC ===
    public static async Task UploadAllBotData(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]Uploading ALL bot data (pk.txt, .env, etc) from local D:\\SC ...[/]");
        var config = BotConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]   Failed to load bots_config.json. Skipping upload.[/]");
            return;
        }

        string remoteRepoRoot = $"/workspaces/{token.Repo}";
        int filesUploaded = 0;
        int botsSkipped = 0;

        foreach (var bot in config.BotsAndTools)
        {
            // Dapatkan path D:\SC\PrivateKey\BotName
            string localBotPath = BotConfig.GetLocalBotPath(bot.Path);
            // Dapatkan path /workspaces/automation-hub/bots/privatekey/BotName
            string remoteBotPath = $"{remoteRepoRoot}/{bot.Path.Replace('\\', '/')}";

            if (!Directory.Exists(localBotPath))
            {
                AnsiConsole.MarkupLine($"[dim]   SKIP: {bot.Name} (Local path not found: {localBotPath})[/]");
                botsSkipped++;
                continue;
            }
            
            AnsiConsole.MarkupLine($"[dim]   Scanning {bot.Name} in {localBotPath}...[/]");
            bool botDirCreated = false;

            foreach (var secretFileName in SecretFileNames)
            {
                string localFilePath = Path.Combine(localBotPath, secretFileName);
                if (File.Exists(localFilePath))
                {
                    // Buat direktori bot di remote (hanya sekali jika diperlukan)
                    if (!botDirCreated)
                    {
                        string mkdirArgs = $"codespace ssh -c {codespaceName} -- mkdir -p {remoteBotPath}";
                        await ShellHelper.RunGhCommand(token, mkdirArgs);
                        botDirCreated = true;
                    }

                    string remoteFilePath = $"{remoteBotPath}/{secretFileName}";
                    await UploadFile(token, codespaceName, localFilePath, remoteFilePath, silent: true);
                    AnsiConsole.MarkupLine($"[green]     ✓ Uploaded {secretFileName}[/]");
                    filesUploaded++;
                }
            }
        }
        
        AnsiConsole.MarkupLine($"[green]   ✓ Upload complete. {filesUploaded} files uploaded. {botsSkipped} bots skipped (no local data).[/]");
    }

    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath, bool silent = false)
    {
        if (!File.Exists(localPath))
        {
            if (!silent) AnsiConsole.MarkupLine($"[yellow]  SKIP: {Path.GetFileName(localPath)} not found locally.[/]");
            return;
        }
        
        if (!silent) AnsiConsole.Markup($"[dim]  Uploading {Path.GetFileName(localPath)}... [/]");
        
        // Format: gh codespace cp <local> remote:<path> -c <name>
        string args = $"codespace cp \"{localPath}\" \"remote:{remotePath}\" -c {csName}";
        
        try
        {
            await ShellHelper.RunGhCommand(token, args);
            if (!silent) AnsiConsole.MarkupLine("[green]Done[/]");
        }
        catch (Exception ex)
        {
            if (!silent) AnsiConsole.MarkupLine($"[red]Failed[/]");
            AnsiConsole.MarkupLine($"[dim]    Error uploading {Path.GetFileName(localPath)}: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            // Don't throw, attempt to continue
        }
    }

    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]Triggering remote startup script (auto-start.sh)...[/]");
        string remoteScript = $"/workspaces/{token.Repo}/auto-start.sh";
        
        // Verify script exists
        AnsiConsole.Markup("[dim]  Verifying auto-start.sh exists... [/]");
        try
        {
            string checkArgs = $"codespace ssh -c {codespaceName} -- test -f {remoteScript} && echo 'exists'";
            string checkResult = await ShellHelper.RunGhCommand(token, checkArgs);
            
            if (!checkResult.Contains("exists"))
            {
                AnsiConsole.MarkupLine("[red]FAILED[/]");
                AnsiConsole.MarkupLine($"[red]  auto-start.sh not found at {remoteScript}[/]");
                throw new Exception("Startup script not found");
            }
            
            AnsiConsole.MarkupLine("[green]OK[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to verify script: {ex.Message}[/]");
            throw;
        }
        
        // Execute startup script (detached via nohup)
        AnsiConsole.Markup("[dim]  Executing startup script (detached)... [/]");
        string cmd = $"\"nohup bash {remoteScript} > /tmp/startup.log 2>&1 &\"";
        string args = $"codespace ssh -c {codespaceName} -- {cmd}";

        await ShellHelper.RunGhCommand(token, args);
        AnsiConsole.MarkupLine("[green]Done[/]");
        AnsiConsole.MarkupLine($"[dim]   Use 'Menu 4 (Attach)' or 'gh codespace ssh -c {codespaceName} -- tail -f /tmp/startup.log'[/]");
    }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[yellow]Deleting codespace: {codespaceName}...[/]");
        try
        {
            string args = $"codespace delete -c {codespaceName} --force";
            await ShellHelper.RunGhCommand(token, args);
            AnsiConsole.MarkupLine("[green]✓ Deleted.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to delete {codespaceName}: {ex.Message}[/]");
        }
    }

    public static async Task<bool> CheckSshHealthWithRetry(TokenEntry token, string codespaceName)
    {
        const int maxAttempts = 3;
        
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                string args = $"codespace ssh -c {codespaceName} -- echo 'alive'";
                string result = await ShellHelper.RunGhCommand(token, args, SSH_TIMEOUT_MS);
                
                if (result.Contains("alive"))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    AnsiConsole.MarkupLine($"[red]  SSH check failed after {maxAttempts} attempts: {ex.Message}[/]");
                }
            }
            
            if (attempt < maxAttempts)
            {
                await Task.Delay(5000);
            }
        }
        
        return false;
    }

    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token)
    {
        string args = "codespace list --json name,displayName,state,machineName";
        string jsonResult = await ShellHelper.RunGhCommand(token, args);

        var allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult) ?? new List<CodespaceInfo>();

        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME);
        
        return (existing, allCodespaces);
    }
    
    // Helper untuk 'gh codespace ssh ... tmux ls'
    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[dim]Fetching running bots from {codespaceName}...[/]");
        // -F "#{window_name}" -> Hanya print nama windownya
        string args = $"codespace ssh -c {codespaceName} -- tmux ls -F \"#{{window_name}}\"";
        
        try
        {
            string result = await ShellHelper.RunGhCommand(token, args, SSH_TIMEOUT_MS);
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(s => s != "dashboard") // Hapus window 'dashboard'
                         .OrderBy(s => s)
                         .ToList();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to get tmux sessions: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            return new List<string>();
        }
    }
    
    private class CodespaceInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";
        [JsonPropertyName("state")]
        public string State { get; set; } = "";
        [JsonPropertyName("machineName")]
        public string MachineName { get; set; } = "";
    }
}
