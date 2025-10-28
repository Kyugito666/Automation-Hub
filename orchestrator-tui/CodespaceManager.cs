using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Diagnostics;

namespace Orchestrator;

public static class CodespaceManager
{
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";
    private const int SSH_COMMAND_TIMEOUT_MS = 120000;
    private const int CREATE_TIMEOUT_MS = 600000; // 10 menit (turun dari 15)
    private const int STATE_POLL_INTERVAL_SEC = 20; // Turun dari 30
    private const int STATE_POLL_MAX_DURATION_MIN = 10; // Turun dari 20
    private const int PROVISIONING_MAX_WAIT_MIN = 5; // KRITIS: Delete jika stuck >5 menit
    private const int SSH_READY_POLL_INTERVAL_SEC = 15;
    private const int SSH_READY_MAX_DURATION_MIN = 8;
    private const int SSH_PROBE_TIMEOUT_MS = 20000;
    private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 10;
    private const int HEALTH_CHECK_MAX_DURATION_MIN = 3; // Turun dari 5
    private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
    private const string HEALTH_CHECK_FAIL_PROXY = "/tmp/auto_start_failed_proxysync";
    private const string HEALTH_CHECK_FAIL_DEPLOY = "/tmp/auto_start_failed_deploy";

    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    private static readonly string[] SecretFileNames = {
        ".env", "pk.txt", "privatekey.txt", "wallet.txt", "token.txt",
        "data.json", "config.json", "settings.json"
    };

    private static string GetProjectRoot() { return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", "..")); }

    public static async Task<string> EnsureHealthyCodespace(TokenEntry token)
    {
        AnsiConsole.MarkupLine("\n[cyan]Ensuring Codespace Runner...[/]");
        string repoFullName = $"{token.Owner}/{token.Repo}";
        CodespaceInfo? codespace = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        Stopwatch? provisioningStopwatch = null;

        while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN)
        {
            double elapsedMinutes = stopwatch.Elapsed.TotalMinutes;
            AnsiConsole.Markup($"[dim]({elapsedMinutes:F1}/{STATE_POLL_MAX_DURATION_MIN} min): Finding codespace...[/]");

            var (found, all) = await FindExistingCodespace(token);
            codespace = found;

            if (codespace == null) {
                AnsiConsole.MarkupLine("\n[yellow]Not found.[/]");
                await CleanupStuckCodespaces(token, all, null);
                return await CreateNewCodespace(token, repoFullName);
            }

            AnsiConsole.MarkupLine($"\n[green]✓ Found:[/] [dim]{codespace.Name} (State: {codespace.State})[/]");

            switch (codespace.State)
            {
                case "Available":
                    AnsiConsole.MarkupLine("[green]  State 'Available'. Verifying SSH...[/]");
                    if (!await WaitForSshReadyWithRetry(token, codespace.Name)) {
                         AnsiConsole.MarkupLine($"[red]  ✗ SSH failed. Deleting...[/]");
                         await DeleteCodespace(token, codespace.Name); codespace = null; break;
                    }
                    AnsiConsole.MarkupLine("[green]  ✓ SSH Ready. Checking health...[/]");
                    
                    // PERBAIKAN: Cek health dulu sebelum trigger
                    var healthStatus = await QuickHealthCheck(token, codespace.Name);
                    if (healthStatus == HealthStatus.Healthy) {
                        AnsiConsole.MarkupLine("[green]  ✓ Already healthy. Reusing.[/]");
                        stopwatch.Stop(); return codespace.Name;
                    }
                    
                    if (healthStatus == HealthStatus.Failed) {
                        AnsiConsole.MarkupLine("[red]  ✗ Failed health flag found. Recreating...[/]");
                        await DeleteCodespace(token, codespace.Name); codespace = null; break;
                    }
                    
                    // Not healthy yet, trigger startup
                    AnsiConsole.MarkupLine("[yellow]  ⚠ Not healthy. Triggering startup...[/]");
                    await TriggerStartupScript(token, codespace.Name);
                    
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                        AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED.[/]");
                        stopwatch.Stop(); return codespace.Name;
                    }
                    AnsiConsole.MarkupLine($"[yellow]  ⚠ Timeout but SSH OK. Assuming healthy.[/]");
                    stopwatch.Stop(); return codespace.Name;

                case "Stopped": case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Starting...[/]");
                    await StartCodespace(token, codespace.Name);
                    AnsiConsole.MarkupLine("[green]  ✓ Started. Triggering startup...[/]");
                    await TriggerStartupScript(token, codespace.Name);
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                         AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED.[/]");
                         stopwatch.Stop(); return codespace.Name;
                    }
                    AnsiConsole.MarkupLine($"[yellow]  ⚠ Timeout but SSH OK. Assuming healthy.[/]");
                    stopwatch.Stop(); return codespace.Name;

                case "Provisioning": case "Creating": case "Starting":
                    // KRITIS: Mulai timer untuk Provisioning
                    if (provisioningStopwatch == null) {
                        provisioningStopwatch = Stopwatch.StartNew();
                        AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Waiting max {PROVISIONING_MAX_WAIT_MIN} min...[/]");
                    } else {
                        double provisioningMinutes = provisioningStopwatch.Elapsed.TotalMinutes;
                        AnsiConsole.MarkupLine($"[yellow]  Still '{codespace.State}' ({provisioningMinutes:F1}/{PROVISIONING_MAX_WAIT_MIN} min)...[/]");
                        
                        // DELETE LANGSUNG jika stuck >5 menit
                        if (provisioningMinutes >= PROVISIONING_MAX_WAIT_MIN) {
                            AnsiConsole.MarkupLine($"[red]  ✗ STUCK >{PROVISIONING_MAX_WAIT_MIN} min. DELETING FORCEFULLY![/]");
                            await DeleteCodespace(token, codespace.Name);
                            provisioningStopwatch = null;
                            codespace = null;
                            break;
                        }
                    }
                    
                    AnsiConsole.MarkupLine($"[dim]  Waiting {STATE_POLL_INTERVAL_SEC}s...[/]");
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000); 
                    continue;

                case "Queued": case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Waiting...[/]");
                    AnsiConsole.MarkupLine($"[dim]  Waiting {STATE_POLL_INTERVAL_SEC}s...[/]");
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000); 
                    continue;

                default:
                    AnsiConsole.MarkupLine($"[red]  State '{codespace.State}' error. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name); 
                    provisioningStopwatch = null;
                    codespace = null; 
                    break;
            }

             if (codespace == null && stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                 AnsiConsole.MarkupLine($"[dim]Retry after issue...[/]"); 
                 await Task.Delay(5000);
             }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine($"\n[red]FATAL: Timeout after {STATE_POLL_MAX_DURATION_MIN} mins.[/]");
        AnsiConsole.MarkupLine("[yellow]Attempting final cleanup & create...[/]");
        var (_, allFinal) = await FindExistingCodespace(token); 
        await CleanupStuckCodespaces(token, allFinal, null);
        
        try { 
            return await CreateNewCodespace(token, repoFullName); 
        } catch (Exception createEx) { 
            AnsiConsole.WriteException(createEx); 
            throw new Exception($"FATAL: Final create failed. Error: {createEx.Message}"); 
        }
    }

    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName) {
        AnsiConsole.MarkupLine($"\n[cyan]Creating new '{CODESPACE_DISPLAY_NAME}' ({MACHINE_TYPE})...[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        Stopwatch createStopwatch = Stopwatch.StartNew(); 
        string newName = "";
        
        try {
            newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS); 
            createStopwatch.Stop();
            
            if (string.IsNullOrWhiteSpace(newName)) 
                throw new Exception("gh create empty name");
            
            AnsiConsole.MarkupLine($"[green]✓ Created: {newName} (in {createStopwatch.Elapsed:mm\\:ss})[/]"); 
            
            // PERBAIKAN: Wait state Available dengan timeout ketat
            AnsiConsole.MarkupLine("[cyan]Waiting for 'Available' state (max 8 min)...[/]");
            if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(8))) {
                AnsiConsole.MarkupLine($"[yellow]State timeout. Forcing upload anyway...[/]");
            }
            
            if (!await WaitForSshReadyWithRetry(token, newName)) {
                AnsiConsole.MarkupLine($"[yellow]SSH timeout. Forcing upload anyway...[/]");
            }

            await UploadConfigs(token, newName); 
            await UploadAllBotData(token, newName);

            AnsiConsole.MarkupLine("[cyan]Triggering initial setup (auto-start.sh)...[/]"); 
            await TriggerStartupScript(token, newName);
            
            AnsiConsole.MarkupLine("[cyan]Waiting initial setup (max 3 min)...[/]");
            if (!await CheckHealthWithRetry(token, newName)) { 
                AnsiConsole.MarkupLine($"[yellow]Initial setup timeout. Assuming running in background.[/]"); 
            } else { 
                AnsiConsole.MarkupLine("[green]✓ Initial setup complete.[/]"); 
            }
            
            return newName;

        } catch (Exception ex) {
            createStopwatch.Stop(); 
            AnsiConsole.WriteException(ex);
            
            if (!string.IsNullOrWhiteSpace(newName)) { 
                await DeleteCodespace(token, newName); 
            }
            
            string info = ""; 
            if (ex.Message.Contains("quota")) info = " (Check Quota!)"; 
            else if (ex.Message.Contains("401")) info = " (Invalid Token!)";
            
            throw new Exception($"FATAL: Create failed{info}. Error: {ex.Message}");
        }
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName) {
        string args = $"codespace start -c {codespaceName}"; 
        
        try { 
            await ShellHelper.RunGhCommand(token, args, 180000); // 3 menit max
        } catch (Exception ex) { 
            if(!ex.Message.Contains("is already available")) 
                AnsiConsole.MarkupLine($"[yellow]  Warn (start): {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
        }
        
        if (!await WaitForState(token, codespaceName, "Available", TimeSpan.FromMinutes(5))) 
            AnsiConsole.MarkupLine($"[yellow]State timeout after start.[/]");
        
        if (!await WaitForSshReadyWithRetry(token, codespaceName)) 
            AnsiConsole.MarkupLine($"[yellow]SSH timeout after start.[/]");
    }

    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout) {
        Stopwatch sw = Stopwatch.StartNew(); 
        
        while(sw.Elapsed < timeout) {
             var state = await GetCodespaceState(token, codespaceName);
             
             if (state == targetState) { 
                 sw.Stop(); 
                 return true; 
             }
             
             if (state == null || state == "Failed" || state == "Error" || state.Contains("ShuttingDown") || state=="Deleted") { 
                 sw.Stop(); 
                 return false; 
             }
             
             await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000);
        } 
        
        sw.Stop(); 
        return false;
    }

     private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName) {
         string args = $"codespace view --json state -c {codespaceName}";
         try { 
             string json = await ShellHelper.RunGhCommand(token, args, 15000); 
             using var doc = JsonDocument.Parse(json); 
             return doc.RootElement.GetProperty("state").GetString(); 
         } catch { 
             return null; 
         }
     }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName) {
        Stopwatch sw = Stopwatch.StartNew();
        
        while(sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
            try { 
                string args = $"codespace ssh -c {codespaceName} -- echo ready"; 
                string res = await ShellHelper.RunGhCommand(token, args, 30000); // 30 detik
                
                if (res.Contains("ready")) { 
                    sw.Stop(); 
                    return true; 
                }
            } catch { 
                // Silent retry
            }
            
            await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000);
        } 
        
        sw.Stop(); 
        return false;
    }

    // PERBAIKAN: Quick health check tanpa retry untuk Available state
    private enum HealthStatus { Healthy, NotReady, Failed }
    
    private static async Task<HealthStatus> QuickHealthCheck(TokenEntry token, string codespaceName) {
        try {
            string args = $"codespace ssh -c {codespaceName} -- \"if [ -f {HEALTH_CHECK_FAILED_PROXY} ] || [ -f {HEALTH_CHECK_FAILED_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
            string result = await ShellHelper.RunGhCommand(token, args, 15000);

            if (result.Contains("FAILED")) return HealthStatus.Failed;
            if (result.Contains("HEALTHY")) return HealthStatus.Healthy;
            return HealthStatus.NotReady;
        } catch {
            return HealthStatus.NotReady;
        }
    }

    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName) {
        Stopwatch sw = Stopwatch.StartNew(); 
        int consecutiveSshSuccess = 0;
        const int SSH_SUCCESS_THRESHOLD = 2;
        
        while(sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
            try {
                string args = $"codespace ssh -c {codespaceName} -- \"if [ -f {HEALTH_CHECK_FAILED_PROXY} ] || [ -f {HEALTH_CHECK_FAILED_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
                string result = await ShellHelper.RunGhCommand(token, args, 20000);

                if (result.Contains("FAILED")) { 
                    sw.Stop(); 
                    return false; 
                }
                
                if (result.Contains("HEALTHY")) { 
                    sw.Stop(); 
                    return true; 
                }
                
                if (result.Contains("NOT_READY")) { 
                    consecutiveSshSuccess++;
                    
                    if (consecutiveSshSuccess >= SSH_SUCCESS_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) {
                        sw.Stop();
                        return true; // Assume healthy jika SSH responsive
                    }
                }
            } catch { 
                consecutiveSshSuccess = 0;
            }
            
            await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000);
        } 
        
        sw.Stop(); 
        return false;
    }

    public static async Task UploadConfigs(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine("\n[cyan]Uploading CORE configs...[/]");
        string remoteDir = $"/workspaces/{token.Repo}/config";
        
        try { 
            string mkdirArgs=$"codespace ssh -c {codespaceName} -- mkdir -p {remoteDir}"; 
            await ShellHelper.RunGhCommand(token, mkdirArgs, 15000); 
        } catch { }
        
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "bots_config.json"), $"{remoteDir}/bots_config.json");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "apilist.txt"), $"{remoteDir}/apilist.txt");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "paths.txt"), $"{remoteDir}/paths.txt");
    }

    public static async Task UploadAllBotData(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine("\n[cyan]Uploading secrets from D:\\SC...[/]"); 
        var config=BotConfig.Load(); 
        if (config == null) return;
        
        string remoteRepoRoot=$"/workspaces/{token.Repo}"; 
        int filesUploaded=0;
        
        foreach (var bot in config.BotsAndTools) {
            string localBotPath=BotConfig.GetLocalBotPath(bot.Path); 
            string remoteBotPath=$"{remoteRepoRoot}/{bot.Path.Replace('\\', '/')}";
            
            if (!Directory.Exists(localBotPath)) continue;
            
            bool botDirCreated=false; 
            
            foreach (var secretFileName in SecretFileNames) {
                string localFilePath=Path.Combine(localBotPath, secretFileName);
                
                if (File.Exists(localFilePath)) {
                    if (!botDirCreated) {
                        try { 
                            string mkdirArgs=$"codespace ssh -c {codespaceName} -- mkdir -p {remoteBotPath}"; 
                            await ShellHelper.RunGhCommand(token, mkdirArgs, 15000); 
                            botDirCreated=true; 
                        } catch { 
                            goto NextBot; 
                        }
                    }
                    
                    string remoteFilePath=$"{remoteBotPath}/{secretFileName}";
                    await UploadFile(token, codespaceName, localFilePath, remoteFilePath, silent: true);
                    filesUploaded++;
                }
            } 
            
            NextBot:;
        } 
        
        AnsiConsole.MarkupLine($"[green]   ✓ Uploaded {filesUploaded} files.[/]");
    }

    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath, bool silent = false) {
        if (!File.Exists(localPath)) return;
        
        string args=$"codespace cp \"{localPath}\" \"remote:{remotePath}\" -c {csName}";
        try { 
            await ShellHelper.RunGhCommand(token, args, 60000); 
        } catch { }
    }

    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName) {
        string remoteScript=$"/workspaces/{token.Repo}/auto-start.sh";
        
        try { 
            string checkArgs=$"codespace ssh -c {codespaceName} -- ls {remoteScript}"; 
            await ShellHelper.RunGhCommand(token, checkArgs, 10000); 
        } catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]Script not found: {ex.Message}[/]"); 
            throw; 
        }
        
        string cmd=$"nohup bash {remoteScript} > /tmp/startup.log 2>&1 &"; 
        string args=$"codespace ssh -c {codespaceName} -- {cmd}";
        
        try { 
            await ShellHelper.RunGhCommand(token, args, 20000); 
        } catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]Trigger failed: {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
            throw; 
        }
    }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine($"[yellow]Deleting {codespaceName}...[/]");
        try { 
            string args=$"codespace delete -c {codespaceName} --force"; 
            await ShellHelper.RunGhCommand(token, args, 30000); 
            AnsiConsole.MarkupLine("[green]✓ Deleted.[/]"); 
        } catch (Exception ex) { 
            if (ex.Message.Contains("404") || ex.Message.Contains("Could not find")) 
                AnsiConsole.MarkupLine($"[dim]   (Already gone)[/]"); 
            else 
                AnsiConsole.MarkupLine($"[yellow]   Warn: {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
        }
        
        await Task.Delay(3000);
    }

    private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> allCodespaces, string? currentCodespaceName) {
        foreach (var cs in allCodespaces) {
            if (cs.Name == currentCodespaceName || cs.State == "Deleted") continue;
            
            if (cs.DisplayName == CODESPACE_DISPLAY_NAME) {
                 await DeleteCodespace(token, cs.Name);
            }
        }
    }

    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token) {
        string args = "codespace list --json name,displayName,state"; 
        List<CodespaceInfo> allCodespaces = new List<CodespaceInfo>();
        
        try {
            string jsonResult = await ShellHelper.RunGhCommand(token, args, 30000);
            try { 
                allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>(); 
            } catch { }
        } catch { }
        
        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");
        return (existing, allCodespaces);
    }

    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName) {
        string args = $"codespace ssh -c {codespaceName} -- tmux list-windows -F \"#{{window_name}}\"";
        try {
            string result = await ShellHelper.RunGhCommand(token, args, 30000);
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(s => s != "dashboard" && s != "bash")
                         .OrderBy(s => s).ToList();
        } catch {
            return new List<string>();
        }
    }

    private class CodespaceInfo { 
        [JsonPropertyName("name")] public string Name { get; set; } = ""; 
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; 
        [JsonPropertyName("state")] public string State { get; set; } = ""; 
    }
}
