using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class CodespaceManager
{
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";
    private const int SSH_COMMAND_TIMEOUT_MS = 90000;
    private const int CREATE_TIMEOUT_MS = 600000;
    private const int START_TIMEOUT_MS = 300000;
    private const int WAIT_FOR_STATE_MAX_ATTEMPTS = 15;
    private const int WAIT_FOR_STATE_DELAY_SEC = 30;
    private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";

    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");

    private static readonly string[] SecretFileNames = new[]
    {
        ".env", "pk.txt", "privatekey.txt", "wallet.txt", "token.txt",
        "data.json", "config.json", "settings.json"
    };

    private static string GetProjectRoot()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null)
        {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            if (Directory.Exists(configDir) && File.Exists(gitignore)) return currentDir.FullName;
            currentDir = currentDir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    public static async Task<string> EnsureHealthyCodespace(TokenEntry token)
    {
        AnsiConsole.MarkupLine("\n[cyan]Inspecting codespaces...[/]");
        string repoFullName = $"{token.Owner}/{token.Repo}";
        CodespaceInfo? codespace = null;
        int checkAttempt = 0;

        while (checkAttempt < WAIT_FOR_STATE_MAX_ATTEMPTS)
        {
            checkAttempt++;
            AnsiConsole.Markup($"[dim]Attempt {checkAttempt}/{WAIT_FOR_STATE_MAX_ATTEMPTS}: Finding codespace...[/]");
            var (found, all) = await FindExistingCodespace(token);
            codespace = found;

            if (codespace == null)
            {
                AnsiConsole.MarkupLine("\n[yellow]No existing runner found.[/]");
                await CleanupStuckCodespaces(token, all, null);
                return await CreateNewCodespace(token, repoFullName);
            }

            AnsiConsole.MarkupLine($"\n[green]✓ Found:[/] [dim]{codespace.Name} (State: {codespace.State})[/]");

            switch (codespace.State)
            {
                case "Available":
                    AnsiConsole.MarkupLine("[green]  State 'Available'. Checking health...[/]");
                    if (await CheckHealthWithRetry(token, codespace.Name))
                    {
                        AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing codespace.[/]");
                        return codespace.Name;
                    }
                    AnsiConsole.MarkupLine("[red]  ✗ Health check FAILED (auto-start not complete?). Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null;
                    break;

                case "Stopped":
                case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Starting...[/]");
                    await StartCodespace(token, codespace.Name);
                    AnsiConsole.MarkupLine("[green]  ✓ Codespace Started. Verifying health...[/]");
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                         AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing started codespace.[/]");
                         return codespace.Name;
                    }
                     AnsiConsole.MarkupLine("[red]  ✗ Health check FAILED after start. Deleting...[/]");
                     await DeleteCodespace(token, codespace.Name);
                     codespace = null;
                     break;

                case "Provisioning":
                case "Creating":
                case "Starting":
                case "Queued":
                case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Waiting ({WAIT_FOR_STATE_DELAY_SEC}s)...[/]");
                    await Task.Delay(WAIT_FOR_STATE_DELAY_SEC * 1000);
                    break;

                default:
                    AnsiConsole.MarkupLine($"[red]  State '{codespace.State}' indicates a problem. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null;
                    break;
            }

             if (codespace == null && checkAttempt < WAIT_FOR_STATE_MAX_ATTEMPTS) {
                 AnsiConsole.MarkupLine($"[dim]Retrying find/create after state issue...[/]");
                 await Task.Delay(5000);
             }
        }

        if (codespace == null)
        {
            AnsiConsole.MarkupLine("\n[yellow]Codespace bermasalah atau gagal start setelah retry. Membuat baru...[/]");
            var (foundAfterWait, allAfterWait) = await FindExistingCodespace(token);
            await CleanupStuckCodespaces(token, allAfterWait, foundAfterWait?.Name);
            return await CreateNewCodespace(token, repoFullName);
        }
        else
        {
             AnsiConsole.MarkupLine($"\n[red]FATAL: Codespace '{codespace.Name}' gagal health check setelah {WAIT_FOR_STATE_MAX_ATTEMPTS} attempts.[/]");
             throw new Exception($"Codespace '{codespace.Name}' failed final health check.");
        }
    }

    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName)
    {
        AnsiConsole.MarkupLine($"\n[cyan]Creating new '{CODESPACE_DISPLAY_NAME}' ({MACHINE_TYPE})...[/]");
        AnsiConsole.MarkupLine("[dim]This may take several minutes...[/]");

        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        var newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS);

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new Exception("Failed to create codespace (no name returned)");
        }
        AnsiConsole.MarkupLine($"[green]✓ Created: {newName}[/]");
        await Task.Delay(5000);

        if (!await WaitForSshReadyWithRetry(token, newName))
        {
            AnsiConsole.MarkupLine($"[red]SSH failed for new codespace '{newName}'. Attempting cleanup...[/]");
            await DeleteCodespace(token, newName);
            throw new Exception($"Codespace '{newName}' created but SSH never became ready");
        }

        AnsiConsole.MarkupLine("[cyan]Uploading core configs...[/]");
        await UploadConfigs(token, newName);
        AnsiConsole.MarkupLine("[cyan]Uploading all bot data (pk.txt, .env, etc)...[/]");
        await UploadAllBotData(token, newName);

        return newName;
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName)
    {
        string args = $"codespace start -c {codespaceName}";
        try
        {
            await ShellHelper.RunGhCommand(token, args, START_TIMEOUT_MS);
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[yellow]Warning during start: {ex.Message.Split('\n').FirstOrDefault()}. Attempting SSH anyway...[/]");
        }

        if (!await WaitForSshReadyWithRetry(token, codespaceName))
        {
            throw new Exception($"Failed to start or SSH into {codespaceName} after attempting start.");
        }
    }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName)
    {
        const int maxSshAttempts = 5;
        const int sshRetryDelaySec = 20;

        AnsiConsole.MarkupLine($"[cyan]Waiting for SSH readiness on '{codespaceName}' (up to ~{(maxSshAttempts * (SSH_COMMAND_TIMEOUT_MS/1000 + sshRetryDelaySec))/60} mins)...[/]");

        for (int attempt = 1; attempt <= maxSshAttempts; attempt++)
        {
            AnsiConsole.Markup($"[dim]  SSH Attempt {attempt}/{maxSshAttempts}... [/]");
            try
            {
                string args = $"codespace ssh -c {codespaceName} -- echo ready";
                string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS);

                if (result.Contains("ready"))
                {
                    AnsiConsole.MarkupLine("[green]SSH is ready[/]");
                    return true;
                }
                AnsiConsole.MarkupLine($"[yellow]Unexpected SSH output: '{result}'. Retrying...[/]");
            }
            catch (TaskCanceledException) {
                AnsiConsole.MarkupLine($"[red]SSH command TIMEOUT after {SSH_COMMAND_TIMEOUT_MS / 1000}s. Retrying...[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]SSH check FAILED: {ex.Message.Split('\n').FirstOrDefault()}. Retrying...[/]");
                 if (ex.Message.Contains("Connection refused") || ex.Message.Contains("process exited")) {
                     await Task.Delay(sshRetryDelaySec * 1000);
                }
            }

            if (attempt < maxSshAttempts)
            {
                AnsiConsole.MarkupLine($"[dim]  Waiting {sshRetryDelaySec}s before next SSH attempt...[/]");
                await Task.Delay(sshRetryDelaySec * 1000);
            }
        }

        AnsiConsole.MarkupLine($"[red]Timeout: SSH did not become ready after {maxSshAttempts} attempts[/]");
        return false;
    }

    // === FIX: Health Check Pakai 'ls' & 'echo' (Lebih Reliable) ===
    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName) {
        const int maxHealthAttempts = 3;
        const int healthRetryDelaySec = 10;
        AnsiConsole.MarkupLine($"[cyan]Performing health check on '{codespaceName}'...[/]");

         for (int attempt = 1; attempt <= maxHealthAttempts; attempt++)
        {
            AnsiConsole.Markup($"[dim]  Health Check Attempt {attempt}/{maxHealthAttempts}... [/]");
            try
            {
                // Strategi baru: Cek file pakai 'ls' lalu filter output
                string args = $"codespace ssh -c {codespaceName} -- ls {HEALTH_CHECK_FILE} 2>/dev/null && echo HEALTHY || echo NOT_READY";
                string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS / 2);

                if (result.Contains("HEALTHY"))
                {
                    AnsiConsole.MarkupLine("[green]Healthy (auto-start complete)[/]");
                    return true;
                }
                else if (result.Contains("NOT_READY"))
                {
                    AnsiConsole.MarkupLine("[yellow]Not healthy yet (auto-start likely still running).[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Unexpected response: {result}[/]");
                }
            }
            catch (TaskCanceledException) {
                 AnsiConsole.MarkupLine($"[red]Health check TIMEOUT. Retrying...[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Health check FAILED: {ex.Message.Split('\n').FirstOrDefault()}. Retrying...[/]");
            }

            if (attempt < maxHealthAttempts)
            {
                AnsiConsole.MarkupLine($"[dim]  Waiting {healthRetryDelaySec}s before next health check...[/]");
                await Task.Delay(healthRetryDelaySec * 1000);
            }
        }
         AnsiConsole.MarkupLine($"[red]Health Check Failed after {maxHealthAttempts} attempts.[/]");
         return false;
    }

    public static async Task UploadConfigs(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]Uploading CORE configs (bots_config.json etc.)...[/]");
        string remoteDir = $"/workspaces/{token.Repo}/config";
        AnsiConsole.Markup("[dim]  Ensuring remote config directory... [/]");
        try {
            string mkdirArgs = $"codespace ssh -c {codespaceName} -- mkdir -p {remoteDir}";
            await ShellHelper.RunGhCommand(token, mkdirArgs); AnsiConsole.MarkupLine("[green]OK[/]");
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warning (mkdir): {ex.Message.Split('\n').FirstOrDefault()}[/]"); }

        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "bots_config.json"), $"{remoteDir}/bots_config.json");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "apilist.txt"), $"{remoteDir}/apilist.txt");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "paths.txt"), $"{remoteDir}/paths.txt");
    }

    public static async Task UploadAllBotData(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]Uploading secrets (pk.txt, .env etc) from D:\\SC...[/]");
        var config = BotConfig.Load(); if (config == null) return;
        string remoteRepoRoot = $"/workspaces/{token.Repo}"; int filesUploaded = 0, botsSkipped = 0;
        foreach (var bot in config.BotsAndTools) {
            string localBotPath = BotConfig.GetLocalBotPath(bot.Path);
            string remoteBotPath = $"{remoteRepoRoot}/{bot.Path.Replace('\\', '/')}";
            if (!Directory.Exists(localBotPath)) { botsSkipped++; continue; }
            AnsiConsole.MarkupLine($"[dim]   Scanning {bot.Name}...[/]"); bool botDirCreated = false; int filesForThisBot = 0;
            foreach (var secretFileName in SecretFileNames) {
                string localFilePath = Path.Combine(localBotPath, secretFileName);
                if (File.Exists(localFilePath)) {
                    if (!botDirCreated) {
                        AnsiConsole.Markup($"[dim]     Creating remote dir... [/]");
                        try { string mkdirArgs = $"codespace ssh -c {codespaceName} -- mkdir -p {remoteBotPath}"; await ShellHelper.RunGhCommand(token, mkdirArgs); AnsiConsole.MarkupLine("[green]OK[/]"); botDirCreated = true; }
                        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL ({ex.Message.Split('\n').FirstOrDefault()})[/]"); goto NextBot; }
                    }
                    string remoteFilePath = $"{remoteBotPath}/{secretFileName}";
                    await UploadFile(token, codespaceName, localFilePath, remoteFilePath, silent: true);
                    AnsiConsole.MarkupLine($"[green]     ✓ Uploaded {secretFileName}[/]"); filesUploaded++; filesForThisBot++;
                }
            } if (filesForThisBot == 0) AnsiConsole.MarkupLine($"[dim]     No secrets found.[/]"); NextBot:;
        } AnsiConsole.MarkupLine($"[green]   ✓ Upload complete ({filesUploaded} files).[/]");
    }

    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath, bool silent = false)
    {
        if (!File.Exists(localPath)) { if (!silent) AnsiConsole.MarkupLine($"[yellow]SKIP: {Path.GetFileName(localPath)}[/]"); return; }
        if (!silent) AnsiConsole.Markup($"[dim]  Uploading {Path.GetFileName(localPath)}... [/]");
        string args = $"codespace cp \"{localPath}\" \"remote:{remotePath}\" -c {csName}";
        try { await ShellHelper.RunGhCommand(token, args); if (!silent) AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { if (!silent) AnsiConsole.MarkupLine($"[red]FAIL[/]"); AnsiConsole.MarkupLine($"[red]    Error: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
    }

    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]Triggering remote startup script (auto-start.sh)...[/]");
        string remoteScript = $"/workspaces/{token.Repo}/auto-start.sh";
        AnsiConsole.Markup("[dim]  Verifying script... [/]");
        try { 
            string checkArgs = $"codespace ssh -c {codespaceName} -- ls {remoteScript} 2>/dev/null && echo EXISTS || echo MISSING";
            string checkResult = await ShellHelper.RunGhCommand(token, checkArgs); 
            if (!checkResult.Contains("EXISTS")) throw new Exception("Script not found"); 
            AnsiConsole.MarkupLine("[green]OK[/]"); 
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message}[/]"); throw; }
        
        AnsiConsole.Markup("[dim]  Executing (detached)... [/]");
        string cmd = $"nohup bash {remoteScript} > /tmp/startup.log 2>&1 &";
        string args = $"codespace ssh -c {codespaceName} -- {cmd}";
        try { await ShellHelper.RunGhCommand(token, args); AnsiConsole.MarkupLine("[green]OK[/]"); AnsiConsole.MarkupLine($"[dim]   Monitor via Menu 4 or 'tail -f /tmp/startup.log'[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}[/]"); throw; }
    }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[yellow]Deleting codespace: {codespaceName}...[/]");
        try
        {
            string args = $"codespace delete -c {codespaceName} --force";
            await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS);
            AnsiConsole.MarkupLine("[green]✓ Deleted.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to delete {codespaceName}: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            if (ex.Message.Contains("404") || ex.Message.Contains("Could not find codespace")) {
                 AnsiConsole.MarkupLine($"[dim]   (Already deleted or never existed)[/]");
            } else {
                 AnsiConsole.MarkupLine($"[yellow]   Deletion might have failed. Manual check might be needed.[/]");
            }
        }
         await Task.Delay(3000);
    }

    private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> allCodespaces, string? currentCodespaceName)
    {
        AnsiConsole.MarkupLine("[dim]Cleaning up other potential stuck runners...[/]");
        int cleaned = 0;
        foreach (var cs in allCodespaces)
        {
            if (cs.Name == currentCodespaceName || cs.State == "Deleted") continue;

            if (cs.DisplayName == CODESPACE_DISPLAY_NAME)
            {
                 AnsiConsole.MarkupLine($"[yellow]   Found potentially stuck: {cs.Name} (State: {cs.State}). Deleting...[/]");
                 await DeleteCodespace(token, cs.Name);
                 cleaned++;
            }
        }
         if (cleaned == 0) AnsiConsole.MarkupLine("[dim]   No other stuck runners found.[/]");
    }

    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token)
    {
        string args = "codespace list --json";
        string jsonResult = "";
        try {
            jsonResult = await ShellHelper.RunGhCommand(token, args);
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error listing codespaces: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            return (null, new List<CodespaceInfo>());
        }

        List<CodespaceInfo> allCodespaces = new List<CodespaceInfo>();
        try {
            allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult) ?? new List<CodespaceInfo>();
        } catch (JsonException ex) {
             AnsiConsole.MarkupLine($"[red]Error parsing codespace list JSON: {ex.Message}[/]");
             return (null, new List<CodespaceInfo>());
        }

        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");

        return (existing, allCodespaces);
    }

    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[dim]Fetching running bots from {codespaceName}...[/]");
        string args = $"codespace ssh -c {codespaceName} -- tmux list-windows -F \"#{{window_name}}\"";
        try { 
            string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); 
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s != "dashboard" && s != "bash")
                .OrderBy(s => s)
                .ToList(); 
        }
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]Failed get tmux: {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
            if (ex.Message.Contains("No sessions")) AnsiConsole.MarkupLine("[yellow]   Tmux hasn't started?[/]"); 
            return new List<string>(); 
        }
    }

    private class CodespaceInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("machineName")] public string MachineName { get; set; } = "";
    }
}
