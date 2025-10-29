using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Orchestrator;

public static class CodespaceManager
{
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";
    private const int SSH_COMMAND_TIMEOUT_MS = 120000; // Timeout default & untuk mkdir/cp
    private const int CREATE_TIMEOUT_MS = 600000;
    private const int STOP_TIMEOUT_MS = 120000;
    private const int START_TIMEOUT_MS = 300000;
    private const int STATE_POLL_INTERVAL_SEC = 2;
    private const int STATE_POLL_MAX_DURATION_MIN = 8;
    private const int SSH_READY_POLL_INTERVAL_SEC = 2;
    private const int SSH_READY_MAX_DURATION_MIN = 8;
    private const int SSH_PROBE_TIMEOUT_MS = 30000; // Timeout cepat hanya untuk cek SSH echo
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

    private static List<string> LoadUploadFileList()
    {
        if (!File.Exists(UploadFilesListPath)) {
            AnsiConsole.MarkupLine($"[yellow]Warn: '{UploadFilesListPath}' not found. Using defaults.[/]");
            return new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt" };
        }
        try {
            return File.ReadAllLines(UploadFilesListPath).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#")).ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error reading '{UploadFilesListPath}': {ex.Message.EscapeMarkup()}. Using defaults.[/]");
            return new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt" };
        }
    }

    private static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
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
                    cancellationToken.ThrowIfCancellationRequested();
                    task.Description = $"[green]Checking:[/] {bot.Name}";
                    if (!bot.Enabled) { task.Increment(1); continue; }

                    string localBotDir = BotConfig.GetLocalBotPath(bot.Path);
                    if (!Directory.Exists(localBotDir)) {
                        filesSkipped++; task.Increment(1); continue;
                     }

                    string remoteBotDir = Path.Combine(remoteWorkspacePath, bot.Path).Replace('\\', '/');
                    bool botProcessed = false;
                    bool remoteDirEnsured = false;

                    foreach (var credFileName in credentialFilesToUpload)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string localFilePath = Path.Combine(localBotDir, credFileName);

                        if (File.Exists(localFilePath))
                        {
                            if (!remoteDirEnsured)
                            {
                                task.Description = $"[grey]Ensuring remote dir:[/] {bot.Name}";
                                try {
                                    string mkdirArgs = $"codespace ssh -c \"{codespaceName}\" -- mkdir -p \"{remoteBotDir}\"";
                                    // === PERBAIKAN: Gunakan timeout lebih lama untuk mkdir ===
                                    await ShellHelper.RunGhCommand(token, mkdirArgs, SSH_COMMAND_TIMEOUT_MS); // Naikkan jadi 120 detik
                                    remoteDirEnsured = true;
                                } catch (OperationCanceledException) { throw;
                                } catch (Exception mkdirEx) {
                                    AnsiConsole.MarkupLine($"[red]\n   ✗ Failed create remote dir for {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()}. Skipping bot.[/]");
                                    filesSkipped += credentialFilesToUpload.Count(cf => File.Exists(Path.Combine(localBotDir, cf)));
                                    botProcessed = true;
                                    goto NextBot;
                                }
                            }

                            botProcessed = true;
                            string remoteFilePath = $"{remoteBotDir}/{credFileName}";
                            task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";
                            string localAbsPath = Path.GetFullPath(localFilePath);
                            string remoteTargetArg = $"remote:{remoteFilePath}";
                            string cpArgs = $"codespace cp --codespace \"{codespaceName}\" \"{localAbsPath}\" \"{remoteTargetArg}\"";

                            try {
                                await ShellHelper.RunGhCommand(token, cpArgs, SSH_COMMAND_TIMEOUT_MS * 2);
                                filesUploaded++;
                            } catch (OperationCanceledException) {
                                 AnsiConsole.MarkupLine($"[yellow]\n   Upload cancelled for {credFileName}.[/]");
                                 filesSkipped++;
                                 throw;
                            } catch (Exception ex) {
                                AnsiConsole.MarkupLine($"[red]\n   ✗ Failed upload {credFileName} for {bot.Name}: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                                filesSkipped++;
                            }
                            await Task.Delay(150, cancellationToken);
                        }
                    }
                NextBot:
                    if (botProcessed) { botsProcessed++; }
                    task.Increment(1);
                }
            });

        AnsiConsole.MarkupLine($"[green]✓ Credential upload finished.[/]");
        AnsiConsole.MarkupLine($"[dim]   Bots checked: {config.BotsAndTools.Count} | Bots processed: {botsProcessed} | Files uploaded: {filesUploaded} | Files skipped/failed: {filesSkipped}[/]");
    }


    public static async Task<string> EnsureHealthyCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[cyan]═══ Ensuring Codespace Runner ═══[/]");
        CodespaceInfo? codespace = null;
        Stopwatch stopwatch = Stopwatch.StartNew();

        AnsiConsole.Markup("[dim]Checking repo last commit... [/]");
        var repoLastCommit = await GetRepoLastCommitDate(token);
        cancellationToken.ThrowIfCancellationRequested();
        if (repoLastCommit.HasValue) AnsiConsole.MarkupLine($"[green]OK[/]");
        else AnsiConsole.MarkupLine("[yellow]Fetch failed[/]");

        while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Finding codespace... [/]");
            var (found, all) = await FindExistingCodespace(token);
            cancellationToken.ThrowIfCancellationRequested();

            codespace = found;

            if (codespace == null) {
                AnsiConsole.MarkupLine("[yellow]Not found[/]");
                await CleanupStuckCodespaces(token, all, null);
                cancellationToken.ThrowIfCancellationRequested();
                return await CreateNewCodespace(token, repoFullName, cancellationToken);
            }

            AnsiConsole.MarkupLine($"[green]Found[/] [dim]{codespace.Name} ({codespace.State})[/]");
            if (repoLastCommit.HasValue && !string.IsNullOrEmpty(codespace.CreatedAt)) {
                 if (DateTime.TryParse(codespace.CreatedAt, out var csCreated)) {
                    csCreated = csCreated.ToUniversalTime();
                    if (repoLastCommit.Value > csCreated) {
                        AnsiConsole.MarkupLine($"[yellow]⚠ Outdated CS. Deleting...[/]");
                        await DeleteCodespace(token, codespace.Name); codespace = null; continue;
                    }
                 }
            }

            switch (codespace.State)
            {
                case "Available":
                    AnsiConsole.MarkupLine("[cyan]State: Available. Checking SSH...[/]");
                    if (!await WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken)) {
                        AnsiConsole.MarkupLine($"[red]SSH failed. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break;
                    }
                    await UploadCredentialsToCodespace(token, codespace.Name, cancellationToken);
                    AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                    try { await TriggerStartupScript(token, codespace.Name); } catch {}
                    if (await CheckHealthWithRetry(token, codespace.Name, cancellationToken)) { AnsiConsole.MarkupLine("[green]✓ Health OK. Reusing.[/]"); stopwatch.Stop(); return codespace.Name; }
                    AnsiConsole.MarkupLine($"[yellow]Health timeout but SSH OK. Assuming healthy.[/]"); stopwatch.Stop(); return codespace.Name;

                case "Stopped": case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Starting...[/]");
                    await StartCodespace(token, codespace.Name);
                    if (!await WaitForState(token, codespace.Name, "Available", TimeSpan.FromMinutes(3), cancellationToken)) AnsiConsole.MarkupLine("[yellow]State timeout, checking SSH anyway...[/]");
                    if (!await WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken)) { AnsiConsole.MarkupLine("[red]SSH failed after start. Deleting...[/]"); await DeleteCodespace(token, codespace.Name); codespace = null; break; }
                    await UploadCredentialsToCodespace(token, codespace.Name, cancellationToken);
                    AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                     try { await TriggerStartupScript(token, codespace.Name); } catch {}
                    if (await CheckHealthWithRetry(token, codespace.Name, cancellationToken)) { AnsiConsole.MarkupLine("[green]✓ Health OK. Reusing.[/]"); stopwatch.Stop(); return codespace.Name; }
                    AnsiConsole.MarkupLine($"[yellow]Health timeout but SSH OK. Assuming healthy.[/]"); stopwatch.Stop(); return codespace.Name;

                case "Starting": case "Queued": case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Waiting {STATE_POLL_INTERVAL_SEC}s...[/]");
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000, cancellationToken); continue;

                default:
                    AnsiConsole.MarkupLine($"[red]Bad state: {codespace.State}. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name); codespace = null; break;
            }

            if (codespace == null && stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) { await Task.Delay(5000, cancellationToken); }
        }

        stopwatch.Stop();
        if (codespace != null) { AnsiConsole.MarkupLine($"\n[yellow]CS still {codespace.State} after timeout.[/]"); return codespace.Name; }
        AnsiConsole.MarkupLine($"\n[red]FATAL: No healthy codespace after timeout.[/]");
        var (_, allFinal) = await FindExistingCodespace(token); await CleanupStuckCodespaces(token, allFinal, null);
        try { return await CreateNewCodespace(token, repoFullName, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception createEx) { AnsiConsole.WriteException(createEx); throw new Exception($"FATAL: Final create failed. {createEx.Message}"); }
    }


    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"\n[cyan]═══ Creating New Codespace ═══[/]");
        AnsiConsole.MarkupLine($"[dim]Machine: {MACHINE_TYPE}, Display: {CODESPACE_DISPLAY_NAME}[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        Stopwatch createStopwatch = Stopwatch.StartNew();
        string newName = "";
        try {
            newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(newName)) throw new Exception("gh create returned empty name");

            AnsiConsole.MarkupLine($"[green]✓ Created: {newName}[/] [dim]({createStopwatch.Elapsed:mm\\:ss})[/]");
            AnsiConsole.MarkupLine("\n[cyan]Optimizing first boot...[/]");
            await Task.Delay(45000, cancellationToken);
            var currentState = await GetCodespaceState(token, newName);
             cancellationToken.ThrowIfCancellationRequested();

            AnsiConsole.MarkupLine($"[dim]State: {currentState}[/]");
            if (currentState == "Available") {
                 AnsiConsole.MarkupLine("[yellow]Restarting...[/]");
                 await StopCodespace(token, newName); await Task.Delay(8000, cancellationToken);
                 await StartCodespace(token, newName);
                 AnsiConsole.MarkupLine("[cyan]Waiting for Available...[/]");
                 if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(5), cancellationToken)) { AnsiConsole.MarkupLine("[yellow]State timeout[/]"); }
            } else {
                 AnsiConsole.MarkupLine($"[dim]Waiting for Available...[/]");
                 if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(6), cancellationToken)) { AnsiConsole.MarkupLine("[yellow]State timeout[/]"); }
            }
            cancellationToken.ThrowIfCancellationRequested();

            AnsiConsole.MarkupLine("[cyan]Waiting for SSH...[/]");
            if (!await WaitForSshReadyWithRetry(token, newName, cancellationToken)) { throw new Exception("SSH failed"); }

            await UploadCredentialsToCodespace(token, newName, cancellationToken);

            AnsiConsole.MarkupLine("[dim]Finalizing...[/]");
            await Task.Delay(5000, cancellationToken);

            AnsiConsole.MarkupLine("[green]✓ Codespace ready.[/]");
            AnsiConsole.MarkupLine("\n[cyan]Triggering Auto-Start...[/]");
            await TriggerStartupScript(token, newName);

            AnsiConsole.MarkupLine("[green]✓ Created & initialized.[/]");
            return newName;

        } catch (OperationCanceledException) {
             AnsiConsole.MarkupLine("[yellow]Creation cancelled.[/]");
             // === PERBAIKAN: Jangan delete, tapi stop ===
             if (!string.IsNullOrWhiteSpace(newName)) {
                 AnsiConsole.MarkupLine($"[yellow]Stopping partially created codespace {newName}...[/]");
                 await StopCodespace(token, newName); // <-- Ganti jadi Stop
             }
             // === AKHIR PERBAIKAN ===
             throw;
        } catch (Exception ex) {
            createStopwatch.Stop(); AnsiConsole.WriteException(ex);
            if (!string.IsNullOrWhiteSpace(newName)) { await DeleteCodespace(token, newName); }
            string info = ""; if (ex.Message.Contains("quota")) info = " (Quota?)"; else if (ex.Message.Contains("401")) info = " (Token?)";
            throw new Exception($"FATAL: Create failed{info}. {ex.Message}");
        }
    }


    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token)
    {
        string args = "codespace list --json name,displayName,state,createdAt";
        List<CodespaceInfo> allCodespaces = new List<CodespaceInfo>();
        try {
            string jsonResult = await ShellHelper.RunGhCommand(token, args);
            if (string.IsNullOrWhiteSpace(jsonResult) || jsonResult == "[]") return (null, allCodespaces);
            try { allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>(); }
            catch (JsonException ex) { AnsiConsole.MarkupLine($"[red]JSON parse error: {ex.Message}[/]"); }
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]List CS error: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");
        return (existing, allCodespaces);
    }

    private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName)
    {
        string args = $"codespace view --json state -c \"{codespaceName}\"";
        try {
            string json = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
        } catch { return null; }
    }

     private static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token)
    {
        try {
            using var client = TokenManager.CreateHttpClient(token);
            var url = $"https://api.github.com/repos/{token.Owner}/{token.Repo}/commits?per_page=1";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() == 0) return null;
            var dateStr = doc.RootElement[0].GetProperty("commit").GetProperty("committer").GetProperty("date").GetString();
            if (DateTime.TryParse(dateStr, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var result)) return result;
            return null;
        } catch { return null; }
    }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[yellow]Deleting {codespaceName}...[/]");
        try { await ShellHelper.RunGhCommand(token, $"codespace delete -c \"{codespaceName}\" --force", SSH_COMMAND_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]✓ Deleted[/]"); }
        catch (Exception ex) { if (ex.Message.Contains("404") || ex.Message.Contains("find")) AnsiConsole.MarkupLine($"[dim]Already gone[/]"); else AnsiConsole.MarkupLine($"[yellow]Delete failed: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
        await Task.Delay(3000);
    }

    private static async Task StopCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.Markup($"[dim]Stopping {codespaceName}... [/]");
        try { await ShellHelper.RunGhCommand(token, $"codespace stop --codespace \"{codespaceName}\"", STOP_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { if (ex.Message.Contains("stopped") || ex.Message.Contains("not running")) AnsiConsole.MarkupLine("[dim]Already stopped[/]"); else AnsiConsole.MarkupLine($"[yellow]Stop error: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
        await Task.Delay(2000);
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.Markup($"[dim]Starting {codespaceName}... [/]");
        try { await ShellHelper.RunGhCommand(token, $"codespace start --codespace \"{codespaceName}\"", START_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { if(!ex.Message.Contains("available")) AnsiConsole.MarkupLine($"[yellow]Start warning: {ex.Message.Split('\n').FirstOrDefault()}[/]"); else AnsiConsole.MarkupLine($"[dim]Already available[/]"); }
    }

     private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> all, string? current)
    {
        AnsiConsole.MarkupLine("[dim]Cleaning stuck codespaces...[/]"); int cleaned=0;
        foreach (var cs in all) { if (cs.Name == current || cs.State == "Deleted") continue; if (cs.DisplayName == CODESPACE_DISPLAY_NAME) { AnsiConsole.MarkupLine($"[yellow]Stuck: {cs.Name} ({cs.State}). Deleting...[/]"); await DeleteCodespace(token, cs.Name); cleaned++; } }
        if (cleaned == 0) AnsiConsole.MarkupLine("[dim]None found[/]");
    }


    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Waiting state '{targetState}' (max {timeout.TotalMinutes:F1}min)...[/]");
        while(sw.Elapsed < timeout) {
            cancellationToken.ThrowIfCancellationRequested(); var state = await GetCodespaceState(token, codespaceName);
            if (state == targetState) { AnsiConsole.MarkupLine($"[green]✓ {targetState}[/]"); return true; }
            if (state == null) { AnsiConsole.MarkupLine($"[red]Lost[/]"); return false; }
            if (state == "Failed" || state == "Error" || state.Contains("Shutting")) { AnsiConsole.MarkupLine($"[red]{state}[/]"); return false; }
            await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000, cancellationToken);
        } AnsiConsole.MarkupLine($"[yellow]Timeout[/]"); return false;
    }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Waiting SSH ready (max {SSH_READY_MAX_DURATION_MIN}min)...[/]");
        while(sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
             cancellationToken.ThrowIfCancellationRequested();
            try { string args = $"codespace ssh -c \"{codespaceName}\" -- echo ready"; string res = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); if (res.Contains("ready")) { AnsiConsole.MarkupLine("[green]✓ SSH Ready[/]"); return true; } }
            catch (OperationCanceledException) { throw; } catch { }
            await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000, cancellationToken);
        } AnsiConsole.MarkupLine($"[yellow]SSH Timeout[/]"); return false;
    }

     public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Checking health (max {HEALTH_CHECK_MAX_DURATION_MIN}min)...[/]");
        int consecutiveSshSuccess = 0; const int SSH_SUCCESS_THRESHOLD = 2;
        while(sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
             cancellationToken.ThrowIfCancellationRequested(); string result = "";
            try { string args = $"codespace ssh -c \"{codespaceName}\" -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\""; result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
                if (result.Contains("FAILED")) { AnsiConsole.MarkupLine($"[red]✗ Startup failed[/]"); return false; }
                if (result.Contains("HEALTHY")) { AnsiConsole.MarkupLine("[green]✓ Healthy[/]"); return true; }
                if (result.Contains("NOT_READY")) { consecutiveSshSuccess++; }
                if (consecutiveSshSuccess >= SSH_SUCCESS_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) { AnsiConsole.MarkupLine($"[cyan]SSH stable. Assuming startup OK.[/]"); return true; }
            } catch (OperationCanceledException) { throw; } catch { consecutiveSshSuccess = 0; }
            await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000, cancellationToken);
        } AnsiConsole.MarkupLine($"[yellow]Health Timeout[/]"); return false;
    }


    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("[cyan]Triggering auto-start.sh...[/]");
        string repoNameLower = token.Repo.ToLower(); string workspacePath = $"/workspaces/{repoNameLower}"; string remoteScript = $"{workspacePath}/auto-start.sh";
        AnsiConsole.Markup("[dim]Executing (detached)... [/]"); string cmd = $"nohup bash \"{remoteScript}\" > /tmp/startup.log 2>&1 &";
        string args = $"codespace ssh -c \"{codespaceName}\" -- {cmd}";
        try { await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn (expected): {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
    }

     public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[dim]Fetching tmux sessions from {codespaceName}...[/]");
        string args = $"codespace ssh -c \"{codespaceName}\" -- tmux list-windows -t automation_hub_bots -F \"#{{window_name}}\"";
        try { string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "dashboard" && s != "bash").OrderBy(s => s).ToList(); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Failed fetch tmux: {ex.Message.Split('\n').FirstOrDefault()}[/]"); if (ex.Message.Contains("No sessions") || ex.Message.Contains("command not found")) AnsiConsole.MarkupLine("[yellow]Tmux not ready/found.[/]"); return new List<string>(); }
    }

    private class CodespaceInfo {
        [JsonPropertyName("name")] public string Name{get;set;}="";
        [JsonPropertyName("displayName")] public string DisplayName{get;set;}="";
        [JsonPropertyName("state")] public string State{get;set;}="";
        [JsonPropertyName("createdAt")] public string CreatedAt{get;set;}="";
    }

}
