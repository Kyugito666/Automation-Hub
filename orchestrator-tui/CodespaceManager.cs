using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Diagnostics; // Untuk Stopwatch

namespace Orchestrator;

public static class CodespaceManager
{
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";

    // Timeout & Retry Config
    private const int SSH_COMMAND_TIMEOUT_MS = 120000; // 2 menit per SSH command
    private const int CREATE_TIMEOUT_MS = 900000;    // 15 menit max create time (gh command)
    private const int START_TIMEOUT_MS = 600000;     // 10 menit max start time (gh command)

    private const int STATE_POLL_INTERVAL_SEC = 30; // Jeda polling state
    private const int STATE_POLL_MAX_DURATION_MIN = 20; // TOTAL max waktu nunggu state (Create/Start)

    // === KONSTANTA YANG HILANG DITAMBAHKAN KEMBALI ===
    private const int SSH_READY_POLL_INTERVAL_SEC = 20; // Jeda antar cek SSH ready
    private const int SSH_READY_MAX_DURATION_MIN = 10; // Max total waktu nunggu SSH ready
    // === AKHIR PENAMBAHAN ===

    // Timeout untuk SSH check *selama* provisioning/starting
    private const int SSH_PROBE_TIMEOUT_MS = 30000; // 30 detik cukup untuk 'echo test'
    private const int SSH_PROBE_FAIL_THRESHOLD = 6; // Anggap stuck jika SSH gagal 6x berturut2 (~3 menit)

    // Timeout untuk health check (nunggu auto-start.sh)
    private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 15;
    private const int HEALTH_CHECK_MAX_DURATION_MIN = 15; // Max total waktu nunggu auto-start.sh selesai
    private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";

    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    private static readonly string[] SecretFileNames = {
        ".env", "pk.txt", "privatekey.txt", "wallet.txt", "token.txt",
        "data.json", "config.json", "settings.json" // Tambahkan config/settings jika perlu
    };

    private static string GetProjectRoot() {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null) {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            if (Directory.Exists(configDir) && File.Exists(gitignore)) return currentDir.FullName;
            currentDir = currentDir.Parent;
        }
        // Fallback jika tidak ketemu (sebaiknya tidak terjadi)
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", ".."));
     }

    public static async Task<string> EnsureHealthyCodespace(TokenEntry token)
    {
        AnsiConsole.MarkupLine("\n[cyan]Ensuring Codespace Runner...[/]");
        string repoFullName = $"{token.Owner}/{token.Repo}";
        CodespaceInfo? codespace = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int consecutiveSshFailures = 0;

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
                    AnsiConsole.MarkupLine("[green]  State 'Available'. Running final health check...[/]");
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                        AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing.[/]");
                        stopwatch.Stop(); return codespace.Name;
                    }
                    AnsiConsole.MarkupLine($"[red]  ✗ State 'Available' but health check FAILED. Deleting broken codespace...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null;
                    break;

                case "Stopped":
                case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Starting...[/]");
                    await StartCodespace(token, codespace.Name);
                    AnsiConsole.MarkupLine("[green]  ✓ Codespace Started. Verifying health...[/]");
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                         AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing.[/]");
                         stopwatch.Stop(); return codespace.Name;
                    }
                     AnsiConsole.MarkupLine("[red]  ✗ Health check FAILED after start. Deleting...[/]");
                     await DeleteCodespace(token, codespace.Name);
                     codespace = null;
                     break;

                case "Provisioning": case "Creating": case "Starting":
                case "Queued": case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Probing SSH while waiting...[/]");
                    if (await ProbeSsh(token, codespace.Name)) {
                        AnsiConsole.MarkupLine("[cyan]    SSH Probe OK. Continuing wait for 'Available' state.[/]");
                        consecutiveSshFailures = 0;
                    } else {
                        consecutiveSshFailures++;
                        AnsiConsole.MarkupLine($"[yellow]    SSH Probe Failed ({consecutiveSshFailures}/{SSH_PROBE_FAIL_THRESHOLD}). Waiting...[/]");
                        if (consecutiveSshFailures >= SSH_PROBE_FAIL_THRESHOLD) {
                            AnsiConsole.MarkupLine($"[red]  ✗ SSH probes failed {SSH_PROBE_FAIL_THRESHOLD} times. Assuming stuck. Deleting...[/]");
                            await DeleteCodespace(token, codespace.Name);
                            codespace = null;
                            break;
                        }
                    }
                    AnsiConsole.MarkupLine($"[dim]  Waiting {STATE_POLL_INTERVAL_SEC}s for next state check...[/]");
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000);
                    continue; // Langsung ke iterasi berikutnya

                default: // Error, Failed, Unknown
                    AnsiConsole.MarkupLine($"[red]  State '{codespace.State}' indicates error. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null;
                    break;
            } // End Switch

             if (codespace == null && stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                 AnsiConsole.MarkupLine($"[dim]Retrying find/create after state issue...[/]");
                 await Task.Delay(5000);
             }
        } // End While Loop

        stopwatch.Stop();
        if (codespace != null) {
             AnsiConsole.MarkupLine($"\n[red]FATAL: Codespace '{codespace.Name}' stuck in state '{codespace.State}' after {STATE_POLL_MAX_DURATION_MIN} mins. Deleting...[/]");
             await DeleteCodespace(token, codespace.Name);
        } else {
             AnsiConsole.MarkupLine($"\n[red]FATAL: Failed to get codespace to Available state after {STATE_POLL_MAX_DURATION_MIN} mins.[/]");
        }

        AnsiConsole.MarkupLine("[yellow]Attempting final create...[/]");
        var (_, allFinal) = await FindExistingCodespace(token);
        await CleanupStuckCodespaces(token, allFinal, null);
        try {
            return await CreateNewCodespace(token, repoFullName);
        } catch (Exception createEx) {
            AnsiConsole.WriteException(createEx);
            throw new Exception($"FATAL: Final create failed. Check quota/status. Last error: {createEx.Message}");
        }
    }


    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName) {
        AnsiConsole.MarkupLine($"\n[cyan]Creating new '{CODESPACE_DISPLAY_NAME}' ({MACHINE_TYPE})...[/]");
        AnsiConsole.MarkupLine($"[dim]Max gh create time: {CREATE_TIMEOUT_MS / 60000} mins.[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        Stopwatch createStopwatch = Stopwatch.StartNew();
        string newName = "";
        try {
            newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS);
            createStopwatch.Stop();
             if (string.IsNullOrWhiteSpace(newName)) throw new Exception("gh create returned empty name");
             AnsiConsole.MarkupLine($"[green]✓ Created: {newName} (in {createStopwatch.Elapsed:mm\\:ss})[/]");
             await Task.Delay(5000);

             if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(STATE_POLL_MAX_DURATION_MIN - 2))) { // Sisa waktu poll
                 throw new Exception($"Did not reach 'Available' state within timeout.");
             }
            if (!await WaitForSshReadyWithRetry(token, newName)) { throw new Exception($"SSH failed for new codespace"); }
            await UploadConfigs(token, newName); await UploadAllBotData(token, newName);
             AnsiConsole.MarkupLine("[cyan]Waiting for initial setup (auto-start.sh)...[/]");
             if (!await CheckHealthWithRetry(token, newName)) { AnsiConsole.MarkupLine($"[red]Initial setup failed (health check timed out). Manual check needed.[/]"); }

             return newName;

        } catch (Exception ex) {
            createStopwatch.Stop(); AnsiConsole.WriteException(ex);
            if (!string.IsNullOrWhiteSpace(newName)) { await DeleteCodespace(token, newName); }
            throw new Exception($"FATAL: Failed during creation. Error: {ex.Message}");
        }
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName) {
        string args = $"codespace start -c {codespaceName}";
        AnsiConsole.MarkupLine($"[dim]  Executing: gh {args}[/]");
        Stopwatch startStopwatch = Stopwatch.StartNew();
        try { await ShellHelper.RunGhCommand(token, args, START_TIMEOUT_MS); }
        catch (Exception ex) { if(!ex.Message.Contains("is already available")) AnsiConsole.MarkupLine($"[yellow]  Warn (start): {ex.Message.Split('\n').FirstOrDefault()}. Checking state/SSH...[/]"); else AnsiConsole.MarkupLine($"[dim]  Already available.[/]"); }
        startStopwatch.Stop();
        AnsiConsole.MarkupLine($"[dim]  'gh start' finished in {startStopwatch.Elapsed:mm\\:ss}. Waiting for 'Available' state & SSH...[/]");

        if (!await WaitForState(token, codespaceName, "Available", TimeSpan.FromMinutes(STATE_POLL_MAX_DURATION_MIN / 2))) {
            throw new Exception($"Did not reach 'Available' state after starting.");
        }
        if (!await WaitForSshReadyWithRetry(token, codespaceName)) { throw new Exception($"Failed SSH after starting."); }
    }

    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout) {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Waiting for '{codespaceName}' state '{targetState}' (max {timeout.TotalMinutes:F1} mins)...[/]");
        while(stopwatch.Elapsed < timeout) {
             AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Checking state... [/]");
             var state = await GetCodespaceState(token, codespaceName);
             if (state == targetState) { AnsiConsole.MarkupLine($"[green]State '{targetState}'[/]"); stopwatch.Stop(); return true; }
             if (state == null) { AnsiConsole.MarkupLine($"[red]CS lost?[/]"); stopwatch.Stop(); return false; }
             if (state == "Failed" || state == "Error" || state.Contains("ShuttingDown") || state=="Deleted") { AnsiConsole.MarkupLine($"[red]Error/Shutdown state: '{state}'. Abort.[/]"); stopwatch.Stop(); return false; }
             AnsiConsole.MarkupLine($"[yellow]State '{state}'. Wait {STATE_POLL_INTERVAL_SEC}s...[/]"); await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000);
        }
        stopwatch.Stop(); AnsiConsole.MarkupLine($"[red]Timeout: No '{targetState}' after {timeout.TotalMinutes:F1} mins.[/]"); return false;
    }

     private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName) {
         string args = $"codespace view --json state -c {codespaceName}";
         try { string jsonResult = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); using var jsonDoc = JsonDocument.Parse(jsonResult); return jsonDoc.RootElement.GetProperty("state").GetString(); }
         catch (Exception ex) { if (ex.Message.Contains("404") || ex.Message.Contains("Could not find")) { AnsiConsole.MarkupLine($"[yellow](GetState) CS '{codespaceName}' not found.[/]"); return null; } AnsiConsole.MarkupLine($"[red](GetState) Err: {ex.Message.Split('\n').FirstOrDefault()}[/]"); return null; }
     }

    private static async Task<bool> ProbeSsh(TokenEntry token, string codespaceName) {
        AnsiConsole.Markup($"[dim]    SSH Probe... [/]");
        try { string args = $"codespace ssh -c {codespaceName} -- echo probe_ok"; string result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); return result.Contains("probe_ok"); }
        catch (TaskCanceledException) { AnsiConsole.Markup($"[yellow]Timeout ({SSH_PROBE_TIMEOUT_MS/1000}s)[/]"); return false; }
        catch (Exception) { AnsiConsole.Markup($"[red]Fail[/]"); return false; }
    }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName) {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Waiting SSH ready '{codespaceName}' (max {SSH_READY_MAX_DURATION_MIN} mins)...[/]");
        while(stopwatch.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) SSH Check... [/]");
            try { string args = $"codespace ssh -c {codespaceName} -- echo ready"; string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); if (result.Contains("ready")) { AnsiConsole.MarkupLine("[green]SSH Ready[/]"); stopwatch.Stop(); return true; } AnsiConsole.MarkupLine($"[yellow]Output: '{result}'. Retry...[/]"); }
            catch (TaskCanceledException) { AnsiConsole.MarkupLine($"[red]TIMEOUT ({SSH_COMMAND_TIMEOUT_MS / 1000}s). Retry...[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}. Retry...[/]"); if (ex.Message.Contains("refused") || ex.Message.Contains("exited")) await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000 / 2); }
            AnsiConsole.MarkupLine($"[dim]  Wait {SSH_READY_POLL_INTERVAL_SEC}s...[/]"); await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000);
        }
        stopwatch.Stop(); AnsiConsole.MarkupLine($"[red]Timeout: SSH fail after {SSH_READY_MAX_DURATION_MIN} mins.[/]"); return false;
    }

    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName) {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Waiting health check ({HEALTH_CHECK_FILE}) on '{codespaceName}' (max {HEALTH_CHECK_MAX_DURATION_MIN} mins)...[/]");
        while(stopwatch.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Health Check... [/]");
            try { string args = $"codespace ssh -c {codespaceName} -- ls {HEALTH_CHECK_FILE} 2>/dev/null && echo HEALTHY || echo NOT_READY"; string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS / 3); if (result.Contains("HEALTHY")) { AnsiConsole.MarkupLine("[green]Healthy[/]"); stopwatch.Stop(); return true; } else if (result.Contains("NOT_READY")) { AnsiConsole.MarkupLine("[yellow]Not healthy yet.[/]"); } else { AnsiConsole.MarkupLine($"[yellow]Resp: {result}. Retry...[/]"); } }
            catch (TaskCanceledException) { AnsiConsole.MarkupLine($"[red]TIMEOUT. Retry...[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}. Retry...[/]"); }
            AnsiConsole.MarkupLine($"[dim]  Wait {HEALTH_CHECK_POLL_INTERVAL_SEC}s...[/]"); await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000);
        }
        stopwatch.Stop(); AnsiConsole.MarkupLine($"[red]Health Check Failed after {HEALTH_CHECK_MAX_DURATION_MIN} mins.[/]"); return false;
    }

    public static async Task UploadConfigs(TokenEntry token, string codespaceName) { AnsiConsole.MarkupLine("\n[cyan]Uploading CORE configs...[/]"); string remoteDir=$"/workspaces/{token.Repo}/config"; AnsiConsole.Markup("[dim]  Ensure remote dir... [/]"); try { string mkdirArgs=$"codespace ssh -c {codespaceName} -- mkdir -p {remoteDir}"; await ShellHelper.RunGhCommand(token, mkdirArgs); AnsiConsole.MarkupLine("[green]OK[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn (mkdir): {ex.Message.Split('\n').FirstOrDefault()}[/]"); } await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "bots_config.json"), $"{remoteDir}/bots_config.json"); await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "apilist.txt"), $"{remoteDir}/apilist.txt"); await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "paths.txt"), $"{remoteDir}/paths.txt"); }
    public static async Task UploadAllBotData(TokenEntry token, string codespaceName) { AnsiConsole.MarkupLine("\n[cyan]Uploading secrets from D:\\SC...[/]"); var config=BotConfig.Load(); if (config == null) return; string remoteRepoRoot=$"/workspaces/{token.Repo}"; int filesUploaded=0, botsSkipped=0; foreach (var bot in config.BotsAndTools) { string localBotPath=BotConfig.GetLocalBotPath(bot.Path); string remoteBotPath=$"{remoteRepoRoot}/{bot.Path.Replace('\\', '/')}"; if (!Directory.Exists(localBotPath)) { botsSkipped++; continue; } AnsiConsole.MarkupLine($"[dim]   Scan {bot.Name}...[/]"); bool botDirCreated=false; int filesForThisBot=0; foreach (var secretFileName in SecretFileNames) { string localFilePath=Path.Combine(localBotPath, secretFileName); if (File.Exists(localFilePath)) { if (!botDirCreated) { AnsiConsole.Markup($"[dim]     Create remote dir... [/]"); try { string mkdirArgs=$"codespace ssh -c {codespaceName} -- mkdir -p {remoteBotPath}"; await ShellHelper.RunGhCommand(token, mkdirArgs); AnsiConsole.MarkupLine("[green]OK[/]"); botDirCreated=true; } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL ({ex.Message.Split('\n').FirstOrDefault()})[/]"); goto NextBot; } } string remoteFilePath=$"{remoteBotPath}/{secretFileName}"; await UploadFile(token, codespaceName, localFilePath, remoteFilePath, silent: true); AnsiConsole.MarkupLine($"[green]     ✓ Up {secretFileName}[/]"); filesUploaded++; filesForThisBot++; } } if (filesForThisBot == 0) AnsiConsole.MarkupLine($"[dim]     No secrets.[/]"); NextBot:; } AnsiConsole.MarkupLine($"[green]   ✓ Up complete ({filesUploaded} files).[/]"); }
    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath, bool silent = false) { if (!File.Exists(localPath)) { if (!silent) AnsiConsole.MarkupLine($"[yellow]SKIP: {Path.GetFileName(localPath)}[/]"); return; } if (!silent) AnsiConsole.Markup($"[dim]  Up {Path.GetFileName(localPath)}... [/]"); string args=$"codespace cp \"{localPath}\" \"remote:{remotePath}\" -c {csName}"; try { await ShellHelper.RunGhCommand(token, args); if (!silent) AnsiConsole.MarkupLine("[green]OK[/]"); } catch (Exception ex) { if (!silent) AnsiConsole.MarkupLine($"[red]FAIL[/]"); AnsiConsole.MarkupLine($"[red]    Err: {ex.Message.Split('\n').FirstOrDefault()}[/]"); } }
    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName) { AnsiConsole.MarkupLine("\n[cyan]Triggering auto-start.sh...[/]"); string remoteScript=$"/workspaces/{token.Repo}/auto-start.sh"; AnsiConsole.Markup("[dim]  Verify... [/]"); try { string checkArgs=$"codespace ssh -c {codespaceName} -- ls {remoteScript} 2>/dev/null && echo EXISTS || echo MISSING"; string checkResult=await ShellHelper.RunGhCommand(token, checkArgs); if (!checkResult.Contains("EXISTS")) throw new Exception("Script not found"); AnsiConsole.MarkupLine("[green]OK[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message}[/]"); throw; } AnsiConsole.Markup("[dim]  Exec (detached)... [/]"); string cmd=$"nohup bash {remoteScript} > /tmp/startup.log 2>&1 &"; string args=$"codespace ssh -c {codespaceName} -- {cmd}"; try { await ShellHelper.RunGhCommand(token, args); AnsiConsole.MarkupLine("[green]OK[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}[/]"); throw; } }
    public static async Task DeleteCodespace(TokenEntry token, string codespaceName) { AnsiConsole.MarkupLine($"[yellow]Deleting {codespaceName}...[/]"); try { string args=$"codespace delete -c {codespaceName} --force"; await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]✓ Del.[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Fail del: {ex.Message.Split('\n').FirstOrDefault()}[/]"); if (ex.Message.Contains("404") || ex.Message.Contains("Could not find")) AnsiConsole.MarkupLine($"[dim]   (Gone)[/]"); else AnsiConsole.MarkupLine($"[yellow]   Manual check.[/]"); } await Task.Delay(3000); }
    private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> allCodespaces, string? currentCodespaceName) { AnsiConsole.MarkupLine("[dim]Cleaning stuck...[/]"); int cleaned=0; foreach (var cs in allCodespaces) { if (cs.Name == currentCodespaceName || cs.State == "Deleted") continue; if (cs.DisplayName == CODESPACE_DISPLAY_NAME) { AnsiConsole.MarkupLine($"[yellow]   Found stuck: {cs.Name} ({cs.State}). Deleting...[/]"); await DeleteCodespace(token, cs.Name); cleaned++; } } if (cleaned == 0) AnsiConsole.MarkupLine("[dim]   None.[/]"); }

    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token) {
        string args = "codespace list --json name,displayName,state"; string jsonResult = "";
        try { jsonResult = await ShellHelper.RunGhCommand(token, args); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err list CS: {ex.Message.Split('\n').FirstOrDefault()}[/]"); if (ex.Message.Contains("fields")) AnsiConsole.MarkupLine("[red]   >>> Update GH CLI? <<<[/]"); return (null, new List<CodespaceInfo>()); }
        List<CodespaceInfo> allCodespaces = new List<CodespaceInfo>();
        try { allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>(); }
        catch (JsonException ex) { AnsiConsole.MarkupLine($"[red]Err parse JSON: {ex.Message}[/]"); AnsiConsole.MarkupLine($"[dim]JSON: {jsonResult}[/]"); return (null, new List<CodespaceInfo>()); }
        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");
        return (existing, allCodespaces);
    }

    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName) { AnsiConsole.MarkupLine($"[dim]Fetching bots from {codespaceName}...[/]"); string args = $"codespace ssh -c {codespaceName} -- tmux list-windows -F \"#{{window_name}}\""; try { string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "dashboard" && s != "bash").OrderBy(s => s).ToList(); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Fail tmux: {ex.Message.Split('\n').FirstOrDefault()}[/]"); if (ex.Message.Contains("No sessions")) AnsiConsole.MarkupLine("[yellow]   Tmux down?[/]"); return new List<string>(); } }

    private class CodespaceInfo { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; [JsonPropertyName("state")] public string State { get; set; } = ""; }
}

