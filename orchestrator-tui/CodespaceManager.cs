using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions; // Dibutuhkan untuk Regex
using System; // Tambahkan using System untuk Exception dan TimeSpan
using System.Threading.Tasks; // Tambahkan using Task

namespace Orchestrator;

public static class CodespaceManager
{
    // --- Konstanta (tidak berubah) ---
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

    // --- Fungsi GetProjectRoot ---
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

    // --- LoadUploadFileList ---
    private static List<string> LoadUploadFileList()
    {
        var defaultList = new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt", "wallet.txt", "settings.yaml", "mnemonics.txt" };
        if (!File.Exists(UploadFilesListPath)) {
            AnsiConsole.MarkupLine($"[yellow]Warn: '{UploadFilesListPath}' not found. Using defaults.[/]");
            return defaultList;
        }
        try {
            return File.ReadAllLines(UploadFilesListPath)
                       .Select(l => l.Trim())
                       .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                       .ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error reading '{UploadFilesListPath}': {ex.Message.EscapeMarkup()}. Using defaults.[/]");
            return defaultList; // FIX CS0161
        }
        // Tidak perlu return lagi di sini karena semua path sudah return
    }

    // --- GetFilesToUploadForBot ---
    private static List<string> GetFilesToUploadForBot(string localBotDir, List<string> allPossibleFiles)
    {
        var existingFiles = new List<string>();
        if (!Directory.Exists(localBotDir)) {
             AnsiConsole.MarkupLine($"[yellow]Warn: Local dir not found: {localBotDir.EscapeMarkup()}[/]");
             return existingFiles; // Return list kosong
        }
        foreach (var fileName in allPossibleFiles) {
            var filePath = Path.Combine(localBotDir, fileName);
            if (File.Exists(filePath)) { existingFiles.Add(fileName); }
        }
        return existingFiles; // Return (bisa kosong)
        // Tidak perlu return lagi di sini
    }

    // --- UploadCredentialsToCodespace ---
    private static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        // ... (Implementasi SAMA PERSIS seperti jawaban sebelumnya, dengan verifikasi 'test -d') ...
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
                        string escapedRemoteBotDir = $"'{remoteBotDir.Replace("'", "'\\''")}'";

                        task.Description = $"[grey]Creating dir:[/] {bot.Name}";
                        bool mkdirSuccess = false;
                        try {
                            string mkdirCmd = $"mkdir -p {escapedRemoteBotDir}";
                            string sshMkdirArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                            await ShellHelper.RunGhCommand(token, sshMkdirArgs, 90000); mkdirSuccess = true;
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception mkdirEx) { AnsiConsole.MarkupLine($"[red]✗ Failed mkdir for {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }

                        bool dirExists = false;
                        if (mkdirSuccess) {
                            task.Description = $"[grey]Verifying dir:[/] {bot.Name}";
                            try {
                                await Task.Delay(500, cancellationToken); string testCmd = $"test -d {escapedRemoteBotDir}";
                                string sshTestArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{testCmd}\"";
                                await ShellHelper.RunGhCommand(token, sshTestArgs, 30000); dirExists = true;
                                AnsiConsole.MarkupLine($"[green]✓ Dir verified: {bot.Name}[/]");
                            } catch (OperationCanceledException) { throw; }
                            catch (Exception testEx) { AnsiConsole.MarkupLine($"[red]✗ Dir verify FAILED: {bot.Name}. Err: {testEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }
                        } else { AnsiConsole.MarkupLine($"[red]✗ Skipping verify for {bot.Name} (mkdir failed).[/]"); }

                        if (dirExists) {
                            foreach (var credFileName in filesToUpload) {
                                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                                string localFilePath = Path.Combine(localBotDir, credFileName); string remoteFilePath = $"{remoteBotDir}/{credFileName}";
                                task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";
                                string localAbsPath = Path.GetFullPath(localFilePath); string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteFilePath}\"";
                                try { await ShellHelper.RunGhCommand(token, cpArgs, 120000); filesUploaded++; }
                                catch (OperationCanceledException) { throw; }
                                catch { AnsiConsole.MarkupLine($"[red]✗ Fail upload: {bot.Name}/{credFileName}[/]"); filesSkipped++; }
                                try { await Task.Delay(200, cancellationToken); } catch (OperationCanceledException) { throw; }
                            }
                            botsProcessed++;
                        } else { AnsiConsole.MarkupLine($"[red]✗ Skipping uploads for {bot.Name} (dir failed).[/]"); filesSkipped += filesToUpload.Count; botsSkipped++; }
                        task.Increment(1);
                    } // End foreach bot

                    task.Description = "[cyan]Uploading ProxySync Configs...";
                    var proxySyncConfigFiles = new List<string> { "apikeys.txt", "apilist.txt" };
                    string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/proxysync/config"; string escapedRemoteProxySyncDir = $"'{remoteProxySyncConfigDir.Replace("'", "'\\''")}'";
                    bool proxySyncConfigUploadSuccess = true; bool proxySyncDirExists = false;
                    try {
                        string mkdirCmd = $"mkdir -p {escapedRemoteProxySyncDir}"; string sshMkdirArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                        await ShellHelper.RunGhCommand(token, sshMkdirArgs, 60000);
                        await Task.Delay(500, cancellationToken); string testCmd = $"test -d {escapedRemoteProxySyncDir}";
                        string sshTestArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{testCmd}\"";
                        await ShellHelper.RunGhCommand(token, sshTestArgs, 30000);
                        proxySyncDirExists = true; AnsiConsole.MarkupLine($"[green]✓ ProxySync dir verified.[/]");
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception dirEx) { AnsiConsole.MarkupLine($"[red]✗ Error ProxySync dir: {dirEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); filesSkipped += proxySyncConfigFiles.Count; proxySyncConfigUploadSuccess = false; }

                    if (proxySyncDirExists) {
                        foreach (var configFileName in proxySyncConfigFiles) {
                             if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                             string localConfigPath = Path.Combine(ConfigRoot, configFileName); string remoteConfigPath = $"{remoteProxySyncConfigDir}/{configFileName}";
                             if (!File.Exists(localConfigPath)) { AnsiConsole.MarkupLine($"[yellow]WARN: Skip non-existent: {configFileName}[/]"); continue; }
                             task.Description = $"[cyan]Uploading:[/] proxysync/{configFileName}";
                             string localAbsPath = Path.GetFullPath(localConfigPath); string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteConfigPath}\"";
                             try { await ShellHelper.RunGhCommand(token, cpArgs, 60000); filesUploaded++; }
                             catch (OperationCanceledException) { throw; }
                             catch { filesSkipped++; proxySyncConfigUploadSuccess = false; AnsiConsole.MarkupLine($"[red]✗ Fail upload: proxysync/{configFileName}[/]"); }
                             try { await Task.Delay(100, cancellationToken); } catch (OperationCanceledException) { throw; }
                        }
                    } else { AnsiConsole.MarkupLine($"[red]✗ Skipping ProxySync uploads (dir failed).[/]"); }
                    if (proxySyncConfigUploadSuccess && proxySyncDirExists) AnsiConsole.MarkupLine("[green]✓ ProxySync configs uploaded.[/]"); else AnsiConsole.MarkupLine("[yellow]! Some ProxySync uploads failed.[/]");
                    task.Increment(1);
                }); // End Progress
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Upload cancelled.[/]"); AnsiConsole.MarkupLine($"[dim]   Partial: Bots OK: {botsProcessed}, Skip: {botsSkipped} | Files OK: {filesUploaded}, Fail: {filesSkipped}[/]"); throw; }
        catch (Exception uploadEx) { AnsiConsole.MarkupLine("\n[red]UNEXPECTED UPLOAD ERROR[/]"); AnsiConsole.WriteException(uploadEx); throw; }
        AnsiConsole.MarkupLine($"\n[green]✓ Upload finished.[/]"); AnsiConsole.MarkupLine($"[dim]   Bots OK: {botsProcessed}, Skip: {botsSkipped} | Files OK: {filesUploaded}, Fail: {filesSkipped}[/]");
    }

    // --- EnsureHealthyCodespace ---
    public static async Task<string> EnsureHealthyCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        // ... (Implementasi SAMA PERSIS seperti jawaban sebelumnya) ...
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
                AnsiConsole.MarkupLine($"[green]Found:[/] [blue]{codespace.Name.EscapeMarkup()}[/] [dim]({codespace.State.EscapeMarkup()})[/]");
                cancellationToken.ThrowIfCancellationRequested();
                if (repoLastCommit.HasValue && !string.IsNullOrEmpty(codespace.CreatedAt)) {
                    if (DateTime.TryParse(codespace.CreatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var csCreated)) {
                        if (repoLastCommit.Value > csCreated) { AnsiConsole.MarkupLine($"[yellow]⚠ Outdated CS. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; AnsiConsole.MarkupLine("[dim]Waiting 5s...[/]"); await Task.Delay(5000, cancellationToken); continue; }
                    } else AnsiConsole.MarkupLine($"[yellow]Warn: Could not parse CS date '{codespace.CreatedAt.EscapeMarkup()}'[/]");
                }
                cancellationToken.ThrowIfCancellationRequested();
                switch (codespace.State) {
                    case "Available":
                        AnsiConsole.MarkupLine("[cyan]State: Available. Verifying SSH & Uploading...[/]");
                        if (!await WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken, useFastPolling: false)) { AnsiConsole.MarkupLine($"[red]SSH failed for {codespace.Name.EscapeMarkup()}. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break; }
                        await UploadCredentialsToCodespace(token, codespace.Name, cancellationToken);
                        AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                        try { await TriggerStartupScript(token, codespace.Name); } catch { }
                        if (await CheckHealthWithRetry(token, codespace.Name, cancellationToken)) { AnsiConsole.MarkupLine("[green]✓ Health OK. Ready.[/]"); stopwatch.Stop(); return codespace.Name; }
                        else { var lastState = await GetCodespaceState(token, codespace.Name); if (lastState == "Available") { AnsiConsole.MarkupLine($"[yellow]WARN: Health timeout, state 'Available'. Using anyway.[/]"); stopwatch.Stop(); return codespace.Name; } else { AnsiConsole.MarkupLine($"[red]Health failed & state '{lastState?.EscapeMarkup() ?? "Unknown"}'. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break; } }
                    case "Stopped": case "Shutdown":
                        AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Starting...[/]"); await StartCodespace(token, codespace.Name);
                        if (!await WaitForState(token, codespace.Name, "Available", TimeSpan.FromMinutes(4), cancellationToken, useFastPolling: false)) { AnsiConsole.MarkupLine("[red]Failed start. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break; }
                        AnsiConsole.MarkupLine("[green]Started. Re-checking...[/]"); await Task.Delay(STATE_POLL_INTERVAL_SLOW_SEC * 1000, cancellationToken); continue;
                    case "Starting": case "Queued": case "Rebuilding": case "Creating":
                        AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Waiting {STATE_POLL_INTERVAL_SLOW_SEC}s...[/]"); await Task.Delay(STATE_POLL_INTERVAL_SLOW_SEC * 1000, cancellationToken); continue;
                    default: AnsiConsole.MarkupLine($"[red]Unhealthy state: '{codespace.State.EscapeMarkup()}'. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break;
                }
                if (codespace == null) { AnsiConsole.MarkupLine("[dim]Waiting 5s...[/]"); await Task.Delay(5000, cancellationToken); }
            } // End while
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]EnsureHealthy cancelled.[/]"); stopwatch.Stop(); throw; }
        catch (Exception ex) { stopwatch.Stop(); AnsiConsole.MarkupLine($"\n[red]FATAL EnsureHealthy:[/]"); AnsiConsole.WriteException(ex); if (codespace != null && !string.IsNullOrEmpty(codespace.Name)) { AnsiConsole.MarkupLine($"[yellow]Deleting broken CS {codespace.Name.EscapeMarkup()}...[/]"); try { await DeleteCodespace(token, codespace.Name); } catch { } } throw; }
        // === FIX CS0161: Tambah return/throw di akhir path utama ===
        // Secara logika path ini tidak akan tercapai karena loop while punya timeout throw exception
        // Tapi compiler butuh kepastian.
        stopwatch.Stop(); // Hentikan stopwatch jika keluar loop normal (seharusnya tidak)
        AnsiConsole.MarkupLine($"\n[red]FATAL: Reached end of EnsureHealthyCodespace loop unexpectedly.[/]");
        throw new Exception("Reached end of EnsureHealthyCodespace loop unexpectedly."); // Throw exception jika keluar loop
    }

    // --- CreateNewCodespace ---
    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        // ... (Implementasi SAMA PERSIS seperti jawaban sebelumnya, tanpa --web) ...
        AnsiConsole.MarkupLine($"\n[cyan]Attempting create new codespace...[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m"; // NO --web
        Stopwatch createStopwatch = Stopwatch.StartNew(); string newName = "";
        try {
            AnsiConsole.MarkupLine("[dim]Running 'gh codespace create'...[/]"); newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS); cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(newName) || !newName.Contains(CODESPACE_DISPLAY_NAME)) { AnsiConsole.MarkupLine($"[yellow]WARN: Unexpected 'gh create' output. Fallback list...[/]"); newName = ""; }
            else { newName = newName.Trim(); AnsiConsole.MarkupLine($"[green]✓ Create command likely OK: {newName.EscapeMarkup()}[/] ({createStopwatch.Elapsed:mm\\:ss})"); }
            if (string.IsNullOrWhiteSpace(newName)) { AnsiConsole.MarkupLine("[dim]Waiting 3s before listing...[/]"); await Task.Delay(3000, cancellationToken); var list = await ListAllCodespaces(token); var found = list.Where(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME).OrderByDescending(cs => cs.CreatedAt).FirstOrDefault(); if (found == null || string.IsNullOrWhiteSpace(found.Name)) throw new Exception("gh create failed & fallback list empty"); newName = found.Name; AnsiConsole.MarkupLine($"[green]✓ Fallback found: {newName.EscapeMarkup()}[/]"); }
            AnsiConsole.MarkupLine("[cyan]Waiting for Available state...[/]"); if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(6), cancellationToken, useFastPolling: true)) throw new Exception($"CS '{newName}' failed reach Available"); // FAST poll
            AnsiConsole.MarkupLine("[cyan]State Available. Verifying SSH...[/]"); if (!await WaitForSshReadyWithRetry(token, newName, cancellationToken, useFastPolling: true)) throw new Exception($"SSH to '{newName}' failed"); // FAST poll
            AnsiConsole.MarkupLine("[cyan]SSH OK. Uploading credentials...[/]"); await UploadCredentialsToCodespace(token, newName, cancellationToken);
            AnsiConsole.MarkupLine("[dim]Finalizing...[/]"); await Task.Delay(5000, cancellationToken);
            AnsiConsole.MarkupLine("[cyan]Triggering auto-start...[/]"); await TriggerStartupScript(token, newName);
            createStopwatch.Stop(); AnsiConsole.MarkupLine($"[bold green]✓ New CS '{newName.EscapeMarkup()}' created & initialized.[/] ({createStopwatch.Elapsed:mm\\:ss})"); return newName; // Return nama codespace
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("[yellow]Create cancelled.[/]"); if (!string.IsNullOrWhiteSpace(newName)) { AnsiConsole.MarkupLine($"[yellow]Cleaning up {newName.EscapeMarkup()}...[/]"); try { await StopCodespace(token, newName); } catch { } try { await DeleteCodespace(token, newName); } catch { } } throw; }
        catch (Exception ex) { createStopwatch.Stop(); AnsiConsole.MarkupLine($"\n[red]ERROR CREATING CODESPACE[/]"); AnsiConsole.WriteException(ex); if (!string.IsNullOrWhiteSpace(newName)) { AnsiConsole.MarkupLine($"[yellow]Deleting failed CS {newName.EscapeMarkup()}...[/]"); try { await DeleteCodespace(token, newName); } catch { } } string info = ""; if (ex.Message.Contains("quota")) info = " (Quota?)"; else if (ex.Message.Contains("401") || ex.Message.Contains("credentials")) info = " (Token/Perms?)"; else if (ex.Message.Contains("403")) info = " (Forbidden?)"; throw new Exception($"FATAL: Create failed{info}. Err: {ex.Message}"); }
        // === FIX CS0161: Tambah return/throw di akhir path utama ===
        // Secara logika path ini tidak akan tercapai karena try-catch akan throw atau return
        // throw new Exception("Reached end of CreateNewCodespace unexpectedly."); // Throw exception jika keluar try-catch
    }

    // --- DeleteCodespace, StopCodespace, StartCodespace ---
    public static async Task DeleteCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[yellow]Attempting delete codespace '{codespaceName.EscapeMarkup()}'...[/]");
        try { string args = $"codespace delete -c \"{codespaceName}\" --force"; await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); AnsiConsole.MarkupLine($"[green]✓ Delete command sent for '{codespaceName.EscapeMarkup()}'.[/]"); }
        catch (Exception ex) { if (ex.Message.Contains("404") || ex.Message.Contains("find")) AnsiConsole.MarkupLine($"[dim]Codespace '{codespaceName.EscapeMarkup()}' already gone.[/]"); else AnsiConsole.MarkupLine($"[yellow]Warn: Delete failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }
        await Task.Delay(3000);
    }
    public static async Task StopCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.Markup($"[dim]Attempting stop codespace '{codespaceName.EscapeMarkup()}'... [/]");
        try { string args = $"codespace stop --codespace \"{codespaceName}\""; await ShellHelper.RunGhCommand(token, args, STOP_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { if (ex.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase)) AnsiConsole.MarkupLine("[dim]Already stopped.[/]"); else AnsiConsole.MarkupLine($"[yellow]Warn: Stop failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }
        await Task.Delay(2000);
    }
    private static async Task StartCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.Markup($"[dim]Attempting start codespace '{codespaceName.EscapeMarkup()}'... [/]");
        try { string args = $"codespace start --codespace \"{codespaceName}\""; await ShellHelper.RunGhCommand(token, args, START_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { if (ex.Message.Contains("available", StringComparison.OrdinalIgnoreCase)) AnsiConsole.MarkupLine($"[dim]Already available.[/]"); else AnsiConsole.MarkupLine($"[yellow]Warn: Start failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }
    }

    // --- TriggerStartupScript ---
    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("[cyan]Triggering remote auto-start.sh script...[/]");
        string repo = token.Repo.ToLower(); string scriptPath = $"/workspaces/{repo}/auto-start.sh";
        AnsiConsole.Markup("[dim]Executing command in background (nohup)... [/]");
        string command = $"nohup bash \"{scriptPath.Replace("\"", "\\\"")}\" > /tmp/startup.log 2>&1 &";
        string args = $"codespace ssh -c \"{codespaceName}\" -- {command}";
        try { await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn: Failed trigger auto-start: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }
    }

    // === IMPLEMENTASI LENGKAP FUNGSI HELPER YANG BENAR ===

    private static async Task<List<CodespaceInfo>> ListAllCodespaces(TokenEntry token)
    {
        string args = "codespace list --json name,displayName,state,createdAt";
        try {
            string jsonResult = await ShellHelper.RunGhCommand(token, args);
            if (string.IsNullOrWhiteSpace(jsonResult) || jsonResult == "[]") return new List<CodespaceInfo>();
            try { return JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>(); }
            catch (JsonException jEx) { AnsiConsole.MarkupLine($"[yellow]Warn: Parse list JSON failed: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); return new List<CodespaceInfo>(); } // FIX CS0161
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error listing codespaces: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); return new List<CodespaceInfo>(); } // FIX CS0161
    }

    private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName)
    {
        try {
            string args = $"codespace view --json state -c \"{codespaceName}\"";
            string json = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
        } catch (JsonException jEx) { AnsiConsole.MarkupLine($"[yellow]Warn: Parse state JSON failed: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); return null; } // FIX CS0161
        catch (Exception) { return null; } // FIX CS0161
    }

    private static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token)
    {
        try {
            using var client = TokenManager.CreateHttpClient(token); client.Timeout = TimeSpan.FromSeconds(30);
            var response = await client.GetAsync($"https://api.github.com/repos/{token.Owner}/{token.Repo}/commits?per_page=1");
            if (!response.IsSuccessStatusCode) { AnsiConsole.MarkupLine($"[yellow]Warn: Fetch commit failed ({response.StatusCode}).[/]"); return null; } // FIX CS0161
            var json = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) { AnsiConsole.MarkupLine($"[yellow]Warn: No commits found?[/]"); return null; } // FIX CS0161
            var dateString = doc.RootElement[0].GetProperty("commit").GetProperty("committer").GetProperty("date").GetString();
            if (DateTime.TryParse(dateString, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)) return dt;
            else { AnsiConsole.MarkupLine($"[yellow]Warn: Parse commit date failed: {dateString?.EscapeMarkup()}[/]"); return null; } // FIX CS0161
        } catch (JsonException jEx) { AnsiConsole.MarkupLine($"[red]Error parse commit JSON: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); return null; } // FIX CS0161
        catch (HttpRequestException httpEx) { AnsiConsole.MarkupLine($"[red]Error fetch commit (Network): {httpEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); return null; } // FIX CS0161
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetch commit: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); return null; } // FIX CS0161
    }

    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken, bool useFastPolling = false)
    {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Waiting state '{targetState}'...[/]");
        int pollIntervalMs = useFastPolling ? STATE_POLL_INTERVAL_FAST_MS : STATE_POLL_INTERVAL_SLOW_SEC * 1000;
        while (sw.Elapsed < timeout) {
            cancellationToken.ThrowIfCancellationRequested(); string? state = await GetCodespaceState(token, codespaceName); cancellationToken.ThrowIfCancellationRequested();
            if (state == targetState) { AnsiConsole.MarkupLine($"[green]✓ Reached '{targetState}'[/]"); return true; } // FIX CS0161
            if (state == null || state == "Failed" || state == "Error" || state.Contains("Shutting") || state == "Deleted") { AnsiConsole.MarkupLine($"[red]✗ Failure state ('{state ?? "Unknown"}')[/]"); return false; } // FIX CS0161
            AnsiConsole.Markup($"[dim].[/]"); try { await Task.Delay(pollIntervalMs, cancellationToken); } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled waiting state[/]"); throw; }
        }
        AnsiConsole.MarkupLine($"[yellow]Timeout waiting state '{targetState}'[/]");
        return false; // FIX CS0161
    }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken, bool useFastPolling = false)
    {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Waiting SSH...[/]");
        int pollIntervalMs = useFastPolling ? SSH_READY_POLL_INTERVAL_FAST_MS : SSH_READY_POLL_INTERVAL_SLOW_SEC * 1000;
        while (sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
            cancellationToken.ThrowIfCancellationRequested(); try {
                string args = $"codespace ssh -c \"{codespaceName}\" -- echo ready"; string res = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); cancellationToken.ThrowIfCancellationRequested();
                if (res != null && res.Contains("ready")) { AnsiConsole.MarkupLine("[green]✓ SSH Ready[/]"); return true; } // FIX CS0161
                AnsiConsole.Markup($"[dim]?[/] ");
            } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled waiting SSH[/]"); throw; }
            catch { AnsiConsole.Markup($"[dim]x[/]"); }
            try { await Task.Delay(pollIntervalMs, cancellationToken); } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled waiting SSH[/]"); throw; }
        }
        AnsiConsole.MarkupLine($"[yellow]Timeout waiting SSH[/]");
        return false; // FIX CS0161
    }

    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Checking health...[/]");
        int successfulSshChecks = 0; const int SSH_STABILITY_THRESHOLD = 2;
        while (sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
            cancellationToken.ThrowIfCancellationRequested(); string result = "";
            try {
                string args = $"codespace ssh -c \"{codespaceName}\" -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
                result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); cancellationToken.ThrowIfCancellationRequested();
                if (result.Contains("FAILED")) { AnsiConsole.MarkupLine($"[red]✗ Script failed[/]"); return false; } // FIX CS0161
                if (result.Contains("HEALTHY")) { AnsiConsole.MarkupLine("[green]✓ Healthy[/]"); return true; } // FIX CS0161
                if (result.Contains("NOT_READY")) { AnsiConsole.Markup($"[dim]_[/]"); successfulSshChecks++; if (successfulSshChecks >= SSH_STABILITY_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) { AnsiConsole.MarkupLine($"[cyan]✓ SSH stable, assuming OK[/]"); return true; } } // FIX CS0161
                else { AnsiConsole.Markup($"[yellow]?[/]"); successfulSshChecks = 0; }
            } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled checking health[/]"); throw; }
            catch { AnsiConsole.Markup($"[red]x[/]"); successfulSshChecks = 0; }
            try { await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000, cancellationToken); } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled checking health[/]"); throw; }
        }
        AnsiConsole.MarkupLine($"[yellow]Timeout checking health[/]");
        return false; // FIX CS0161
    }

    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[dim]Fetching tmux sessions...[/]");
        string args = $"codespace ssh -c \"{codespaceName}\" -- tmux list-windows -t automation_hub_bots -F \"#{{window_name}}\"";
        try {
            string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS);
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "dashboard" && s != "bash").OrderBy(s => s).ToList(); // FIX CS0161
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Failed fetch tmux: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); AnsiConsole.MarkupLine($"[dim](Normal if new/stopped)[/]");
            return new List<string>(); // FIX CS0161
        }
        // Tidak perlu return lagi di sini
    }

    // Class DTO JSON
    private class CodespaceInfo { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; [JsonPropertyName("state")] public string State { get; set; } = ""; [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = ""; }

} // Akhir class CodespaceManager
