using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading; // <-- Tambah using Threading

namespace Orchestrator;

public static class CodespaceManager
{
    // ... (Konstanta & GetProjectRoot() tetap sama) ...
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";
    private const int SSH_COMMAND_TIMEOUT_MS = 120000;
    private const int CREATE_TIMEOUT_MS = 600000;
    private const int STOP_TIMEOUT_MS = 120000;
    private const int START_TIMEOUT_MS = 300000;
    private const int STATE_POLL_INTERVAL_SEC = 2;
    private const int STATE_POLL_MAX_DURATION_MIN = 8;
    private const int SSH_READY_POLL_INTERVAL_SEC = 2;
    private const int SSH_READY_MAX_DURATION_MIN = 8;
    private const int SSH_PROBE_TIMEOUT_MS = 30000;
    private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 10;
    private const int HEALTH_CHECK_MAX_DURATION_MIN = 4;
    private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
    private const string HEALTH_CHECK_FAIL_PROXY = "/tmp/auto_start_failed_proxysync";
    private const string HEALTH_CHECK_FAIL_DEPLOY = "/tmp/auto_start_failed_deploy";

    private static readonly string ProjectRoot = GetProjectRoot();
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
    private static readonly string UploadFilesListPath = Path.Combine(ConfigRoot, "upload_files.txt");

    private static string GetProjectRoot()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null) {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            if (Directory.Exists(configDir) && File.Exists(gitignore)) { return currentDir.FullName; }
            currentDir = currentDir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", ".."));
    }

    // Fungsi LoadUploadFileList tetap sama
    private static List<string> LoadUploadFileList()
    {
        if (!File.Exists(UploadFilesListPath)) {
            AnsiConsole.MarkupLine($"[yellow]Warn: '{UploadFilesListPath}' not found. Using defaults.[/]");
            return new List<string> { /* defaults */ };
        }
        try {
            return File.ReadAllLines(UploadFilesListPath).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#")).ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error reading '{UploadFilesListPath}': {ex.Message.EscapeMarkup()}. Using defaults.[/]");
            return new List<string> { /* defaults */ };
        }
    }


    // --- FUNGSI UPLOAD CREDENTIAL (Fix `gh cp` Syntax) ---
    private static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken) // <-- Tambah CancellationToken
    {
        AnsiConsole.MarkupLine("\n[cyan]═══ Uploading Credentials via gh cp ═══[/]");
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any()) { AnsiConsole.MarkupLine("[yellow]Skip: No bot config.[/]"); return; }

        var credentialFilesToUpload = LoadUploadFileList();
        if (!credentialFilesToUpload.Any()) { AnsiConsole.MarkupLine("[yellow]Skip: No files listed in 'config/upload_files.txt'.[/]"); return; }
        AnsiConsole.MarkupLine($"[dim]   Checking for {credentialFilesToUpload.Count} potential files per bot...[/]");

        int botsProcessed = 0; int filesUploaded = 0; int filesSkipped = 0;
        string remoteWorkspacePath = $"/workspaces/{token.Repo}";

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn() })
            .StartAsync(async ctx => {
                var task = ctx.AddTask("[green]Processing bots...[/]", new ProgressTaskSettings { MaxValue = config.BotsAndTools.Count });

                foreach (var bot in config.BotsAndTools)
                {
                    cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel per Bot
                    task.Description = $"[green]Checking:[/] {bot.Name}";
                    if (!bot.Enabled) { task.Increment(1); continue; }

                    string localBotDir = BotConfig.GetLocalBotPath(bot.Path);
                    if (!Directory.Exists(localBotDir)) { filesSkipped++; task.Increment(1); continue; }

                    string remoteBotDir = Path.Combine(remoteWorkspacePath, bot.Path).Replace('\\', '/');
                    bool botProcessed = false;

                    // Buat direktori remote SEKALI per bot
                    try {
                        string mkdirArgs = $"codespace ssh -c {codespaceName} -- mkdir -p \"{remoteBotDir}\"";
                        await ShellHelper.RunGhCommand(token, mkdirArgs, SSH_PROBE_TIMEOUT_MS);
                    } catch (Exception mkdirEx) {
                        AnsiConsole.MarkupLine($"[red]   Failed create remote dir for {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()}. Skipping bot.[/]");
                        filesSkipped += credentialFilesToUpload.Count; // Asumsi semua file gagal
                        task.Increment(1);
                        continue; // Lanjut ke bot berikutnya
                    }


                    foreach (var credFileName in credentialFilesToUpload)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel per File
                        string localFilePath = Path.Combine(localBotDir, credFileName);

                        if (File.Exists(localFilePath))
                        {
                            botProcessed = true;
                            string remoteFilePath = $"{remoteBotDir}/{credFileName}";
                            task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";
                            string localAbsPath = Path.GetFullPath(localFilePath);

                            // === PERBAIKAN: Format `gh cp` ===
                            // Format: gh codespace cp --codespace <name> <local_path> remote:<remote_path>
                            string remoteTargetArg = $"remote:{remoteFilePath}"; // <-- WAJIB pakai "remote:" prefix
                            string cpArgs = $"codespace cp --codespace \"{codespaceName}\" \"{localAbsPath}\" \"{remoteTargetArg}\"";
                            // === AKHIR PERBAIKAN ===

                            try {
                                // Timeout lebih lama untuk upload file
                                await ShellHelper.RunGhCommand(token, cpArgs, SSH_COMMAND_TIMEOUT_MS * 2); // 240 detik
                                filesUploaded++;
                            } catch (OperationCanceledException) { // Tangkap cancel dari ShellHelper
                                 AnsiConsole.MarkupLine($"[yellow]   Upload cancelled for {credFileName}.[/]");
                                 filesSkipped++;
                                 throw; // Lempar lagi biar loop utama berhenti
                            } catch (Exception ex) {
                                AnsiConsole.MarkupLine($"[red]   ✗ Failed upload {credFileName} for {bot.Name}: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                                filesSkipped++;
                            }
                            // Beri jeda sedikit antar file untuk mengurangi load
                            await Task.Delay(150, cancellationToken); // Jeda + cek cancel
                        }
                    } // End foreach file

                    if (botProcessed) { botsProcessed++; }
                    task.Increment(1);
                } // End foreach bot
            });

        AnsiConsole.MarkupLine($"[green]✓ Credential upload finished.[/]");
        AnsiConsole.MarkupLine($"[dim]   Bots checked: {config.BotsAndTools.Count} | Bots processed: {botsProcessed} | Files uploaded: {filesUploaded} | Files skipped/failed: {filesSkipped}[/]");
    }
    // --- AKHIR FUNGSI UPLOAD ---


    public static async Task<string> EnsureHealthyCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken) // <-- Tambah CancellationToken
    {
        AnsiConsole.MarkupLine("\n[cyan]═══ Ensuring Codespace Runner ═══[/]");
        CodespaceInfo? codespace = null;
        Stopwatch stopwatch = Stopwatch.StartNew();

        AnsiConsole.Markup("[dim]Checking repo last commit... [/]");
        var repoLastCommit = await GetRepoLastCommitDate(token);
        cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
        if (repoLastCommit.HasValue) AnsiConsole.MarkupLine($"[green]OK[/] [dim]({repoLastCommit.Value:yyyy-MM-dd HH:mm} UTC)[/]");
        else AnsiConsole.MarkupLine("[yellow]Unable to fetch (continuing)[/]");

        while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN)
        {
            cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel di awal loop
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Finding codespace... [/]");
            var (found, all) = await FindExistingCodespace(token);
            cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel setelah find

            codespace = found;

            if (codespace == null) {
                AnsiConsole.MarkupLine("[yellow]Not found[/]");
                await CleanupStuckCodespaces(token, all, null);
                cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel sebelum create
                return await CreateNewCodespace(token, repoFullName, cancellationToken); // <-- Kirim CancellationToken
            }

            // ... (Logika cek outdated tetap sama) ...
            AnsiConsole.MarkupLine($"[green]Found[/] [dim]{codespace.Name} ({codespace.State})[/]");
            if (repoLastCommit.HasValue && !string.IsNullOrEmpty(codespace.CreatedAt)) { /*...*/ }

            switch (codespace.State)
            {
                case "Available":
                    AnsiConsole.MarkupLine("[cyan]State: Available. Checking SSH...[/]");
                    if (!await WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken)) { // <-- Kirim CancellationToken
                        AnsiConsole.MarkupLine($"[red]SSH failed. Deleting...[/]");
                        await DeleteCodespace(token, codespace.Name); codespace = null; break;
                    }
                    await UploadCredentialsToCodespace(token, codespace.Name, cancellationToken); // <-- Kirim CancellationToken
                    AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                    try { await TriggerStartupScript(token, codespace.Name); }
                    catch (Exception scriptEx) { AnsiConsole.MarkupLine($"[yellow]Warn: Trigger failed: {scriptEx.Message.Split('\n').FirstOrDefault()}[/]"); }
                    if (await CheckHealthWithRetry(token, codespace.Name, cancellationToken)) { // <-- Kirim CancellationToken
                        AnsiConsole.MarkupLine("[green]✓ Health OK. Reusing.[/]"); stopwatch.Stop(); return codespace.Name;
                    }
                    AnsiConsole.MarkupLine($"[yellow]Health timeout but SSH OK. Assuming healthy.[/]"); stopwatch.Stop(); return codespace.Name;

                case "Stopped": case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Starting...[/]");
                    await StartCodespace(token, codespace.Name);
                    if (!await WaitForState(token, codespace.Name, "Available", TimeSpan.FromMinutes(3), cancellationToken)) // <-- Kirim CancellationToken
                        AnsiConsole.MarkupLine("[yellow]State timeout, checking SSH anyway...[/]");
                    if (!await WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken)) { // <-- Kirim CancellationToken
                        AnsiConsole.MarkupLine("[red]SSH failed after start. Deleting...[/]");
                        await DeleteCodespace(token, codespace.Name); codespace = null; break;
                    }
                    await UploadCredentialsToCodespace(token, codespace.Name, cancellationToken); // <-- Kirim CancellationToken
                    AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                     try { await TriggerStartupScript(token, codespace.Name); }
                    catch (Exception scriptEx) { AnsiConsole.MarkupLine($"[yellow]Warn: Trigger failed: {scriptEx.Message.Split('\n').FirstOrDefault()}[/]"); }
                    if (await CheckHealthWithRetry(token, codespace.Name, cancellationToken)) { // <-- Kirim CancellationToken
                        AnsiConsole.MarkupLine("[green]✓ Health OK. Reusing.[/]"); stopwatch.Stop(); return codespace.Name;
                    }
                    AnsiConsole.MarkupLine($"[yellow]Health timeout but SSH OK. Assuming healthy.[/]"); stopwatch.Stop(); return codespace.Name;

                case "Starting": case "Queued": case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Waiting {STATE_POLL_INTERVAL_SEC}s...[/]");
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000, cancellationToken); // <-- Kirim CancellationToken
                    continue;

                default:
                    AnsiConsole.MarkupLine($"[red]Bad state: {codespace.State}. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name); codespace = null; break;
            }

            if (codespace == null && stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                await Task.Delay(5000, cancellationToken); // <-- Kirim CancellationToken
            }
        } // End while

        // ... (Logika fallback jika timeout loop utama tetap sama) ...
        stopwatch.Stop();
        if (codespace != null) { AnsiConsole.MarkupLine($"\n[yellow]CS still {codespace.State} after timeout.[/]"); return codespace.Name; }
        AnsiConsole.MarkupLine($"\n[red]FATAL: No healthy codespace after timeout.[/]");
        var (_, allFinal) = await FindExistingCodespace(token); await CleanupStuckCodespaces(token, allFinal, null);
        try { return await CreateNewCodespace(token, repoFullName, cancellationToken); } // <-- Kirim CancellationToken
        catch (Exception createEx) { AnsiConsole.WriteException(createEx); throw; }
    }


    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken) // <-- Tambah CancellationToken
    {
        AnsiConsole.MarkupLine($"\n[cyan]═══ Creating New Codespace ═══[/]");
        AnsiConsole.MarkupLine($"[dim]Machine: {MACHINE_TYPE}, Display: {CODESPACE_DISPLAY_NAME}[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        Stopwatch createStopwatch = Stopwatch.StartNew();
        string newName = "";
        try {
            newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS);
            cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel setelah create
            if (string.IsNullOrWhiteSpace(newName)) throw new Exception("gh create returned empty name");

            AnsiConsole.MarkupLine($"[green]✓ Created: {newName}[/] [dim]({createStopwatch.Elapsed:mm\\:ss})[/]");
            AnsiConsole.MarkupLine("\n[cyan]═══ First Boot Optimization ═══[/]");
            AnsiConsole.MarkupLine("[yellow]Waiting for init...[/]");
            await Task.Delay(45000, cancellationToken); // <-- Kirim CancellationToken
            var currentState = await GetCodespaceState(token, newName);
             cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel

            AnsiConsole.MarkupLine($"[dim]Current state: {currentState}[/]");
            if (currentState == "Available") {
                 AnsiConsole.MarkupLine("[yellow]Performing restart...[/]");
                 await StopCodespace(token, newName); await Task.Delay(8000, cancellationToken); // <-- Kirim CancellationToken
                 await StartCodespace(token, newName);
                 AnsiConsole.MarkupLine("[cyan]Waiting for Available state...[/]");
                 if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(5), cancellationToken)) { // <-- Kirim CancellationToken
                     AnsiConsole.MarkupLine("[yellow]State timeout, checking SSH anyway...[/]");
                 }
            } else {
                 AnsiConsole.MarkupLine($"[dim]Waiting for Available (current: {currentState})...[/]");
                 if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(6), cancellationToken)) { // <-- Kirim CancellationToken
                     AnsiConsole.MarkupLine("[yellow]State timeout, checking SSH anyway...[/]");
                 }
            }
            cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel

            AnsiConsole.MarkupLine("[cyan]Waiting for SSH ready...[/]");
            if (!await WaitForSshReadyWithRetry(token, newName, cancellationToken)) { // <-- Kirim CancellationToken
                throw new Exception("SSH failed after initialization");
            }
            // SSH Ready
            await UploadCredentialsToCodespace(token, newName, cancellationToken); // <-- Kirim CancellationToken

            AnsiConsole.MarkupLine("[dim]Finalizing setup...[/]");
            await Task.Delay(5000, cancellationToken); // <-- Kirim CancellationToken

            AnsiConsole.MarkupLine("[green]✓ Codespace ready.[/]");
            AnsiConsole.MarkupLine("\n[cyan]═══ Triggering Auto-Start ═══[/]");
            await TriggerStartupScript(token, newName); // Trigger tidak perlu CancellationToken krn cepat

            AnsiConsole.MarkupLine("[green]✓ Codespace created & initialized.[/]");
            return newName;

        } catch (OperationCanceledException) { // Tangkap cancel selama proses create
             AnsiConsole.MarkupLine("[yellow]Codespace creation cancelled.[/]");
             if (!string.IsNullOrWhiteSpace(newName)) { await DeleteCodespace(token, newName); } // Cleanup
             throw; // Lempar lagi
        } catch (Exception ex) {
            // ... (Logika error handling create tetap sama) ...
            createStopwatch.Stop(); AnsiConsole.WriteException(ex);
            if (!string.IsNullOrWhiteSpace(newName)) { await DeleteCodespace(token, newName); }
            string info = ""; if (ex.Message.Contains("quota")) info = " (Quota?)"; else if (ex.Message.Contains("401")) info = " (Token?)";
            throw new Exception($"FATAL: Create failed{info}. {ex.Message}");
        }
    }


    // --- Tambahkan CancellationToken ke fungsi wait ---
    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.Markup($"[cyan]Waiting state '{targetState}' (max {timeout.TotalMinutes:F1}min)...[/]");
        while(sw.Elapsed < timeout) {
            cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
            var state = await GetCodespaceState(token, codespaceName);
            if (state == targetState) { AnsiConsole.MarkupLine($"[green]✓ {targetState}[/]"); return true; }
            if (state == null) { AnsiConsole.MarkupLine($"[red]Lost[/]"); return false; }
            if (state == "Failed" || state == "Error" || state.Contains("Shutting")) { AnsiConsole.MarkupLine($"[red]{state}[/]"); return false; }
            await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000, cancellationToken); // <-- Kirim CancellationToken
        }
        AnsiConsole.MarkupLine($"[yellow]Timeout[/]"); return false;
    }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.Markup($"[cyan]Waiting SSH ready (max {SSH_READY_MAX_DURATION_MIN}min)...[/]");
        while(sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
             cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
            try {
                string args = $"codespace ssh -c {codespaceName} -- echo ready";
                string res = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
                if (res.Contains("ready")) { AnsiConsole.MarkupLine("[green]✓ SSH Ready[/]"); return true; }
            }
            catch (OperationCanceledException) { throw; } // Jangan tangkap cancel
            catch { /* Ignore other errors, retry */ }
            await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000, cancellationToken); // <-- Kirim CancellationToken
        }
        AnsiConsole.MarkupLine($"[yellow]SSH Timeout[/]"); return false;
    }

     public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken) // <-- Tambah CancellationToken
    {
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.Markup($"[cyan]Checking health (max {HEALTH_CHECK_MAX_DURATION_MIN}min)...[/]");
        int consecutiveSshSuccess = 0; const int SSH_SUCCESS_THRESHOLD = 2;
        while(sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
             cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
            string result = "";
            try {
                string args = $"codespace ssh -c {codespaceName} -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
                result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
                if (result.Contains("FAILED")) { AnsiConsole.MarkupLine($"[red]✗ Startup failed[/]"); return false; }
                if (result.Contains("HEALTHY")) { AnsiConsole.MarkupLine("[green]✓ Healthy[/]"); return true; }
                if (result.Contains("NOT_READY")) { consecutiveSshSuccess++; }
                if (consecutiveSshSuccess >= SSH_SUCCESS_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) { AnsiConsole.MarkupLine($"[cyan]SSH stable. Assuming startup OK.[/]"); return true; }
            }
            catch (OperationCanceledException) { throw; } // Jangan tangkap cancel
            catch { consecutiveSshSuccess = 0; }
            await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000, cancellationToken); // <-- Kirim CancellationToken
        }
        AnsiConsole.MarkupLine($"[yellow]Health Timeout[/]"); return false;
    }
    // --- Akhir penambahan CancellationToken ---


    // ... (Fungsi FindExistingCodespace, GetCodespaceState, GetRepoLastCommitDate, DeleteCodespace, StopCodespace, StartCodespace, CleanupStuckCodespaces, TriggerStartupScript, GetTmuxSessions, class CodespaceInfo tetap sama) ...
    // ... TIDAK PERLU DIUBAH LAGI ...
     private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token) { /* ... sama ... */ return (null, new List<CodespaceInfo>()); } // Placeholder
    private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName) { /* ... sama ... */ return null; } // Placeholder
     private static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token) { /* ... sama ... */ return null; } // Placeholder
    public static async Task DeleteCodespace(TokenEntry token, string codespaceName) { /* ... sama ... */ await Task.CompletedTask; } // Placeholder
    private static async Task StopCodespace(TokenEntry token, string codespaceName) { /* ... sama ... */ await Task.CompletedTask; } // Placeholder
    private static async Task StartCodespace(TokenEntry token, string codespaceName) { /* ... sama ... */ await Task.CompletedTask; } // Placeholder
    private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> all, string? current) { /* ... sama ... */ await Task.CompletedTask; } // Placeholder
     public static async Task TriggerStartupScript(TokenEntry token, string codespaceName) { /* ... sama ... */ await Task.CompletedTask; } // Placeholder
     public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName) { /* ... sama ... */ return new List<string>(); } // Placeholder
     private class CodespaceInfo { public string Name{get;set;}=""; public string DisplayName{get;set;}=""; public string State{get;set;}=""; public string CreatedAt{get;set;}=""; } // Placeholder

} // Akhir class CodespaceManager
