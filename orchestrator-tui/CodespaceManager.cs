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

    private const int SSH_READY_POLL_INTERVAL_SEC = 20; // Jeda antar cek SSH ready
    private const int SSH_READY_MAX_DURATION_MIN = 15; // Max total waktu nunggu SSH ready (naikkan)

    // Timeout untuk health check (nunggu auto-start.sh RINGAN)
    private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 10; // Cek lebih sering
    private const int HEALTH_CHECK_MAX_DURATION_MIN = 5;  // Max 5 menit cukup untuk auto-start ringan
    private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
    // File flag error dari auto-start.sh
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
        bool needsInitialSetup = false; // Flag jika CS baru dibuat

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
                // Langsung create, karena create akan handle tunggu state & SSH
                return await CreateNewCodespace(token, repoFullName);
            }

            AnsiConsole.MarkupLine($"\n[green]✓ Found:[/] [dim]{codespace.Name} (State: {codespace.State})[/]");

            // --- 2. Handle State yang Ditemukan ---
            switch (codespace.State)
            {
                case "Available":
                    AnsiConsole.MarkupLine("[green]  State 'Available'. Waiting for SSH readiness...[/]");
                    // Meskipun Available, SSH mungkin belum siap sepenuhnya post-resume
                    if (!await WaitForSshReadyWithRetry(token, codespace.Name)) {
                         AnsiConsole.MarkupLine($"[red]  ✗ SSH failed even though state is Available. Deleting broken codespace...[/]");
                         await DeleteCodespace(token, codespace.Name);
                         codespace = null; // Tandai create baru
                         break; // Keluar switch, biarkan loop lanjut
                    }
                    AnsiConsole.MarkupLine("[green]  ✓ SSH Ready. Triggering auto-start and checking health...[/]");
                    await TriggerStartupScript(token, codespace.Name); // Jalankan auto-start ringan
                    if (await CheckHealthWithRetry(token, codespace.Name)) { // Tunggu health check
                        AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing.[/]");
                        stopwatch.Stop(); return codespace.Name; // SUKSES KELUAR
                    }
                    // Jika Available, SSH ok, TAPI health check GAGAL -> Error di auto-start?
                    AnsiConsole.MarkupLine($"[red]  ✗ Health check FAILED after triggering auto-start. Check /tmp/startup.log in codespace. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null;
                    break;

                case "Stopped":
                case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Starting...[/]");
                    // Start akan coba bangunkan, lalu tunggu state Available, lalu tunggu SSH
                    await StartCodespace(token, codespace.Name);
                    AnsiConsole.MarkupLine("[green]  ✓ Codespace Started & SSH Ready. Triggering auto-start and checking health...[/]");
                    await TriggerStartupScript(token, codespace.Name); // Jalankan auto-start ringan
                    if (await CheckHealthWithRetry(token, codespace.Name)) { // Tunggu health check
                         AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing.[/]");
                         stopwatch.Stop(); return codespace.Name; // SUKSES KELUAR
                    }
                     AnsiConsole.MarkupLine($"[red]  ✗ Health check FAILED after start. Check /tmp/startup.log. Deleting...[/]");
                     await DeleteCodespace(token, codespace.Name);
                     codespace = null;
                     break;

                case "Provisioning": case "Creating": case "Starting":
                case "Queued": case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]  State '{codespace.State}'. Waiting ({STATE_POLL_INTERVAL_SEC}s)...[/]");
                    // Jangan probe SSH di sini, biarkan Codespace selesaikan dulu state-nya
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000);
                    continue; // Langsung ke iterasi berikutnya untuk cek state lagi

                default: // Error, Failed, Unknown
                    AnsiConsole.MarkupLine($"[red]  State '{codespace.State}' indicates error. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null;
                    break;
            } // End Switch

             // Jika codespace dihapus, tunggu sebentar sebelum retry find
             if (codespace == null && stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                 AnsiConsole.MarkupLine($"[dim]Retrying find/create after state issue...[/]");
                 await Task.Delay(5000);
             }
        } // End While Loop

        // --- 3. Handle Timeout ---
        stopwatch.Stop();
        if (codespace != null) { // Timeout tapi CS masih ada (stuck?)
             AnsiConsole.MarkupLine($"\n[red]FATAL: Codespace '{codespace.Name}' stuck in state '{codespace.State}' after {STATE_POLL_MAX_DURATION_MIN} mins. Deleting...[/]");
             await DeleteCodespace(token, codespace.Name);
        } else { // Timeout dan CS sudah hilang (atau gagal delete?)
             AnsiConsole.MarkupLine($"\n[red]FATAL: Failed to get codespace to Available/Healthy state after {STATE_POLL_MAX_DURATION_MIN} mins.[/]");
        }

        // --- 4. Final Attempt to Create ---
        AnsiConsole.MarkupLine("[yellow]Attempting final create...[/]");
        var (_, allFinal) = await FindExistingCodespace(token); // Cek lagi kalau ada sisa
        await CleanupStuckCodespaces(token, allFinal, null);
        try {
            // CreateNewCodespace sudah include wait state, wait ssh, upload, trigger, wait health
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
             await Task.Delay(5000); // Jeda singkat

             // Tunggu state jadi Available
             if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(STATE_POLL_MAX_DURATION_MIN - (int)createStopwatch.Elapsed.TotalMinutes - 1 ))) { // Sisa waktu poll
                 throw new Exception($"Did not reach 'Available' state within timeout.");
             }
             // Tunggu SSH Ready
            if (!await WaitForSshReadyWithRetry(token, newName)) { throw new Exception($"SSH failed for new codespace"); }

            // Upload Config & Data Krusial
            await UploadConfigs(token, newName);
            await UploadAllBotData(token, newName); // Upload pk.txt, .env dll

            // Trigger auto-start.sh PERTAMA KALI
            AnsiConsole.MarkupLine("[cyan]Triggering initial setup (auto-start.sh)...[/]");
            await TriggerStartupScript(token, newName);

            // Tunggu health check PERTAMA KALI
             AnsiConsole.MarkupLine("[cyan]Waiting for initial setup to complete...[/]");
             if (!await CheckHealthWithRetry(token, newName)) {
                 // JANGAN throw, tapi kasih warning keras
                 AnsiConsole.MarkupLine($"[bold red]WARNING: Initial setup via auto-start.sh FAILED or timed out after {HEALTH_CHECK_MAX_DURATION_MIN} mins.[/]");
                 AnsiConsole.MarkupLine($"[bold red]   Check /tmp/startup.log in the codespace '{newName}' manually![/]");
                 AnsiConsole.MarkupLine($"[yellow]   Proceeding anyway, but bots might not run correctly.[/]");
             } else {
                  AnsiConsole.MarkupLine("[green]✓ Initial setup complete.[/]");
             }

             return newName; // Return nama CS meskipun health check awal gagal (biar user bisa attach & debug)

        } catch (Exception ex) {
            createStopwatch.Stop(); AnsiConsole.WriteException(ex);
            if (!string.IsNullOrWhiteSpace(newName)) { await DeleteCodespace(token, newName); }
            // Tambahkan detail error spesifik jika mungkin
            string additionalInfo = "";
            if (ex.Message.Contains("403") || ex.Message.Contains("quota")) additionalInfo = " (Check GitHub Billing/Quota!)";
            else if (ex.Message.Contains("401") || ex.Message.Contains("Bad credentials")) additionalInfo = " (Invalid GitHub Token!)";
            throw new Exception($"FATAL: Failed during creation{additionalInfo}. Error: {ex.Message}");
        }
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName) {
        // Hanya jalankan 'gh codespace start'
        string args = $"codespace start -c {codespaceName}";
        AnsiConsole.MarkupLine($"[dim]  Executing: gh {args}[/]");
        Stopwatch startStopwatch = Stopwatch.StartNew();
        try { await ShellHelper.RunGhCommand(token, args, START_TIMEOUT_MS); }
        catch (Exception ex) { if(!ex.Message.Contains("is already available")) AnsiConsole.MarkupLine($"[yellow]  Warn (start): {ex.Message.Split('\n').FirstOrDefault()}. Checking state/SSH...[/]"); else AnsiConsole.MarkupLine($"[dim]  Already available.[/]"); }
        startStopwatch.Stop();
        AnsiConsole.MarkupLine($"[dim]  'gh start' finished in {startStopwatch.Elapsed:mm\\:ss}. Waiting for 'Available' state & SSH...[/]");

        // Setelah start, tunggu state Available DAN SSH Ready
        if (!await WaitForState(token, codespaceName, "Available", TimeSpan.FromMinutes(STATE_POLL_MAX_DURATION_MIN / 2))) {
            throw new Exception($"Did not reach 'Available' state after starting.");
        }
        if (!await WaitForSshReadyWithRetry(token, codespaceName)) { throw new Exception($"Failed SSH after starting."); }
        // JANGAN trigger auto-start di sini, itu tugas EnsureHealthyCodespace setelah SSH ready
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
         catch (Exception ex) { if (ex.Message.Contains("404") || ex.Message.Contains("Could not find")) return null; AnsiConsole.MarkupLine($"[red](GetState) Err: {ex.Message.Split('\n').FirstOrDefault()}[/]"); return null; }
     }

    private static async Task<bool> ProbeSsh(TokenEntry token, string codespaceName) {
        AnsiConsole.Markup($"[dim]    SSH Probe... [/]");
        try { string args = $"codespace ssh -c {codespaceName} -- echo probe_ok"; string result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); bool success = result.Contains("probe_ok"); AnsiConsole.MarkupLine(success ? "[cyan]OK[/]" : "[yellow]Fail[/]"); return success; }
        catch (TaskCanceledException) { AnsiConsole.Markup($"[yellow]Timeout[/]"); return false; }
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

    // === PERBAIKAN HEALTH CHECK: Cek file GAGAL juga ===
    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName) {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Waiting health check (auto-start status) on '{codespaceName}' (max {HEALTH_CHECK_MAX_DURATION_MIN} mins)...[/]");
        while(stopwatch.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Health Check... [/]");
            try {
                // Cek file GAGAL dulu
                string checkFailArgs = $"codespace ssh -c {codespaceName} -- ls {HEALTH_CHECK_FAILED_PROXY} {HEALTH_CHECK_FAILED_DEPLOY} 2>/dev/null && echo FAILED || echo NOT_FAILED";
                string failResult = await ShellHelper.RunGhCommand(token, checkFailArgs, SSH_COMMAND_TIMEOUT_MS / 4); // Timeout super pendek
                if (failResult.Contains("FAILED")) {
                    AnsiConsole.MarkupLine($"[bold red]FAILED! auto-start.sh reported an error.[/]");
                    stopwatch.Stop(); return false; // Gagal health check
                }

                // Baru cek file SUKSES
                string checkSuccessArgs = $"codespace ssh -c {codespaceName} -- ls {HEALTH_CHECK_FILE} 2>/dev/null && echo HEALTHY || echo NOT_READY";
                string successResult = await ShellHelper.RunGhCommand(token, checkSuccessArgs, SSH_COMMAND_TIMEOUT_MS / 4); // Timeout super pendek

                if (successResult.Contains("HEALTHY")) {
                    AnsiConsole.MarkupLine("[green]Healthy (auto-start done)[/]");
                    stopwatch.Stop(); return true; // Sukses health check
                } else if (successResult.Contains("NOT_READY")) {
                    AnsiConsole.MarkupLine("[yellow]Not healthy yet (running?).[/]");
                } else {
                    AnsiConsole.MarkupLine($"[yellow]Resp: {successResult}. Retry...[/]"); // Respon aneh
                }
            }
            catch (TaskCanceledException) { AnsiConsole.MarkupLine($"[red]TIMEOUT. Retry...[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL: {ex.Message.Split('\n').FirstOrDefault()}. Retry...[/]"); }

            AnsiConsole.MarkupLine($"[dim]  Wait {HEALTH_CHECK_POLL_INTERVAL_SEC}s...[/]");
            await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000);
        }
        stopwatch.Stop();
        AnsiConsole.MarkupLine($"[red]Health Check Failed: auto-start.sh did not complete successfully within {HEALTH_CHECK_MAX_DURATION_MIN} mins.[/]");
        return false; // Gagal health check (timeout)
    }
    // === AKHIR PERBAIKAN HEALTH CHECK ===


    public static async Task UploadConfigs(TokenEntry token, string codespaceName) { /* ... fungsi tidak berubah ... */ }
    public static async Task UploadAllBotData(TokenEntry token, string codespaceName) { /* ... fungsi tidak berubah ... */ }
    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath, bool silent = false) { /* ... fungsi tidak berubah ... */ }
    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName) { /* ... fungsi tidak berubah ... */ }
    public static async Task DeleteCodespace(TokenEntry token, string codespaceName) { /* ... fungsi tidak berubah ... */ }
    private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> allCodespaces, string? currentCodespaceName) { /* ... fungsi tidak berubah ... */ }
    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token) { /* ... fungsi tidak berubah ... */ }
    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName) { /* ... fungsi tidak berubah ... */ }
    private class CodespaceInfo { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; [JsonPropertyName("state")] public string State { get; set; } = ""; }
}

