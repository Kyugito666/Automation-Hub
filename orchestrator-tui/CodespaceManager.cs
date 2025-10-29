using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
// === Tambah using untuk Regex ===
using System.Text.RegularExpressions;

namespace Orchestrator;

public static class CodespaceManager
{
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";
    private const int SSH_COMMAND_TIMEOUT_MS = 120000;
    private const int CREATE_TIMEOUT_MS = 600000; // 10 menit
    private const int STOP_TIMEOUT_MS = 120000;
    private const int START_TIMEOUT_MS = 300000;
    private const int STATE_POLL_INTERVAL_FAST_MS = 500; // 500ms untuk create baru
    private const int STATE_POLL_INTERVAL_SLOW_SEC = 3;  // 3s untuk check/start
    private const int STATE_POLL_MAX_DURATION_MIN = 8;
    private const int SSH_READY_POLL_INTERVAL_FAST_MS = 500; // 500ms untuk create baru
    private const int SSH_READY_POLL_INTERVAL_SLOW_SEC = 2;  // 2s untuk check/start
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

    // Fungsi helper baru untuk mendapatkan root project (lebih robust)
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
            return new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt", "wallet.txt", "settings.yaml", "mnemonics.txt" };
        }
        try {
            return File.ReadAllLines(UploadFilesListPath).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#")).ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error reading '{UploadFilesListPath}': {ex.Message.EscapeMarkup()}. Using defaults.[/]");
            return new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt", "wallet.txt", "settings.yaml", "mnemonics.txt" };
        }
    }

    private static List<string> GetFilesToUploadForBot(string localBotDir, List<string> allPossibleFiles)
    {
        var existingFiles = new List<string>();
        foreach (var fileName in allPossibleFiles)
        {
            var filePath = Path.Combine(localBotDir, fileName);
            if (File.Exists(filePath)) { existingFiles.Add(fileName); }
        }
        return existingFiles;
    }

    private static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[cyan]═══ Uploading Credentials & Configs via gh cp ═══[/]");
        var config = BotConfig.Load();
        if (config == null) {
             AnsiConsole.MarkupLine("[red]✗ Gagal memuat bots_config.json. Upload dibatalkan.[/]");
             return;
        }

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

                        if (bot.Name == "ProxySync-Tool") {
                            AnsiConsole.MarkupLine($"[dim]SKIP Creds: {bot.Name} (handled separately)[/]");
                            task.Increment(1); continue;
                        }
                        if (!bot.Enabled) {
                            AnsiConsole.MarkupLine($"[dim]SKIP Disabled: {bot.Name}[/]");
                            task.Increment(1); continue;
                        }

                        string localBotDir = BotConfig.GetLocalBotPath(bot.Path);
                        if (!Directory.Exists(localBotDir)) {
                            AnsiConsole.MarkupLine($"[yellow]SKIP No Local Dir: {bot.Name} ({localBotDir.EscapeMarkup()})[/]");
                            botsSkipped++; task.Increment(1); continue;
                        }

                        var filesToUpload = GetFilesToUploadForBot(localBotDir, botCredentialFiles);
                        if (!filesToUpload.Any()) {
                            AnsiConsole.MarkupLine($"[dim]SKIP No Creds Found: {bot.Name}[/]");
                            botsSkipped++; task.Increment(1); continue;
                        }

                        string remoteBotDir = Path.Combine(remoteWorkspacePath, bot.Path).Replace('\\', '/');
                        task.Description = $"[grey]Creating dir:[/] {bot.Name}";

                        try {
                            string mkdirCmd = $"mkdir -p '{remoteBotDir.Replace("'", "'\\''")}'";
                            string sshArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                            await ShellHelper.RunGhCommand(token, sshArgs, 90000); // Timeout 90s
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception mkdirEx) {
                            AnsiConsole.MarkupLine($"[red]✗ Failed mkdir {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()}[/]");
                            filesSkipped += filesToUpload.Count; botsSkipped++; task.Increment(1); continue;
                        }

                        try { await Task.Delay(2000, cancellationToken); } catch (OperationCanceledException) { throw; } // Delay 2s

                        foreach (var credFileName in filesToUpload)
                        {
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                            string localFilePath = Path.Combine(localBotDir, credFileName);
                            string remoteFilePath = $"{remoteBotDir}/{credFileName}";
                            task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";
                            string localAbsPath = Path.GetFullPath(localFilePath);
                            string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteFilePath}\"";
                            try {
                                await ShellHelper.RunGhCommand(token, cpArgs, 120000); // Timeout cp 120s
                                filesUploaded++;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { filesSkipped++; }
                            try { await Task.Delay(200, cancellationToken); } catch (OperationCanceledException) { throw; } // Delay antar file
                        }
                        botsProcessed++; task.Increment(1);
                    } // End foreach bot

                    // Upload config ProxySync
                    task.Description = "[cyan]Uploading ProxySync Configs...";
                    var proxySyncConfigFiles = new List<string> { "apikeys.txt", "apilist.txt" };
                    string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/proxysync/config";
                    bool proxySyncConfigUploadSuccess = true;
                    try {
                        string mkdirCmd = $"mkdir -p '{remoteProxySyncConfigDir.Replace("'", "'\\''")}'";
                        string sshArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                        await ShellHelper.RunGhCommand(token, sshArgs, 60000); // Timeout 60s
                        foreach (var configFileName in proxySyncConfigFiles) {
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                            string localConfigPath = Path.Combine(ConfigRoot, configFileName);
                            string remoteConfigPath = $"{remoteProxySyncConfigDir}/{configFileName}";
                            if (!File.Exists(localConfigPath)) {
                                AnsiConsole.MarkupLine($"[yellow]WARN: Local ProxySync config '{configFileName}' not found. Skipping.[/]"); continue;
                            }
                            task.Description = $"[cyan]Uploading:[/] proxysync/{configFileName}";
                            string localAbsPath = Path.GetFullPath(localConfigPath);
                            string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteConfigPath}\"";
                            try {
                                await ShellHelper.RunGhCommand(token, cpArgs, 60000); // Timeout 60s
                                filesUploaded++;
                            } catch (OperationCanceledException) { throw; }
                            catch { filesSkipped++; proxySyncConfigUploadSuccess = false; AnsiConsole.MarkupLine($"[red]✗ Failed to upload {configFileName} for ProxySync.[/]"); }
                            try { await Task.Delay(100, cancellationToken); } catch (OperationCanceledException) { throw; }
                        }
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗ Error setting up ProxySync config dir: {ex.Message.Split('\n').FirstOrDefault()}[/]"); filesSkipped += proxySyncConfigFiles.Count; proxySyncConfigUploadSuccess = false; }
                    if (proxySyncConfigUploadSuccess) AnsiConsole.MarkupLine("[green]✓ ProxySync configs uploaded.[/]");
                    else AnsiConsole.MarkupLine("[yellow]! Some ProxySync configs failed to upload.[/]");
                    task.Increment(1); // Increment for ProxySync task
                }); // End Progress
        } catch (OperationCanceledException) {
            AnsiConsole.MarkupLine("\n[yellow]Upload process cancelled by user.[/]");
            AnsiConsole.MarkupLine($"[dim]   Partial Status: Bots Processed: {botsProcessed}, Files OK: {filesUploaded}, Files Failed: {filesSkipped}[/]"); throw;
        } catch (Exception uploadEx) {
            AnsiConsole.MarkupLine("\n[red]━━━ UNEXPECTED UPLOAD ERROR ━━━[/]"); AnsiConsole.WriteException(uploadEx); throw;
        }
        AnsiConsole.MarkupLine($"\n[green]✓ Upload process finished.[/]");
        AnsiConsole.MarkupLine($"[dim]   Bots Creds: {botsProcessed} processed, {botsSkipped} skipped | Files (incl. ProxySync): {filesUploaded} OK, {filesSkipped} failed[/]");
    }

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

                if (codespace == null) {
                    AnsiConsole.MarkupLine("[yellow]Not found.[/]");
                    return await CreateNewCodespace(token, repoFullName, cancellationToken);
                }
                AnsiConsole.MarkupLine($"[green]Found:[/] [blue]{codespace.Name}[/] [dim]({codespace.State})[/]");
                cancellationToken.ThrowIfCancellationRequested();
                if (repoLastCommit.HasValue && !string.IsNullOrEmpty(codespace.CreatedAt)) {
                    if (DateTime.TryParse(codespace.CreatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var csCreated)) {
                        if (repoLastCommit.Value > csCreated) {
                            AnsiConsole.MarkupLine($"[yellow]⚠ Codespace created ({csCreated:yyyy-MM-dd HH:mm}) before last commit ({repoLastCommit.Value:yyyy-MM-dd HH:mm}). Deleting...[/]");
                            await DeleteCodespace(token, codespace.Name); codespace = null;
                            AnsiConsole.MarkupLine("[dim]Waiting 5s before retry...[/]"); await Task.Delay(5000, cancellationToken); continue;
                        }
                    } else AnsiConsole.MarkupLine($"[yellow]Warn: Could not parse codespace creation date '{codespace.CreatedAt}'[/]");
                }
                cancellationToken.ThrowIfCancellationRequested();
                switch (codespace.State) {
                    case "Available":
                        AnsiConsole.MarkupLine("[cyan]State: Available. Verifying SSH & Uploading...[/]");
                        if (!await WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken, useFastPolling: false)) { // SLOW poll
                            AnsiConsole.MarkupLine($"[red]SSH verification failed for {codespace.Name}. Deleting...[/]");
                            await DeleteCodespace(token, codespace.Name); codespace = null; break;
                        }
                        await UploadCredentialsToCodespace(token, codespace.Name, cancellationToken);
                        AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                        try { await TriggerStartupScript(token, codespace.Name); } catch { }
                        if (await CheckHealthWithRetry(token, codespace.Name, cancellationToken)) {
                            AnsiConsole.MarkupLine("[green]✓ Health OK. Codespace ready.[/]"); stopwatch.Stop(); return codespace.Name;
                        } else {
                            var lastState = await GetCodespaceState(token, codespace.Name);
                            if (lastState == "Available") {
                                AnsiConsole.MarkupLine($"[yellow]WARN: Health check timed out, but state is 'Available'. Using codespace anyway.[/]");
                                stopwatch.Stop(); return codespace.Name;
                            } else {
                                AnsiConsole.MarkupLine($"[red]Health check failed and state is '{lastState ?? "Unknown"}'. Deleting unhealthy codespace...[/]");
                                await DeleteCodespace(token, codespace.Name); codespace = null; break;
                            }
                        }
                    case "Stopped": case "Shutdown":
                        AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Attempting to start...[/]");
                        await StartCodespace(token, codespace.Name);
                        if (!await WaitForState(token, codespace.Name, "Available", TimeSpan.FromMinutes(4), cancellationToken, useFastPolling: false)) { // SLOW poll
                            AnsiConsole.MarkupLine("[red]Failed to reach 'Available' state after start. Deleting...[/]");
                            await DeleteCodespace(token, codespace.Name); codespace = null; break;
                        }
                        AnsiConsole.MarkupLine("[green]Started. Re-checking status in next loop cycle...[/]");
                        await Task.Delay(STATE_POLL_INTERVAL_SLOW_SEC * 1000, cancellationToken); continue;
                    case "Starting": case "Queued": case "Rebuilding": case "Creating":
                        AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Waiting {STATE_POLL_INTERVAL_SLOW_SEC}s...[/]");
                        await Task.Delay(STATE_POLL_INTERVAL_SLOW_SEC * 1000, cancellationToken); continue;
                    default:
                        AnsiConsole.MarkupLine($"[red]Unhealthy or unknown state: '{codespace.State}'. Deleting...[/]");
                        await DeleteCodespace(token, codespace.Name); codespace = null; break;
                }
                if (codespace == null) { AnsiConsole.MarkupLine("[dim]Waiting 5s before retry...[/]"); await Task.Delay(5000, cancellationToken); }
            } // End while
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]EnsureHealthyCodespace cancelled by user.[/]"); stopwatch.Stop(); throw; }
        catch (Exception ex) { stopwatch.Stop(); AnsiConsole.MarkupLine($"\n[red]FATAL error during EnsureHealthyCodespace:[/]"); AnsiConsole.WriteException(ex); if (codespace != null && !string.IsNullOrEmpty(codespace.Name)) { AnsiConsole.MarkupLine($"[yellow]Attempting to delete potentially broken codespace {codespace.Name}...[/]"); try { await DeleteCodespace(token, codespace.Name); } catch { } } throw; }
        stopwatch.Stop(); AnsiConsole.MarkupLine($"\n[red]FATAL: Could not ensure a healthy codespace within {STATE_POLL_MAX_DURATION_MIN} minutes.[/]"); if (codespace != null && !string.IsNullOrEmpty(codespace.Name)) { AnsiConsole.MarkupLine($"[yellow]Attempting to delete last known codespace {codespace.Name}...[/]"); try { await DeleteCodespace(token, codespace.Name); } catch { } } throw new Exception($"Failed to ensure healthy codespace after multiple attempts.");
    }

    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"\n[cyan]Attempting to create a new codespace...[/]");
        // === FIX: Hapus --web ===
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        Stopwatch createStopwatch = Stopwatch.StartNew();
        string newName = "";
        try {
            AnsiConsole.MarkupLine("[dim]Running 'gh codespace create'...[/]");
            newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS);
            cancellationToken.ThrowIfCancellationRequested();

            // Output 'gh codespace create' (tanpa --web) biasanya nama codespace atau pesan sukses yg mengandung nama
            if (string.IsNullOrWhiteSpace(newName)) {
                 // Mungkin outputnya hanya pesan sukses tanpa nama eksplisit? Coba fallback.
                 AnsiConsole.MarkupLine($"[yellow]WARN: 'gh create' output empty? Trying ListAllCodespaces...[/]");
            } else if (!newName.Contains(CODESPACE_DISPLAY_NAME)) { // Cek jika outputnya bukan nama
                 AnsiConsole.MarkupLine($"[yellow]WARN: Output 'gh create' ({newName.Split('\n').FirstOrDefault()}) might not be the name. Trying ListAllCodespaces...[/]");
                 // Reset newName biar fallback jalan
                 newName = "";
            } else {
                // Asumsi output adalah nama codespace
                newName = newName.Trim(); // Bersihkan whitespace
                AnsiConsole.MarkupLine($"[green]✓ Create command likely succeeded: {newName}[/] ({createStopwatch.Elapsed:mm\\:ss})");
            }

            // Fallback jika newName masih kosong atau dianggap tidak valid
            if (string.IsNullOrWhiteSpace(newName)) {
                AnsiConsole.MarkupLine("[dim]Waiting 3s before listing...[/]");
                await Task.Delay(3000, cancellationToken);
                var list = await ListAllCodespaces(token);
                var found = list.Where(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME)
                              .OrderByDescending(cs => cs.CreatedAt)
                              .FirstOrDefault();
                if (found == null || string.IsNullOrWhiteSpace(found.Name)) {
                    throw new Exception("gh codespace create command returned unexpected output and list failed to find the new codespace");
                }
                newName = found.Name;
                AnsiConsole.MarkupLine($"[green]✓ Fallback found new name: {newName}[/]");
            }

            AnsiConsole.MarkupLine("[cyan]Waiting for codespace to become fully available...[/]");
            if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(6), cancellationToken, useFastPolling: true)) { // FAST poll
                throw new Exception($"Codespace '{newName}' failed to reach 'Available' state within timeout.");
            }

            AnsiConsole.MarkupLine("[cyan]State is Available. Verifying SSH connection...[/]");
            if (!await WaitForSshReadyWithRetry(token, newName, cancellationToken, useFastPolling: true)) { // FAST poll
                throw new Exception($"SSH connection to '{newName}' failed after becoming available.");
            }

            AnsiConsole.MarkupLine("[cyan]SSH OK. Uploading credentials and configs...[/]");
            await UploadCredentialsToCodespace(token, newName, cancellationToken);

            AnsiConsole.MarkupLine("[dim]Finalizing setup...[/]");
            await Task.Delay(5000, cancellationToken);

            AnsiConsole.MarkupLine("[cyan]Triggering auto-start script...[/]");
            await TriggerStartupScript(token, newName);

            createStopwatch.Stop();
            AnsiConsole.MarkupLine($"[bold green]✓ New codespace '{newName}' created and initialized successfully.[/] ({createStopwatch.Elapsed:mm\\:ss})");
            return newName;

        } catch (OperationCanceledException) {
             AnsiConsole.MarkupLine("[yellow]Codespace creation cancelled by user.[/]");
             if (!string.IsNullOrWhiteSpace(newName)) { AnsiConsole.MarkupLine($"[yellow]Attempting to clean up partially created codespace {newName}...[/]"); try { await StopCodespace(token, newName); } catch { } try { await DeleteCodespace(token, newName); } catch { } } throw;
        } catch (Exception ex) {
            createStopwatch.Stop(); AnsiConsole.MarkupLine($"\n[red]━━━ ERROR CREATING CODESPACE ━━━[/]"); AnsiConsole.WriteException(ex);
            if (!string.IsNullOrWhiteSpace(newName)) { AnsiConsole.MarkupLine($"[yellow]Attempting to delete failed codespace {newName}...[/]"); try { await DeleteCodespace(token, newName); } catch { } }
            string info = ""; if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) info = " (Quota limit likely reached)"; else if (ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase)) info = " (Invalid token or permissions issue)"; else if (ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase)) info = " (Forbidden - possibly rate limit or permissions)"; throw new Exception($"FATAL: Failed to create codespace{info}. Original error: {ex.Message}");
        }
    }

    private static async Task<List<CodespaceInfo>> ListAllCodespaces(TokenEntry token)
    {
        string args = "codespace list --json name,displayName,state,createdAt";
        try {
            string jsonResult = await ShellHelper.RunGhCommand(token, args); if (string.IsNullOrWhiteSpace(jsonResult) || jsonResult == "[]") return new List<CodespaceInfo>();
            try { return JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>(); }
            catch (JsonException jEx) { AnsiConsole.MarkupLine($"[yellow]Warn: Failed to parse codespace list JSON: {jEx.Message}[/]"); return new List<CodespaceInfo>(); }
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error listing codespaces: {ex.Message.Split('\n').FirstOrDefault()}[/]"); return new List<CodespaceInfo>(); }
    }

    private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName)
    { try { string json = await ShellHelper.RunGhCommand(token, $"codespace view --json state -c \"{codespaceName}\"", SSH_PROBE_TIMEOUT_MS); using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("state", out var p) ? p.GetString() : null; } catch { return null; } }

    private static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token)
    { try { using var client = TokenManager.CreateHttpClient(token); client.Timeout = TimeSpan.FromSeconds(30); var response = await client.GetAsync($"https://api.github.com/repos/{token.Owner}/{token.Repo}/commits?per_page=1"); if (!response.IsSuccessStatusCode) { AnsiConsole.MarkupLine($"[yellow]Warn: Failed to fetch last commit ({response.StatusCode}).[/]"); return null; } var json = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(json); if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) { AnsiConsole.MarkupLine($"[yellow]Warn: No commits found in repo?[/]"); return null; } var dateString = doc.RootElement[0].GetProperty("commit").GetProperty("committer").GetProperty("date").GetString(); return DateTime.TryParse(dateString, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null; } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetching last commit: {ex.Message.Split('\n').FirstOrDefault()}[/]"); return null; } }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName)
    { AnsiConsole.MarkupLine($"[yellow]Attempting to delete codespace '{codespaceName}'...[/]"); try { await ShellHelper.RunGhCommand(token, $"codespace delete -c \"{codespaceName}\" --force", SSH_COMMAND_TIMEOUT_MS); AnsiConsole.MarkupLine($"[green]✓ Delete command sent successfully for '{codespaceName}'.[/]"); } catch (Exception ex) { if (ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("find", StringComparison.OrdinalIgnoreCase)) { AnsiConsole.MarkupLine($"[dim]Codespace '{codespaceName}' already gone or never existed.[/]"); } else { AnsiConsole.MarkupLine($"[yellow]Warn: Delete command failed for '{codespaceName}': {ex.Message.Split('\n').FirstOrDefault()}[/]"); } } await Task.Delay(3000); }

    public static async Task StopCodespace(TokenEntry token, string codespaceName)
    { AnsiConsole.Markup($"[dim]Attempting to stop codespace '{codespaceName}'... [/]"); try { await ShellHelper.RunGhCommand(token, $"codespace stop --codespace \"{codespaceName}\"", STOP_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); } catch (Exception ex) { if (ex.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase)) { AnsiConsole.MarkupLine("[dim]Already stopped.[/]"); } else { AnsiConsole.MarkupLine($"[yellow]Warn: Stop command failed: {ex.Message.Split('\n').FirstOrDefault()}[/]"); } } await Task.Delay(2000); }

    private static async Task StartCodespace(TokenEntry token, string codespaceName)
    { AnsiConsole.Markup($"[dim]Attempting to start codespace '{codespaceName}'... [/]"); try { await ShellHelper.RunGhCommand(token, $"codespace start --codespace \"{codespaceName}\"", START_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); } catch (Exception ex) { if (!ex.Message.Contains("available", StringComparison.OrdinalIgnoreCase)) { AnsiConsole.MarkupLine($"[yellow]Warn: Start command failed: {ex.Message.Split('\n').FirstOrDefault()}[/]"); } else { AnsiConsole.MarkupLine($"[dim]Already available.[/]"); } } }

    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken, bool useFastPolling = false)
    { Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Waiting for state '{targetState}' (max {timeout.TotalMinutes:F0} min)...[/]"); int pollIntervalMs = useFastPolling ? STATE_POLL_INTERVAL_FAST_MS : STATE_POLL_INTERVAL_SLOW_SEC * 1000; while (sw.Elapsed < timeout) { cancellationToken.ThrowIfCancellationRequested(); var state = await GetCodespaceState(token, codespaceName); cancellationToken.ThrowIfCancellationRequested(); if (state == targetState) { AnsiConsole.MarkupLine($"[green]✓ Reached '{targetState}'[/]"); return true; } if (state == null || state == "Failed" || state == "Error" || state.Contains("Shutting") || state == "Deleted") { AnsiConsole.MarkupLine($"[red]✗ Reached failure state ('{state ?? "Unknown/Deleted"}')[/]"); return false; } AnsiConsole.Markup($"[dim].[/]"); try { await Task.Delay(pollIntervalMs, cancellationToken); } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for state '{targetState}'[/]"); throw; } } AnsiConsole.MarkupLine($"[yellow]Timeout waiting for state '{targetState}'[/]"); return false; }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken, bool useFastPolling = false)
    { Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Waiting for SSH connection (max {SSH_READY_MAX_DURATION_MIN} min)...[/]"); int pollIntervalMs = useFastPolling ? SSH_READY_POLL_INTERVAL_FAST_MS : SSH_READY_POLL_INTERVAL_SLOW_SEC * 1000; while (sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) { cancellationToken.ThrowIfCancellationRequested(); try { string res = await ShellHelper.RunGhCommand(token, $"codespace ssh -c \"{codespaceName}\" -- echo ready", SSH_PROBE_TIMEOUT_MS); cancellationToken.ThrowIfCancellationRequested(); if (res != null && res.Contains("ready")) { AnsiConsole.MarkupLine("[green]✓ SSH Ready[/]"); return true; } AnsiConsole.Markup($"[dim]?[/] "); } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for SSH[/]"); throw; } catch { AnsiConsole.Markup($"[dim]x[/]"); } try { await Task.Delay(pollIntervalMs, cancellationToken); } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for SSH[/]"); throw; } } AnsiConsole.MarkupLine($"[yellow]Timeout waiting for SSH connection[/]"); return false; }

    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    { Stopwatch sw = Stopwatch.StartNew(); AnsiConsole.Markup($"[cyan]Checking startup script health (max {HEALTH_CHECK_MAX_DURATION_MIN} min)...[/]"); int successfulSshChecks = 0; const int SSH_STABILITY_THRESHOLD = 2; while (sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) { cancellationToken.ThrowIfCancellationRequested(); string result = ""; try { string args = $"codespace ssh -c \"{codespaceName}\" -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\""; result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); cancellationToken.ThrowIfCancellationRequested(); if (result.Contains("FAILED")) { AnsiConsole.MarkupLine($"[red]✗ Startup script failed (found fail flag)[/]"); return false; } if (result.Contains("HEALTHY")) { AnsiConsole.MarkupLine("[green]✓ Healthy (found done flag)[/]"); return true; } if (result.Contains("NOT_READY")) { AnsiConsole.Markup($"[dim]_[/]"); successfulSshChecks++; if (successfulSshChecks >= SSH_STABILITY_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) { AnsiConsole.MarkupLine($"[cyan]✓ SSH stable & script not failed. Assuming OK.[/]"); return true; } } else { AnsiConsole.Markup($"[yellow]?[/]"); successfulSshChecks = 0; } } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while checking health[/]"); throw; } catch { AnsiConsole.Markup($"[red]x[/]"); successfulSshChecks = 0; } try { await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000, cancellationToken); } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while checking health[/]"); throw; } } AnsiConsole.MarkupLine($"[yellow]Timeout waiting for health flag[/]"); return false; }

    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
    { AnsiConsole.MarkupLine("[cyan]Triggering remote auto-start.sh script...[/]"); string repo = token.Repo.ToLower(); string scriptPath = $"/workspaces/{repo}/auto-start.sh"; AnsiConsole.Markup("[dim]Executing command in background (nohup)... [/]"); string command = $"nohup bash \"{scriptPath}\" > /tmp/startup.log 2>&1 &"; string args = $"codespace ssh -c \"{codespaceName}\" -- {command}"; try { await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); AnsiConsole.MarkupLine("[green]OK[/]"); } catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn: Failed to trigger auto-start: {ex.Message.Split('\n').FirstOrDefault()}[/]"); } }

    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
    { AnsiConsole.MarkupLine($"[dim]Fetching running bot sessions (tmux)...[/]"); string args = $"codespace ssh -c \"{codespaceName}\" -- tmux list-windows -t automation_hub_bots -F \"#{{window_name}}\""; try { string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "dashboard" && s != "bash").OrderBy(s => s).ToList(); } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Failed to fetch tmux sessions: {ex.Message.Split('\n').FirstOrDefault()}[/]"); AnsiConsole.MarkupLine($"[dim](This is normal if bots haven't started yet or if the codespace is new)[/]"); return new List<string>(); } }

    private class CodespaceInfo { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; [JsonPropertyName("state")] public string State { get; set; } = ""; [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = ""; }
} // Akhir class CodespaceManager
