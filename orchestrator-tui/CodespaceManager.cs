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
    private const int SSH_COMMAND_TIMEOUT_MS = 120000;
    private const int CREATE_TIMEOUT_MS = 900000;
    private const int START_TIMEOUT_MS = 600000;
    private const int STATE_POLL_INTERVAL_SEC = 30;
    private const int STATE_POLL_MAX_DURATION_MIN = 15;
    private const int SSH_READY_POLL_INTERVAL_SEC = 20;
    private const int SSH_READY_MAX_DURATION_MIN = 10;
    private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 15;
    private const int HEALTH_CHECK_MAX_DURATION_MIN = 10;
    private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";

    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");

    private static readonly string[] SecretFileNames = {
        ".env", "pk.txt", "privatekey.txt", "wallet.txt", "token.txt",
        "data.json", "config.json", "settings.json"
    };

    private static string GetProjectRoot() {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null) {
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
        Stopwatch stopwatch = Stopwatch.StartNew(); // Timer untuk state polling

        // Loop untuk handle state transisi
        while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN)
        {
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}): Finding codespace...[/]");
            var (found, all) = await FindExistingCodespace(token);
            codespace = found;

            if (codespace == null) {
                AnsiConsole.MarkupLine("\n[yellow]No existing runner found.[/]");
                await CleanupStuckCodespaces(token, all, null);
                return await CreateNewCodespace(token, repoFullName);
            }

            AnsiConsole.MarkupLine($"\n[green]✓ Found:[/] [dim]{codespace.Name} (State: {codespace.State})[/]");

            switch (codespace.State) {
                case "Available":
                    AnsiConsole.MarkupLine("[green]  State 'Available'. Checking health...[/]");
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                        AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing.[/]");
                        stopwatch.Stop(); return codespace.Name;
                    }
                    AnsiConsole.MarkupLine("[red]  ✗ Health check FAILED. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null; // Tandai untuk create baru
                    break; // Lanjut loop (akan create baru jika timeout)

                case "Stopped":
                case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Starting...[/]");
                    await StartCodespace(token, codespace.Name); // Start + Wait SSH Ready
                    AnsiConsole.MarkupLine("[green]  ✓ Codespace Started. Verifying health...[/]");
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                         AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing.[/]");
                         stopwatch.Stop(); return codespace.Name;
                    }
                     AnsiConsole.MarkupLine("[red]  ✗ Health check FAILED after start. Deleting...[/]");
                     await DeleteCodespace(token, codespace.Name);
                     codespace = null;
                     break; // Lanjut loop

                case "Provisioning": case "Creating": case "Starting":
                case "Queued": case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Waiting ({STATE_POLL_INTERVAL_SEC}s)...[/]");
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000);
                    // Langsung lanjut ke iterasi berikutnya
                    continue; // Skip break bawah, langsung loop lagi

                default: // Error, Failed, Unknown
                    AnsiConsole.MarkupLine($"[red]  State '{codespace.State}' indicates error. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null;
                    break; // Lanjut loop
            }

            // Jika codespace dihapus, tunggu sebentar sebelum retry find
             if (codespace == null && stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                 AnsiConsole.MarkupLine($"[dim]Retrying find/create after state issue...[/]");
                 await Task.Delay(5000);
             }
        } // End While Loop

        stopwatch.Stop();
        // Jika timeout tapi codespace ada (misal stuck 'Starting')
        if (codespace != null) {
             AnsiConsole.MarkupLine($"\n[red]FATAL: Codespace '{codespace.Name}' stuck in state '{codespace.State}' after {STATE_POLL_MAX_DURATION_MIN} minutes. Deleting...[/]");
             await DeleteCodespace(token, codespace.Name);
             codespace = null; // Pastikan create baru
        } else {
             AnsiConsole.MarkupLine($"\n[red]FATAL: Failed to get codespace to Available state after {STATE_POLL_MAX_DURATION_MIN} minutes.[/]");
        }

        // Coba create terakhir kali
        AnsiConsole.MarkupLine("[yellow]Attempting to create a new codespace as a last resort...[/]");
        var (_, allFinal) = await FindExistingCodespace(token);
        await CleanupStuckCodespaces(token, allFinal, null);
        return await CreateNewCodespace(token, repoFullName); // Jika ini gagal, akan throw exception
    }


    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName)
    {
        AnsiConsole.MarkupLine($"\n[cyan]Creating new '{CODESPACE_DISPLAY_NAME}' ({MACHINE_TYPE})...[/]");
        AnsiConsole.MarkupLine($"[dim]Max creation time: {CREATE_TIMEOUT_MS / 60000} mins.[/]");

        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        var newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS);

        if (string.IsNullOrWhiteSpace(newName)) {
            throw new Exception("Failed to create codespace (no name returned, check GitHub quota/limits)");
        }
        AnsiConsole.MarkupLine($"[green]✓ Created: {newName}[/]");
        await Task.Delay(5000);

        if (!await WaitForSshReadyWithRetry(token, newName)) {
            AnsiConsole.MarkupLine($"[red]SSH failed for new codespace '{newName}'. Attempting cleanup...[/]");
            await DeleteCodespace(token, newName);
            throw new Exception($"Codespace '{newName}' created but SSH never became ready");
        }

        AnsiConsole.MarkupLine("[cyan]Uploading core configs...[/]");
        await UploadConfigs(token, newName);
        AnsiConsole.MarkupLine("[cyan]Uploading all bot data (pk.txt, .env, etc)...[/]");
        await UploadAllBotData(token, newName);

        // Setelah upload, tunggu health check (auto-start.sh selesai)
        AnsiConsole.MarkupLine("[cyan]Waiting for initial setup (auto-start.sh) to complete...[/]");
        if (!await CheckHealthWithRetry(token, newName)) {
            AnsiConsole.MarkupLine($"[red]Initial setup failed for '{newName}'. Health check timed out. Manual check needed.[/]");
            // JANGAN throw exception, mungkin bot jalan sebagian. Return nama CS.
        }

        return newName;
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName)
    {
        string args = $"codespace start -c {codespaceName}";
        AnsiConsole.MarkupLine($"[dim]  Executing: gh {args}[/]");
        try {
            await ShellHelper.RunGhCommand(token, args, START_TIMEOUT_MS);
        } catch (Exception ex) {
             // Error "already available" itu normal, abaikan. Error lain log sebagai warning.
             if(!ex.Message.Contains("is already available")) {
                 AnsiConsole.MarkupLine($"[yellow]  Warning during start: {ex.Message.Split('\n').FirstOrDefault()}. Checking SSH anyway...[/]");
             } else {
                 AnsiConsole.MarkupLine($"[dim]  Codespace already available.[/]");
             }
        }

        // Setelah start, WAJIB tunggu SSH ready
        if (!await WaitForSshReadyWithRetry(token, codespaceName)) {
            throw new Exception($"Failed to start or SSH into {codespaceName} after attempting start.");
        }
    }


    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Waiting for SSH readiness on '{codespaceName}' (max {SSH_READY_MAX_DURATION_MIN} mins)...[/]");

        while(stopwatch.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN)
        {
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) SSH Check... [/]");
            try
            {
                string args = $"codespace ssh -c {codespaceName} -- echo ready";
                string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS);

                if (result.Contains("ready")) {
                    AnsiConsole.MarkupLine("[green]SSH is ready[/]");
                    stopwatch.Stop(); return true;
                }
                AnsiConsole.MarkupLine($"[yellow]Unexpected output: '{result}'. Retrying...[/]");
            }
            catch (TaskCanceledException) {
                AnsiConsole.MarkupLine($"[red]TIMEOUT ({SSH_COMMAND_TIMEOUT_MS / 1000}s). Retrying...[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}. Retrying...[/]");
                 if (ex.Message.Contains("Connection refused") || ex.Message.Contains("process exited")) {
                     await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000 / 2); // Tunggu lebih cepat jika ini
                }
            }

            AnsiConsole.MarkupLine($"[dim]  Waiting {SSH_READY_POLL_INTERVAL_SEC}s...[/]");
            await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000);
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine($"[red]Timeout: SSH did not become ready after {SSH_READY_MAX_DURATION_MIN} minutes.[/]");
        return false;
    }

    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName) {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Waiting for health check ({HEALTH_CHECK_FILE}) on '{codespaceName}' (max {HEALTH_CHECK_MAX_DURATION_MIN} mins)...[/]");

        while(stopwatch.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN)
        {
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Health Check... [/]");
            try
            {
                string args = $"codespace ssh -c {codespaceName} -- ls {HEALTH_CHECK_FILE} 2>/dev/null && echo HEALTHY || echo NOT_READY";
                // Timeout pendek untuk check file
                string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS / 3);

                if (result.Contains("HEALTHY")) {
                    AnsiConsole.MarkupLine("[green]Healthy (auto-start complete)[/]");
                    stopwatch.Stop(); return true;
                } else if (result.Contains("NOT_READY")) {
                    AnsiConsole.MarkupLine("[yellow]Not healthy yet (auto-start running/failed?).[/]");
                } else {
                    AnsiConsole.MarkupLine($"[yellow]Unexpected response: {result}. Retrying...[/]");
                }
            }
            catch (TaskCanceledException) {
                 AnsiConsole.MarkupLine($"[red]TIMEOUT. Retrying...[/]");
            }
            catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}. Retrying...[/]");
            }

            AnsiConsole.MarkupLine($"[dim]  Waiting {HEALTH_CHECK_POLL_INTERVAL_SEC}s...[/]");
            await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000);
        }
        stopwatch.Stop();
        AnsiConsole.MarkupLine($"[red]Health Check Failed after {HEALTH_CHECK_MAX_DURATION_MIN} minutes.[/]");
        return false;
    }

    public static async Task UploadConfigs(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine("\n[cyan]Uploading CORE configs (bots_config.json etc.)...[/]");
        string remoteDir = $"/workspaces/{token.Repo}/config";
        AnsiConsole.Markup("[dim]  Ensuring remote config directory... [/]");
        try { string mkdirArgs = $"codespace ssh -c {codespaceName} -- mkdir -p {remoteDir}"; await ShellHelper.RunGhCommand(token, mkdirArgs); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn (mkdir): {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "bots_config.json"), $"{remoteDir}/bots_config.json");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "apilist.txt"), $"{remoteDir}/apilist.txt");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "paths.txt"), $"{remoteDir}/paths.txt");
    }

    public static async Task UploadAllBotData(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine("\n[cyan]Uploading secrets (pk.txt, .env etc) from D:\\SC...[/]");
        var config = BotConfig.Load(); if (config == null) return;
        string remoteRepoRoot = $"/workspaces/{token.Repo}"; int filesUploaded = 0, botsSkipped = 0;
        foreach (var bot in config.BotsAndTools) {
            string localBotPath = BotConfig.GetLocalBotPath(bot.Path); string remoteBotPath = $"{remoteRepoRoot}/{bot.Path.Replace('\\', '/')}";
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

    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath, bool silent = false) {
        if (!File.Exists(localPath)) { if (!silent) AnsiConsole.MarkupLine($"[yellow]SKIP: {Path.GetFileName(localPath)}[/]"); return; }
        if (!silent) AnsiConsole.Markup($"[dim]  Uploading {Path.GetFileName(localPath)}... [/]");
        string args = $"codespace cp \"{localPath}\" \"remote:{remotePath}\" -c {csName}";
        try { await ShellHelper.RunGhCommand(token, args); if (!silent) AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { if (!silent) AnsiConsole.MarkupLine($"[red]FAIL[/]"); AnsiConsole.MarkupLine($"[red]    Error: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
    }

    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine("\n[cyan]Triggering remote startup script (auto-start.sh)...[/]");
        string remoteScript = $"/workspaces/{token.Repo}/auto-start.sh";
        AnsiConsole.Markup("[dim]  Verifying script... [/]");
        try { string checkArgs = $"codespace ssh -c {codespaceName} -- ls {remoteScript} 2>/dev/null && echo EXISTS || echo MISSING"; string checkResult = await ShellHelper.RunGhCommand(token, checkArgs); if (!checkResult.Contains("EXISTS")) throw new Exception("Script not found"); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message}[/]"); throw; }
        AnsiConsole.Markup("[dim]  Executing (detached)... [/]");
        string cmd = $"nohup bash {remoteScript} > /tmp/startup.log 2>&1 &"; string args = $"codespace ssh -c {codespaceName} -- {cmd}";
        try { await ShellHelper.RunGhCommand(token, args); AnsiConsole.MarkupLine("[green]OK[/]"); AnsiConsole.MarkupLine($"[dim]   Monitor via Menu 4 or 'tail -f /tmp/startup.log'[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}[/]"); throw; }
    }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine($"[yellow]Deleting codespace: {codespaceName}...[/]");
        try { string args = $"codespace delete -c {codespaceName} --force"; await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]✓ Deleted.[/]"); }
        catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Fail del: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            if (ex.Message.Contains("404") || ex.Message.Contains("Could not find")) AnsiConsole.MarkupLine($"[dim]   (Already gone)[/]");
            else AnsiConsole.MarkupLine($"[yellow]   Manual check needed.[/]");
        } await Task.Delay(3000);
    }

    private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> allCodespaces, string? currentCodespaceName) {
        AnsiConsole.MarkupLine("[dim]Cleaning up other potential stuck runners...[/]"); int cleaned = 0;
        foreach (var cs in allCodespaces) {
            if (cs.Name == currentCodespaceName || cs.State == "Deleted") continue;
            if (cs.DisplayName == CODESPACE_DISPLAY_NAME) {
                 AnsiConsole.MarkupLine($"[yellow]   Found stuck: {cs.Name} ({cs.State}). Deleting...[/]");
                 await DeleteCodespace(token, cs.Name); cleaned++;
            }
        } if (cleaned == 0) AnsiConsole.MarkupLine("[dim]   None found.[/]");
    }

    // === PERBAIKAN: Gunakan FIELD yang BENAR untuk `gh codespace list --json` ===
    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token)
    {
        // Field yang dibutuhkan: name, displayName, state
        string args = "codespace list --json name,displayName,state";
        string jsonResult = "";
        try {
            jsonResult = await ShellHelper.RunGhCommand(token, args);
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error listing codespaces: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            // Jika error karena --json butuh field, ini akan gagal di sini
            if (ex.Message.Contains("Specify one or more comma-separated fields")) {
                 AnsiConsole.MarkupLine("[red]   >>> GH CLI version might be outdated or command changed? Update GH CLI. <<<[/]");
            }
            return (null, new List<CodespaceInfo>());
        }

        List<CodespaceInfo> allCodespaces = new List<CodespaceInfo>();
        try {
            // Hasil dari --json field1,field2,... adalah array JSON
            allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? new List<CodespaceInfo>();
        } catch (JsonException ex) {
             AnsiConsole.MarkupLine($"[red]Error parsing codespace list JSON: {ex.Message}[/]");
             AnsiConsole.MarkupLine($"[dim]Raw JSON: {jsonResult}[/]"); // Tampilkan JSON mentah untuk debug
             return (null, new List<CodespaceInfo>());
        }

        // Cari yang display name cocok DAN state BUKAN "Deleted"
        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");

        return (existing, allCodespaces);
    }
    // === AKHIR PERBAIKAN JSON FIELD ===


    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine($"[dim]Fetching running bots from {codespaceName}...[/]");
        string args = $"codespace ssh -c {codespaceName} -- tmux list-windows -F \"#{{window_name}}\"";
        try { string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "dashboard" && s != "bash").OrderBy(s => s).ToList(); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Failed get tmux: {ex.Message.Split('\n').FirstOrDefault()}[/]"); if (ex.Message.Contains("No sessions")) AnsiConsole.MarkupLine("[yellow]   Tmux hasn't started?[/]"); return new List<string>(); }
    }

    private class CodespaceInfo {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        // machineName dihapus karena tidak diminta di --json
        // [JsonPropertyName("machineName")] public string MachineName { get; set; } = "";
    }
}
