using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions; // Dibutuhkan untuk Regex

namespace Orchestrator;

public static class CodespaceManager
{
    // --- Konstanta (tidak berubah dari sebelumnya) ---
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";
    private const int SSH_COMMAND_TIMEOUT_MS = 120000;
    private const int CREATE_TIMEOUT_MS = 600000;
    private const int STOP_TIMEOUT_MS = 120000;
    private const int START_TIMEOUT_MS = 300000;
    private const int STATE_POLL_INTERVAL_FAST_MS = 500;
    private const int STATE_POLL_INTERVAL_SLOW_SEC = 3;
    private const int STATE_POLL_MAX_DURATION_MIN = 8;
    private const int SSH_READY_POLL_INTERVAL_FAST_MS = 500;
    private const int SSH_READY_POLL_INTERVAL_SLOW_SEC = 2;
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

    // --- Fungsi GetProjectRoot, LoadUploadFileList, GetFilesToUploadForBot (tidak berubah) ---
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
    private static List<string> LoadUploadFileList() { /* ... implementasi sama ... */ }
    private static List<string> GetFilesToUploadForBot(string localBotDir, List<string> allPossibleFiles) { /* ... implementasi sama ... */ }


    // === FUNGSI UploadCredentialsToCodespace DENGAN VERIFIKASI ===
    private static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[cyan]═══ Uploading Credentials & Configs via gh cp ═══[/]");
        var config = BotConfig.Load();
        if (config == null) { AnsiConsole.MarkupLine("[red]✗ Gagal load bots_config.json. Upload batal.[/]"); return; }

        var botCredentialFiles = LoadUploadFileList();
        int botsProcessed = 0; int filesUploaded = 0; int filesSkipped = 0; int botsSkipped = 0;
        string remoteWorkspacePath = $"/workspaces/{token.Repo.ToLowerInvariant()}";

        AnsiConsole.MarkupLine($"[dim]Remote workspace: {remoteWorkspacePath}[/]");
        AnsiConsole.MarkupLine($"[dim]Scanning {botCredentialFiles.Count} possible credential files per bot...[/]");

        try {
            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn() })
                .StartAsync(async ctx => {
                    var task = ctx.AddTask("[green]Processing bots & configs...[/]", new ProgressTaskSettings { MaxValue = config.BotsAndTools.Count + 1 });

                    // --- Loop Bot Credentials ---
                    foreach (var bot in config.BotsAndTools)
                    {
                        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                        task.Description = $"[green]Checking:[/] {bot.Name}";

                        if (bot.Name == "ProxySync-Tool") { AnsiConsole.MarkupLine($"[dim]SKIP Creds: {bot.Name} (handled separately)[/]"); task.Increment(1); continue; }
                        if (!bot.Enabled) { AnsiConsole.MarkupLine($"[dim]SKIP Disabled: {bot.Name}[/]"); task.Increment(1); continue; }

                        string localBotDir = BotConfig.GetLocalBotPath(bot.Path);
                        if (!Directory.Exists(localBotDir)) { AnsiConsole.MarkupLine($"[yellow]SKIP No Local Dir: {bot.Name} ({localBotDir.EscapeMarkup()})[/]"); botsSkipped++; task.Increment(1); continue; }

                        var filesToUpload = GetFilesToUploadForBot(localBotDir, botCredentialFiles);
                        if (!filesToUpload.Any()) { AnsiConsole.MarkupLine($"[dim]SKIP No Creds Found: {bot.Name}[/]"); botsSkipped++; task.Increment(1); continue; }

                        string remoteBotDir = Path.Combine(remoteWorkspacePath, bot.Path).Replace('\\', '/');
                        string escapedRemoteBotDir = $"'{remoteBotDir.Replace("'", "'\\''")}'"; // Escape untuk command ssh

                        // 1. Coba Buat Direktori
                        task.Description = $"[grey]Creating dir:[/] {bot.Name}";
                        bool mkdirSuccess = false;
                        try {
                            string mkdirCmd = $"mkdir -p {escapedRemoteBotDir}";
                            string sshMkdirArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                            await ShellHelper.RunGhCommand(token, sshMkdirArgs, 90000); // Timeout 90s
                            mkdirSuccess = true; // Anggap sukses jika tidak ada exception
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception mkdirEx) {
                            AnsiConsole.MarkupLine($"[red]✗ Failed mkdir command for {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                            // Tidak perlu set mkdirSuccess = false, sudah default
                        }

                        // 2. Verifikasi Direktori dengan 'test -d'
                        bool dirExists = false;
                        if (mkdirSuccess) // Hanya verifikasi jika command mkdir tidak throw error
                        {
                            task.Description = $"[grey]Verifying dir:[/] {bot.Name}";
                            try {
                                await Task.Delay(500, cancellationToken); // Delay singkat sebelum verifikasi
                                string testCmd = $"test -d {escapedRemoteBotDir}";
                                string sshTestArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{testCmd}\"";
                                // Jalankan 'test -d'. RunGhCommand akan throw exception jika exit code != 0
                                await ShellHelper.RunGhCommand(token, sshTestArgs, 30000); // Timeout pendek untuk test
                                dirExists = true; // Jika tidak ada exception, direktori ada
                                AnsiConsole.MarkupLine($"[green]✓ Directory verified for {bot.Name}[/]");
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception testEx) {
                                // Tangkap error dari RunGhCommand (jika exit code != 0)
                                AnsiConsole.MarkupLine($"[red]✗ Directory verification FAILED for {bot.Name} after mkdir attempt.[/]");
                                AnsiConsole.MarkupLine($"[grey]   Verification error: {testEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                                // dirExists tetap false
                            }
                        } else {
                            AnsiConsole.MarkupLine($"[red]✗ Skipping verification for {bot.Name} because mkdir command failed.[/]");
                        }


                        // 3. Upload File HANYA JIKA Direktori Ada
                        if (dirExists)
                        {
                            foreach (var credFileName in filesToUpload)
                            {
                                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                                string localFilePath = Path.Combine(localBotDir, credFileName);
                                string remoteFilePath = $"{remoteBotDir}/{credFileName}"; // Path non-escaped untuk cp
                                task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";
                                string localAbsPath = Path.GetFullPath(localFilePath);
                                string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteFilePath}\"";
                                try {
                                    await ShellHelper.RunGhCommand(token, cpArgs, 120000); // Timeout cp 120s
                                    filesUploaded++;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch { // Tangkap error cp (sudah fatal karena retry dihapus)
                                    AnsiConsole.MarkupLine($"[red]✗ Failed upload: {bot.Name}/{credFileName}[/]"); // Log tambahan
                                    filesSkipped++;
                                }
                                try { await Task.Delay(200, cancellationToken); } catch (OperationCanceledException) { throw; }
                            }
                            botsProcessed++;
                        }
                        else // Jika direktori tidak ada/gagal diverifikasi
                        {
                            AnsiConsole.MarkupLine($"[red]✗ Skipping file uploads for {bot.Name} due to directory creation/verification failure.[/]");
                            filesSkipped += filesToUpload.Count; // Hitung semua file sebagai gagal
                            botsSkipped++;
                        }

                        task.Increment(1); // Increment task bot
                    } // Akhir foreach bot

                    // --- Upload Config ProxySync (TIDAK BERUBAH, tapi pakai verifikasi juga) ---
                    task.Description = "[cyan]Uploading ProxySync Configs...";
                    var proxySyncConfigFiles = new List<string> { "apikeys.txt", "apilist.txt" };
                    string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/proxysync/config";
                    string escapedRemoteProxySyncDir = $"'{remoteProxySyncConfigDir.Replace("'", "'\\''")}'";
                    bool proxySyncConfigUploadSuccess = true;
                    bool proxySyncDirExists = false;

                    try {
                        // 1. Coba buat direktori
                        string mkdirCmd = $"mkdir -p {escapedRemoteProxySyncDir}";
                        string sshMkdirArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                        await ShellHelper.RunGhCommand(token, sshMkdirArgs, 60000); // Timeout 60s

                        // 2. Verifikasi direktori
                        await Task.Delay(500, cancellationToken);
                        string testCmd = $"test -d {escapedRemoteProxySyncDir}";
                        string sshTestArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{testCmd}\"";
                        await ShellHelper.RunGhCommand(token, sshTestArgs, 30000);
                        proxySyncDirExists = true;
                        AnsiConsole.MarkupLine($"[green]✓ ProxySync config directory verified.[/]");

                    } catch (OperationCanceledException) { throw; }
                    catch (Exception dirEx) {
                         AnsiConsole.MarkupLine($"[red]✗ Error creating/verifying ProxySync config dir: {dirEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                         filesSkipped += proxySyncConfigFiles.Count; // Anggap semua gagal
                         proxySyncConfigUploadSuccess = false;
                    }

                    // 3. Upload jika direktori ada
                    if (proxySyncDirExists) {
                        foreach (var configFileName in proxySyncConfigFiles) {
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                            string localConfigPath = Path.Combine(ConfigRoot, configFileName);
                            string remoteConfigPath = $"{remoteProxySyncConfigDir}/{configFileName}";
                            if (!File.Exists(localConfigPath)) { AnsiConsole.MarkupLine($"[yellow]WARN: Local ProxySync config '{configFileName}' not found. Skipping.[/]"); continue; }

                            task.Description = $"[cyan]Uploading:[/] proxysync/{configFileName}";
                            string localAbsPath = Path.GetFullPath(localConfigPath);
                            string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteConfigPath}\"";
                            try {
                                await ShellHelper.RunGhCommand(token, cpArgs, 60000); filesUploaded++;
                            } catch (OperationCanceledException) { throw; }
                            catch { filesSkipped++; proxySyncConfigUploadSuccess = false; AnsiConsole.MarkupLine($"[red]✗ Failed upload: proxysync/{configFileName}[/]"); }
                            try { await Task.Delay(100, cancellationToken); } catch (OperationCanceledException) { throw; }
                        }
                    } else {
                         AnsiConsole.MarkupLine($"[red]✗ Skipping ProxySync config uploads due to directory failure.[/]");
                    }

                    if (proxySyncConfigUploadSuccess && proxySyncDirExists) AnsiConsole.MarkupLine("[green]✓ ProxySync configs uploaded.[/]");
                    else AnsiConsole.MarkupLine("[yellow]! Some ProxySync configs failed to upload or directory failed.[/]");
                    task.Increment(1); // Increment task proxysync
                    // --- Akhir Upload Config ProxySync ---

                }); // Akhir Progress
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Upload cancelled.[/]"); AnsiConsole.MarkupLine($"[dim]   Partial: Bots OK: {botsProcessed}, Bots Skip: {botsSkipped} | Files OK: {filesUploaded}, Files Fail: {filesSkipped}[/]"); throw; }
        catch (Exception uploadEx) { AnsiConsole.MarkupLine("\n[red]UNEXPECTED UPLOAD ERROR[/]"); AnsiConsole.WriteException(uploadEx); throw; }
        AnsiConsole.MarkupLine($"\n[green]✓ Upload finished.[/]");
        AnsiConsole.MarkupLine($"[dim]   Bots OK: {botsProcessed}, Bots Skip: {botsSkipped} | Files OK: {filesUploaded}, Files Fail: {filesSkipped}[/]");
    }
    // === AKHIR FUNGSI UploadCredentialsToCodespace ===


    // --- Fungsi EnsureHealthyCodespace, CreateNewCodespace, ListAllCodespaces, GetCodespaceState, ---
    // --- GetRepoLastCommitDate, DeleteCodespace, StopCodespace, StartCodespace,              ---
    // --- WaitForState, WaitForSshReadyWithRetry, CheckHealthWithRetry, TriggerStartupScript, ---
    // --- GetTmuxSessions, class CodespaceInfo                                                ---
    // --- TIDAK BERUBAH dari versi terakhir yang sudah benar (dengan fast polling & tanpa --web) ---

    public static async Task<string> EnsureHealthyCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[cyan]Ensuring Codespace...[/]");
        CodespaceInfo? codespace = null; Stopwatch stopwatch = Stopwatch.StartNew();
        try {
            AnsiConsole.Markup("[dim]Checking repo commit... [/]");
            var repoLastCommit = await GetRepoLastCommitDate(token); cancellationToken.ThrowIfCancellationRequested();
            if (repoLastCommit.HasValue) AnsiConsole.MarkupLine($"[green]OK ({repoLastCommit.Value:yyyy-MM-dd HH:mm} UTC)[/]"); else AnsiConsole.MarkupLine("[yellow]Fetch failed[/]");

            while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                cancellationToken.ThrowIfCancellationRequested();
                AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Finding CS '{CODESPACE_DISPLAY_NAME}'... [/]");
                var codespaceList = await ListAllCodespaces(token); cancellationToken.ThrowIfCancellationRequested();
                codespace = codespaceList.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");

                if (codespace == null) { AnsiConsole.MarkupLine("[yellow]Not found.[/]"); return await CreateNewCodespace(token, repoFullName, cancellationToken); }
                AnsiConsole.MarkupLine($"[green]Found:[/] [blue]{codespace.Name}[/] [dim]({codespace.State})[/]");
                cancellationToken.ThrowIfCancellationRequested();
                if (repoLastCommit.HasValue && !string.IsNullOrEmpty(codespace.CreatedAt)) {
                    if (DateTime.TryParse(codespace.CreatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var csCreated)) {
                        if (repoLastCommit.Value > csCreated) { AnsiConsole.MarkupLine($"[yellow]⚠ Outdated CS. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; AnsiConsole.MarkupLine("[dim]Waiting 5s...[/]"); await Task.Delay(5000, cancellationToken); continue; }
                    } else AnsiConsole.MarkupLine($"[yellow]Warn: Could not parse CS date '{codespace.CreatedAt}'[/]");
                }
                cancellationToken.ThrowIfCancellationRequested();
                switch (codespace.State) {
                    case "Available":
                        AnsiConsole.MarkupLine("[cyan]State: Available. Verifying SSH & Uploading...[/]");
                        if (!await WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken, useFastPolling: false)) { AnsiConsole.MarkupLine($"[red]SSH failed. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break; }
                        await UploadCredentialsToCodespace(token, codespace.Name, cancellationToken);
                        AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                        try { await TriggerStartupScript(token, codespace.Name); } catch { }
                        if (await CheckHealthWithRetry(token, codespace.Name, cancellationToken)) { AnsiConsole.MarkupLine("[green]✓ Health OK. Ready.[/]"); stopwatch.Stop(); return codespace.Name; }
                        else { var lastState = await GetCodespaceState(token, codespace.Name); if (lastState == "Available") { AnsiConsole.MarkupLine($"[yellow]WARN: Health timeout, but state 'Available'. Using anyway.[/]"); stopwatch.Stop(); return codespace.Name; } else { AnsiConsole.MarkupLine($"[red]Health failed & state '{lastState ?? "Unknown"}'. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break; } }
                    case "Stopped": case "Shutdown":
                        AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Starting...[/]"); await StartCodespace(token, codespace.Name);
                        if (!await WaitForState(token, codespace.Name, "Available", TimeSpan.FromMinutes(4), cancellationToken, useFastPolling: false)) { AnsiConsole.MarkupLine("[red]Failed start. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break; }
                        AnsiConsole.MarkupLine("[green]Started. Re-checking...[/]"); await Task.Delay(STATE_POLL_INTERVAL_SLOW_SEC * 1000, cancellationToken); continue;
                    case "Starting": case "Queued": case "Rebuilding": case "Creating":
                        AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Waiting {STATE_POLL_INTERVAL_SLOW_SEC}s...[/]"); await Task.Delay(STATE_POLL_INTERVAL_SLOW_SEC * 1000, cancellationToken); continue;
                    default: AnsiConsole.MarkupLine($"[red]Unhealthy state: '{codespace.State}'. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break;
                }
                if (codespace == null) { AnsiConsole.MarkupLine("[dim]Waiting 5s...[/]"); await Task.Delay(5000, cancellationToken); }
            }
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]EnsureHealthy cancelled.[/]"); stopwatch.Stop(); throw; }
        catch (Exception ex) { stopwatch.Stop(); AnsiConsole.MarkupLine($"\n[red]FATAL EnsureHealthy:[/]"); AnsiConsole.WriteException(ex); if (codespace != null && !string.IsNullOrEmpty(codespace.Name)) { AnsiConsole.MarkupLine($"[yellow]Deleting broken CS {codespace.Name}...[/]"); try { await DeleteCodespace(token, codespace.Name); } catch { } } throw; }
        stopwatch.Stop(); AnsiConsole.MarkupLine($"\n[red]FATAL: Timeout ensuring healthy codespace.[/]"); if (codespace != null && !string.IsNullOrEmpty(codespace.Name)) { AnsiConsole.MarkupLine($"[yellow]Deleting last known CS {codespace.Name}...[/]"); try { await DeleteCodespace(token, codespace.Name); } catch { } } throw new Exception($"Failed ensure healthy codespace.");
    }

    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"\n[cyan]Attempting create new codespace...[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m"; // NO --web
        Stopwatch createStopwatch = Stopwatch.StartNew(); string newName = "";
        try {
            AnsiConsole.MarkupLine("[dim]Running 'gh codespace create'...[/]"); newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS); cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(newName) || !newName.Contains(CODESPACE_DISPLAY_NAME)) { AnsiConsole.MarkupLine($"[yellow]WARN: Unexpected 'gh create' output. Fallback list...[/]"); newName = ""; }
            else { newName = newName.Trim(); AnsiConsole.MarkupLine($"[green]✓ Create command likely OK: {newName}[/] ({createStopwatch.Elapsed:mm\\:ss})"); }
            if (string.IsNullOrWhiteSpace(newName)) { AnsiConsole.MarkupLine("[dim]Waiting 3s before listing...[/]"); await Task.Delay(3000, cancellationToken); var list = await ListAllCodespaces(token); var found = list.Where(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME).OrderByDescending(cs => cs.CreatedAt).FirstOrDefault(); if (found == null || string.IsNullOrWhiteSpace(found.Name)) throw new Exception("gh create failed & fallback list empty"); newName = found.Name; AnsiConsole.MarkupLine($"[green]✓ Fallback found: {newName}[/]"); }
            AnsiConsole.MarkupLine("[cyan]Waiting for Available state...[/]"); if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(6), cancellationToken, useFastPolling: true)) throw new Exception($"CS '{newName}' failed reach Available"); // FAST poll
            AnsiConsole.MarkupLine("[cyan]State Available. Verifying SSH...[/]"); if (!await WaitForSshReadyWithRetry(token, newName, cancellationToken, useFastPolling: true)) throw new Exception($"SSH to '{newName}' failed"); // FAST poll
            AnsiConsole.MarkupLine("[cyan]SSH OK. Uploading credentials...[/]"); await UploadCredentialsToCodespace(token, newName, cancellationToken);
            AnsiConsole.MarkupLine("[dim]Finalizing...[/]"); await Task.Delay(5000, cancellationToken);
            AnsiConsole.MarkupLine("[cyan]Triggering auto-start...[/]"); await TriggerStartupScript(token, newName);
            createStopwatch.Stop(); AnsiConsole.MarkupLine($"[bold green]✓ New CS '{newName}' created & initialized.[/] ({createStopwatch.Elapsed:mm\\:ss})"); return newName;
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("[yellow]Create cancelled.[/]"); if (!string.IsNullOrWhiteSpace(newName)) { AnsiConsole.MarkupLine($"[yellow]Cleaning up {newName}...[/]"); try { await StopCodespace(token, newName); } catch { } try { await DeleteCodespace(token, newName); } catch { } } throw; }
        catch (Exception ex) { createStopwatch.Stop(); AnsiConsole.MarkupLine($"\n[red]ERROR CREATING CODESPACE[/]"); AnsiConsole.WriteException(ex); if (!string.IsNullOrWhiteSpace(newName)) { AnsiConsole.MarkupLine($"[yellow]Deleting failed CS {newName}...[/]"); try { await DeleteCodespace(token, newName); } catch { } } string info = ""; if (ex.Message.Contains("quota")) info = " (Quota?)"; else if (ex.Message.Contains("401") || ex.Message.Contains("credentials")) info = " (Token/Perms?)"; else if (ex.Message.Contains("403")) info = " (Forbidden?)"; throw new Exception($"FATAL: Create failed{info}. Err: {ex.Message}"); }
    }

    private static async Task<List<CodespaceInfo>> ListAllCodespaces(TokenEntry token) { /* ... implementasi sama ... */ }
    private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName) { /* ... implementasi sama ... */ }
    private static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token) { /* ... implementasi sama ... */ }
    public static async Task DeleteCodespace(TokenEntry token, string codespaceName) { /* ... implementasi sama ... */ }
    public static async Task StopCodespace(TokenEntry token, string codespaceName) { /* ... implementasi sama ... */ }
    private static async Task StartCodespace(TokenEntry token, string codespaceName) { /* ... implementasi sama ... */ }
    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken, bool useFastPolling = false) { /* ... implementasi sama ... */ }
    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken, bool useFastPolling = false) { /* ... implementasi sama ... */ }
    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken) { /* ... implementasi sama ... */ }
    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName) { /* ... implementasi sama ... */ }
    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName) { /* ... implementasi sama ... */ }
    private class CodespaceInfo { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; [JsonPropertyName("state")] public string State { get; set; } = ""; [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = ""; }

} // Akhir class CodespaceManager
