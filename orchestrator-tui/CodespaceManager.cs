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
    private const int STATE_POLL_MAX_DURATION_MIN = 20; // TOTAL max waktu nunggu state

    // === FIX CS0103: Definisikan konstanta yang hilang ===
    private const int SSH_READY_POLL_INTERVAL_SEC = 20; // Jeda antar cek SSH ready
    private const int SSH_READY_MAX_DURATION_MIN = 15; // Max total waktu nunggu SSH ready
    // === AKHIR FIX ===

    private const int SSH_PROBE_TIMEOUT_MS = 30000; // 30 detik cukup untuk 'echo test'
    // Kurangi threshold, mungkin 6 terlalu lama
    private const int SSH_PROBE_FAIL_THRESHOLD = 4; // Anggap stuck jika SSH gagal 4x berturut2 (~2 menit)

    // Timeout untuk health check (nunggu auto-start.sh RINGAN)
    private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 10;
    private const int HEALTH_CHECK_MAX_DURATION_MIN = 5;  // Max 5 menit
    private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
    private const string HEALTH_CHECK_FAILED_PROXY = "/tmp/auto_start_failed_proxysync";
    private const string HEALTH_CHECK_FAILED_DEPLOY = "/tmp/auto_start_failed_deploy";


    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    private static readonly string[] SecretFileNames = {
        ".env", "pk.txt", "privatekey.txt", "wallet.txt", "token.txt",
        "data.json", "config.json", "settings.json"
    };

    private static string GetProjectRoot() { /* ... fungsi tidak berubah ... */ return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", "..")); }

    // === FLOW UTAMA (Niru Nexus) ===
    public static async Task<string> EnsureHealthyCodespace(TokenEntry token)
    {
        AnsiConsole.MarkupLine("\n[cyan]Ensuring Codespace Runner...[/]");
        string repoFullName = $"{token.Owner}/{token.Repo}";
        CodespaceInfo? codespace = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        // === FIX CS0219: Hapus variabel tidak terpakai ===
        // bool needsInitialSetup = false;
        // === AKHIR FIX ===
        int consecutiveSshFailures = 0;

        while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN)
        {
            double elapsedMinutes = stopwatch.Elapsed.TotalMinutes;
            AnsiConsole.Markup($"[dim]({elapsedMinutes:F1}/{STATE_POLL_MAX_DURATION_MIN} min): Finding codespace...[/]");

            var (found, all) = await FindExistingCodespace(token);
            codespace = found;

            // --- 1. Handle Jika Tidak Ditemukan ---
            if (codespace == null) {
                AnsiConsole.MarkupLine("\n[yellow]Not found.[/]");
                await CleanupStuckCodespaces(token, all, null);
                return await CreateNewCodespace(token, repoFullName);
            }

            AnsiConsole.MarkupLine($"\n[green]✓ Found:[/] [dim]{codespace.Name} (State: {codespace.State})[/]");

            // --- 2. Handle State yang Ditemukan ---
            switch (codespace.State)
            {
                case "Available":
                    AnsiConsole.MarkupLine("[green]  State 'Available'. Ensuring SSH is truly ready...[/]");
                    if (!await WaitForSshReadyWithRetry(token, codespace.Name)) { // Cek SSH lagi
                         AnsiConsole.MarkupLine($"[red]  ✗ SSH failed despite 'Available' state. Deleting...[/]");
                         await DeleteCodespace(token, codespace.Name); codespace = null; break;
                    }
                    AnsiConsole.MarkupLine("[green]  ✓ SSH Ready. Triggering auto-start & checking health...[/]");
                    await TriggerStartupScript(token, codespace.Name);
                    if (await CheckHealthWithRetry(token, codespace.Name)) { // Tunggu health check
                        AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing.[/]");
                        stopwatch.Stop(); return codespace.Name; // SUKSES
                    }
                    AnsiConsole.MarkupLine($"[red]  ✗ Health check FAILED after trigger. Check logs. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name); codespace = null; break;

                case "Stopped": case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Starting...[/]");
                    // Start + Wait State Available + Wait SSH Ready
                    await StartCodespace(token, codespace.Name);
                    AnsiConsole.MarkupLine("[green]  ✓ Started & SSH Ready. Triggering auto-start & checking health...[/]");
                    await TriggerStartupScript(token, codespace.Name);
                    if (await CheckHealthWithRetry(token, codespace.Name)) { // Tunggu health check
                         AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing.[/]");
                         stopwatch.Stop(); return codespace.Name; // SUKSES
                    }
                     AnsiConsole.MarkupLine($"[red]  ✗ Health check FAILED after start. Check logs. Deleting...[/]");
                     await DeleteCodespace(token, codespace.Name); codespace = null; break;

                case "Provisioning": case "Creating": case "Starting":
                case "Queued": case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Probing SSH while waiting...[/]");
                    if (await ProbeSsh(token, codespace.Name)) {
                        AnsiConsole.MarkupLine("[cyan]    SSH Probe OK. Continue waiting state.[/]"); consecutiveSshFailures = 0;
                    } else {
                        consecutiveSshFailures++; AnsiConsole.MarkupLine($"[yellow]    SSH Probe Failed ({consecutiveSshFailures}/{SSH_PROBE_FAIL_THRESHOLD}). Waiting...[/]");
                        if (consecutiveSshFailures >= SSH_PROBE_FAIL_THRESHOLD) {
                            AnsiConsole.MarkupLine($"[red]  ✗ SSH stuck. Deleting...[/]");
                            await DeleteCodespace(token, codespace.Name); codespace = null; break;
                        }
                    }
                    AnsiConsole.MarkupLine($"[dim]  Waiting {STATE_POLL_INTERVAL_SEC}s for next state check...[/]");
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000); continue;

                default: // Error, Failed, Unknown
                    AnsiConsole.MarkupLine($"[red]  State '{codespace.State}' error. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name); codespace = null; break;
            } // End Switch

             if (codespace == null && stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                 AnsiConsole.MarkupLine($"[dim]Retry find/create after issue...[/]"); await Task.Delay(5000);
             }
        } // End While Loop

        // --- 3. Handle Timeout ---
        stopwatch.Stop();
        if (codespace != null) { AnsiConsole.MarkupLine($"\n[red]FATAL: CS '{codespace.Name}' stuck '{codespace.State}' after {STATE_POLL_MAX_DURATION_MIN} mins. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); }
        else { AnsiConsole.MarkupLine($"\n[red]FATAL: Failed state after {STATE_POLL_MAX_DURATION_MIN} mins.[/]"); }

        // --- 4. Final Attempt to Create ---
        AnsiConsole.MarkupLine("[yellow]Attempting final create...[/]");
        var (_, allFinal) = await FindExistingCodespace(token); await CleanupStuckCodespaces(token, allFinal, null);
        try { return await CreateNewCodespace(token, repoFullName); }
        catch (Exception createEx) { AnsiConsole.WriteException(createEx); throw new Exception($"FATAL: Final create failed. Error: {createEx.Message}"); }
    }


    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName) {
        AnsiConsole.MarkupLine($"\n[cyan]Creating new '{CODESPACE_DISPLAY_NAME}' ({MACHINE_TYPE})...[/]");
        AnsiConsole.MarkupLine($"[dim]Max gh create time: {CREATE_TIMEOUT_MS / 60000} mins.[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        Stopwatch createStopwatch = Stopwatch.StartNew(); string newName = "";
        try {
            newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS); createStopwatch.Stop();
             if (string.IsNullOrWhiteSpace(newName)) throw new Exception("gh create empty name");
             AnsiConsole.MarkupLine($"[green]✓ Created: {newName} (in {createStopwatch.Elapsed:mm\\:ss})[/]"); await Task.Delay(5000);

             // Tunggu Available + SSH Ready
             if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(STATE_POLL_MAX_DURATION_MIN - (int)createStopwatch.Elapsed.TotalMinutes - 1 ))) throw new Exception($"Not 'Available' in time");
             if (!await WaitForSshReadyWithRetry(token, newName)) throw new Exception($"SSH failed for new CS");

            // Upload Config & Data
            await UploadConfigs(token, newName); await UploadAllBotData(token, newName);

            // Trigger & Wait Health Check
             AnsiConsole.MarkupLine("[cyan]Triggering initial setup (auto-start.sh)...[/]"); await TriggerStartupScript(token, newName);
             AnsiConsole.MarkupLine("[cyan]Waiting initial setup complete...[/]");
             if (!await CheckHealthWithRetry(token, newName)) { AnsiConsole.MarkupLine($"[bold red]WARNING: Initial setup FAILED/timeout ({HEALTH_CHECK_MAX_DURATION_MIN} mins). Check /tmp/startup.log manually![/]"); }
             else { AnsiConsole.MarkupLine("[green]✓ Initial setup complete.[/]"); }
             return newName; // Return walau health check awal gagal

        } catch (Exception ex) {
            createStopwatch.Stop(); AnsiConsole.WriteException(ex);
            if (!string.IsNullOrWhiteSpace(newName)) { await DeleteCodespace(token, newName); }
            string info = ""; if (ex.Message.Contains("quota")) info = " (Check Quota!)"; else if (ex.Message.Contains("401")) info = " (Invalid Token!)";
            throw new Exception($"FATAL: Create failed{info}. Error: {ex.Message}");
        }
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName) {
        string args = $"codespace start -c {codespaceName}"; AnsiConsole.MarkupLine($"[dim]  Exec: gh {args}[/]"); Stopwatch sw = Stopwatch.StartNew();
        try { await ShellHelper.RunGhCommand(token, args, START_TIMEOUT_MS); }
        catch (Exception ex) { if(!ex.Message.Contains("is already available")) AnsiConsole.MarkupLine($"[yellow]  Warn (start): {ex.Message.Split('\n').FirstOrDefault()}. Checking...[/]"); else AnsiConsole.MarkupLine($"[dim]  Already available.[/]"); }
        sw.Stop(); AnsiConsole.MarkupLine($"[dim]  'gh start' done ({sw.Elapsed:mm\\:ss}). Waiting 'Available' & SSH...[/]");
        if (!await WaitForState(token, codespaceName, "Available", TimeSpan.FromMinutes(STATE_POLL_MAX_DURATION_MIN / 2))) throw new Exception($"Not 'Available' after start");
        if (!await WaitForSshReadyWithRetry(token, codespaceName)) throw new Exception($"Failed SSH after start");
    }

    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout) {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.MarkupLine($"[cyan]Waiting '{codespaceName}' state '{targetState}' (max {timeout.TotalMinutes:F1} mins)...[/]");
        while(sw.Elapsed < timeout) {
             AnsiConsole.Markup($"[dim]({sw.Elapsed:mm\\:ss}) Check state... [/]"); var state = await GetCodespaceState(token, codespaceName);
             if (state == targetState) { AnsiConsole.MarkupLine($"[green]State '{targetState}'[/]"); sw.Stop(); return true; }
             if (state == null) { AnsiConsole.MarkupLine($"[red]CS lost?[/]"); sw.Stop(); return false; }
             if (state == "Failed" || state == "Error" || state.Contains("ShuttingDown") || state=="Deleted") { AnsiConsole.MarkupLine($"[red]Error/Down: '{state}'. Abort.[/]"); sw.Stop(); return false; }
             AnsiConsole.MarkupLine($"[yellow]State '{state}'. Wait {STATE_POLL_INTERVAL_SEC}s...[/]"); await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000);
        } sw.Stop(); AnsiConsole.MarkupLine($"[red]Timeout: No '{targetState}' after {timeout.TotalMinutes:F1} mins.[/]"); return false;
    }

     private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName) {
         string args = $"codespace view --json state -c {codespaceName}";
         try { string json = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("state").GetString(); }
         catch (Exception ex) { if (ex.Message.Contains("404")) return null; AnsiConsole.MarkupLine($"[red](GetState) Err: {ex.Message.Split('\n').FirstOrDefault()}[/]"); return null; }
     }

    private static async Task<bool> ProbeSsh(TokenEntry token, string codespaceName) {
        AnsiConsole.Markup($"[dim]    SSH Probe... [/]");
        try { string args = $"codespace ssh -c {codespaceName} -- echo probe_ok"; string res = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); bool ok = res.Contains("probe_ok"); AnsiConsole.MarkupLine(ok ? "[cyan]OK[/]" : "[yellow]Fail[/]"); return ok; }
        catch (TaskCanceledException) { AnsiConsole.Markup($"[yellow]Timeout[/]"); return false; }
        catch (Exception) { AnsiConsole.Markup($"[red]Fail[/]"); return false; }
    }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName) {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.MarkupLine($"[cyan]Waiting SSH ready '{codespaceName}' (max {SSH_READY_MAX_DURATION_MIN} mins)...[/]");
        while(sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
            AnsiConsole.Markup($"[dim]({sw.Elapsed:mm\\:ss}) SSH Check... [/]");
            try { string args = $"codespace ssh -c {codespaceName} -- echo ready"; string res = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); if (res.Contains("ready")) { AnsiConsole.MarkupLine("[green]SSH Ready[/]"); sw.Stop(); return true; } AnsiConsole.MarkupLine($"[yellow]Out: '{res}'. Retry...[/]"); }
            catch (TaskCanceledException) { AnsiConsole.MarkupLine($"[red]TIMEOUT ({SSH_COMMAND_TIMEOUT_MS / 1000}s). Retry...[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}. Retry...[/]"); if (ex.Message.Contains("refused") || ex.Message.Contains("exited")) await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 500); } // Wait half interval if connection refused
            AnsiConsole.MarkupLine($"[dim]  Wait {SSH_READY_POLL_INTERVAL_SEC}s...[/]"); await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000);
        } sw.Stop(); AnsiConsole.MarkupLine($"[red]Timeout: SSH fail after {SSH_READY_MAX_DURATION_MIN} mins.[/]"); return false;
    }

    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName) {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.MarkupLine($"[cyan]Waiting health check '{codespaceName}' (max {HEALTH_CHECK_MAX_DURATION_MIN} mins)...[/]");
        while(sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
            AnsiConsole.Markup($"[dim]({sw.Elapsed:mm\\:ss}) Health Check... [/]"); string result = "";
            try {
                // Combined check for fail or success flags
                string args = $"codespace ssh -c {codespaceName} -- \"if [ -f {HEALTH_CHECK_FAILED_PROXY} ] || [ -f {HEALTH_CHECK_FAILED_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
                result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS / 4); // Short timeout

                if (result.Contains("FAILED")) { AnsiConsole.MarkupLine($"[bold red]FAILED! auto-start error flag found.[/]"); sw.Stop(); return false; }
                if (result.Contains("HEALTHY")) { AnsiConsole.MarkupLine("[green]Healthy[/]"); sw.Stop(); return true; }
                if (result.Contains("NOT_READY")) { AnsiConsole.MarkupLine("[yellow]Not healthy yet.[/]"); }
                else { AnsiConsole.MarkupLine($"[yellow]Resp: {result}. Retry...[/]"); } // Unexpected
            }
            catch (TaskCanceledException) { AnsiConsole.MarkupLine($"[red]TIMEOUT. Retry...[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}. Retry...[/]"); }
            AnsiConsole.MarkupLine($"[dim]  Wait {HEALTH_CHECK_POLL_INTERVAL_SEC}s...[/]"); await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000);
        } sw.Stop(); AnsiConsole.MarkupLine($"[red]Health Check Failed after {HEALTH_CHECK_MAX_DURATION_MIN} mins.[/]"); return false;
    }

    // === FIX CS1998: Hapus async jika tidak perlu ===
    // Fungsi ini hanya setup string path, tidak ada await
    public static async Task UploadConfigs(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine("\n[cyan]Uploading CORE configs...[/]");
        string remoteDir = $"/workspaces/{token.Repo}/config";
        AnsiConsole.Markup("[dim]  Ensure remote dir... [/]");
        // Jalankan mkdir sync (karena UploadFile async)
        try { string mkdirArgs=$"codespace ssh -c {codespaceName} -- mkdir -p {remoteDir}"; ShellHelper.RunGhCommand(token, mkdirArgs).GetAwaiter().GetResult(); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn (mkdir): {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
        // Upload tetap async
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "bots_config.json"), $"{remoteDir}/bots_config.json");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "apilist.txt"), $"{remoteDir}/apilist.txt");
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "paths.txt"), $"{remoteDir}/paths.txt");
    }

    // Fungsi ini memang melakukan banyak await
    public static async Task UploadAllBotData(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine("\n[cyan]Uploading secrets from D:\\SC...[/]"); var config=BotConfig.Load(); if (config == null) return;
        string remoteRepoRoot=$"/workspaces/{token.Repo}"; int filesUploaded=0, botsSkipped=0;
        foreach (var bot in config.BotsAndTools) {
            string localBotPath=BotConfig.GetLocalBotPath(bot.Path); string remoteBotPath=$"{remoteRepoRoot}/{bot.Path.Replace('\\', '/')}";
            if (!Directory.Exists(localBotPath)) { botsSkipped++; continue; }
            AnsiConsole.MarkupLine($"[dim]   Scan {bot.Name}...[/]"); bool botDirCreated=false; int filesForThisBot=0;
            foreach (var secretFileName in SecretFileNames) {
                string localFilePath=Path.Combine(localBotPath, secretFileName);
                if (File.Exists(localFilePath)) {
                    if (!botDirCreated) {
                        AnsiConsole.Markup($"[dim]     Create remote dir... [/]");
                        try { string mkdirArgs=$"codespace ssh -c {codespaceName} -- mkdir -p {remoteBotPath}"; await ShellHelper.RunGhCommand(token, mkdirArgs); AnsiConsole.MarkupLine("[green]OK[/]"); botDirCreated=true; }
                        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL ({ex.Message.Split('\n').FirstOrDefault()})[/]"); goto NextBot; }
                    }
                    string remoteFilePath=$"{remoteBotPath}/{secretFileName}";
                    await UploadFile(token, codespaceName, localFilePath, remoteFilePath, silent: true); // await di sini
                    AnsiConsole.MarkupLine($"[green]     ✓ Up {secretFileName}[/]"); filesUploaded++; filesForThisBot++;
                }
            } if (filesForThisBot == 0) AnsiConsole.MarkupLine($"[dim]     No secrets.[/]"); NextBot:;
        } AnsiConsole.MarkupLine($"[green]   ✓ Up complete ({filesUploaded} files).[/]");
    }

    // Fungsi ini memang melakukan await
    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath, bool silent = false) {
        if (!File.Exists(localPath)) { if (!silent) AnsiConsole.MarkupLine($"[yellow]SKIP: {Path.GetFileName(localPath)}[/]"); return; }
        if (!silent) AnsiConsole.Markup($"[dim]  Up {Path.GetFileName(localPath)}... [/]"); string args=$"codespace cp \"{localPath}\" \"remote:{remotePath}\" -c {csName}";
        try { await ShellHelper.RunGhCommand(token, args); if (!silent) AnsiConsole.MarkupLine("[green]OK[/]"); } // await di sini
        catch (Exception ex) { if (!silent) AnsiConsole.MarkupLine($"[red]FAIL[/]"); AnsiConsole.MarkupLine($"[red]    Err: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
    }

    // Fungsi ini memang melakukan await
    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine("\n[cyan]Triggering auto-start.sh...[/]"); string remoteScript=$"/workspaces/{token.Repo}/auto-start.sh";
        AnsiConsole.Markup("[dim]  Verify... [/]");
        try { string checkArgs=$"codespace ssh -c {codespaceName} -- ls {remoteScript} 2>/dev/null && echo EXISTS || echo MISSING"; string checkResult=await ShellHelper.RunGhCommand(token, checkArgs); if (!checkResult.Contains("EXISTS")) throw new Exception("Script not found"); AnsiConsole.MarkupLine("[green]OK[/]"); } // await di sini
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message}[/]"); throw; }
        AnsiConsole.Markup("[dim]  Exec (detached)... [/]"); string cmd=$"nohup bash {remoteScript} > /tmp/startup.log 2>&1 &"; string args=$"codespace ssh -c {codespaceName} -- {cmd}";
        try { await ShellHelper.RunGhCommand(token, args); AnsiConsole.MarkupLine("[green]OK[/]"); } // await di sini
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}[/]"); throw; }
    }

    // Fungsi ini memang melakukan await
    public static async Task DeleteCodespace(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine($"[yellow]Deleting {codespaceName}...[/]");
        try { string args=$"codespace delete -c {codespaceName} --force"; await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]✓ Del.[/]"); } // await di sini
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Fail del: {ex.Message.Split('\n').FirstOrDefault()}[/]"); if (ex.Message.Contains("404") || ex.Message.Contains("Could not find")) AnsiConsole.MarkupLine($"[dim]   (Gone)[/]"); else AnsiConsole.MarkupLine($"[yellow]   Manual check.[/]"); }
        await Task.Delay(3000); // await di sini
    }

    // Fungsi ini memang melakukan await
    private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> allCodespaces, string? currentCodespaceName) {
        AnsiConsole.MarkupLine("[dim]Cleaning stuck...[/]"); int cleaned=0;
        foreach (var cs in allCodespaces) {
            if (cs.Name == currentCodespaceName || cs.State == "Deleted") continue;
            if (cs.DisplayName == CODESPACE_DISPLAY_NAME) {
                 AnsiConsole.MarkupLine($"[yellow]   Found stuck: {cs.Name} ({cs.State}). Deleting...[/]");
                 await DeleteCodespace(token, cs.Name); cleaned++; // await di sini
            }
        } if (cleaned == 0) AnsiConsole.MarkupLine("[dim]   None.[/]");
    }

    // === FIX CS0161: Pastikan semua path return value ===
    // Fungsi ini memang melakukan await
    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token) {
        string args = "codespace list --json name,displayName,state"; string jsonResult = "";
        List<CodespaceInfo> allCodespaces = new List<CodespaceInfo>(); // Inisialisasi di luar try
        try {
            jsonResult = await ShellHelper.RunGhCommand(token, args); // await di sini
            try { allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>(); }
            catch (JsonException ex) { AnsiConsole.MarkupLine($"[red]Err parse JSON: {ex.Message}[/]"); AnsiConsole.MarkupLine($"[dim]JSON: {jsonResult}[/]"); /* Return list kosong */ }
        }
        catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Err list CS: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            if (ex.Message.Contains("fields")) AnsiConsole.MarkupLine("[red]   >>> Update GH CLI? <<<[/]");
            // Return list kosong jika gagal
        }
        // Cari existing setelah try-catch selesai
        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");
        return (existing, allCodespaces); // Selalu return tuple
    }

    // Fungsi ini memang melakukan await
    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName) {
        AnsiConsole.MarkupLine($"[dim]Fetching bots from {codespaceName}...[/]");
        string args = $"codespace ssh -c {codespaceName} -- tmux list-windows -F \"#{{window_name}}\"";
        try {
            string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); // await di sini
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(s => s != "dashboard" && s != "bash")
                         .OrderBy(s => s).ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Fail tmux: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            if (ex.Message.Contains("No sessions")) AnsiConsole.MarkupLine("[yellow]   Tmux down?[/]");
            return new List<string>(); // Return list kosong jika error
        }
    }
    // === AKHIR FIX ===

    private class CodespaceInfo { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; [JsonPropertyName("state")] public string State { get; set; } = ""; }
}

