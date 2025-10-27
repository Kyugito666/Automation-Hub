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
    private const int SSH_RETRY_MAX = 10;
    private const int SSH_RETRY_DELAY_SEC = 30;

    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    private static readonly string MasterProxyFile = Path.Combine(ProjectRoot, "proxysync", "proxy.txt");

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

    public static async Task<string> EnsureHealthyCodespace(TokenEntry token)
    {
        AnsiConsole.MarkupLine("\n[cyan]Inspecting existing codespaces...[/]");
        string repoFullName = $"{token.Owner}/{token.Repo}";
        
        try
        {
            var (existing, all) = await FindExistingCodespace(token);

            if (existing != null)
            {
                AnsiConsole.MarkupLine($"[green]✓ Found existing runner:[/] [dim]{existing.Name} (State: {existing.State})[/]");

                if (existing.State == "Available")
                {
                    if (await CheckSshHealthWithRetry(token, existing.Name))
                    {
                        AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing this codespace.[/]");
                        return existing.Name;
                    }
                    
                    AnsiConsole.MarkupLine("[red]  ✗ Health check FAILED. Deleting unhealthy codespace...[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]  Codespace state is '{existing.State}', not 'Available'. Deleting...[/]");
                }

                await DeleteCodespace(token, existing.Name);
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

            // Create new codespace
            AnsiConsole.MarkupLine($"[cyan]Creating new '{CODESPACE_DISPLAY_NAME}' ({MACHINE_TYPE})...[/]");
            AnsiConsole.MarkupLine("[dim]This may take several minutes (creation + boot time)...[/]");
            
            // FIXED: Gunakan -R bukan -r
            string args = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
            var newName = await ShellHelper.RunGhCommand(token, args, CREATE_TIMEOUT_MS);
            
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
        AnsiConsole.MarkupLine("\n[cyan]Uploading configs to codespace...[/]");
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
        await UploadFile(token, codespaceName, MasterProxyFile, $"{remoteDir}/proxy.txt");
    }

    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath)
    {
        if (!File.Exists(localPath))
        {
            AnsiConsole.MarkupLine($"[yellow]  SKIP: {Path.GetFileName(localPath)} not found locally.[/]");
            return;
        }
        
        AnsiConsole.Markup($"[dim]  Uploading {Path.GetFileName(localPath)}... [/]");
        
        // FIXED: Format yang benar untuk gh codespace cp
        // Syntax: gh codespace cp <local> remote:<path> -c <name>
        string args = $"codespace cp \"{localPath}\" \"remote:{remotePath}\" -c {csName}";
        
        try
        {
            await ShellHelper.RunGhCommand(token, args);
            AnsiConsole.MarkupLine("[green]Done[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed[/]");
            AnsiConsole.MarkupLine($"[dim]    Error: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            throw; // Re-throw untuk trigger retry di level atas
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
                AnsiConsole.MarkupLine("[yellow]  Codespace may be in wrong directory or repo not cloned properly[/]");
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
        AnsiConsole.MarkupLine("[dim]   Use 'gh codespace ssh' to monitor 'tmux ls' or 'tail -f /tmp/startup.log'[/]");
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

    /// <summary>
    /// Enhanced SSH health check with retry (adopted from nexus pattern)
    /// </summary>
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
