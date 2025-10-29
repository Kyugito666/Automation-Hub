using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Diagnostics;
using System.IO; // <-- Pastikan using IO ada
using System.Collections.Generic; // <-- Tambah using Generic Collections
using System.Linq; // <-- Tambah using Linq

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
    private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config"); // <-- Path ke folder config
    private static readonly string UploadFilesListPath = Path.Combine(ConfigRoot, "upload_files.txt"); // <-- Path ke file list upload

    private static string GetProjectRoot()
    {
        // Cari root project berdasarkan keberadaan folder 'config' dan file '.gitignore'
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null)
        {
            var configDir = Path.Combine(currentDir.FullName, "config");
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            if (Directory.Exists(configDir) && File.Exists(gitignore))
            {
                return currentDir.FullName;
            }
            currentDir = currentDir.Parent;
        }
        // Fallback jika tidak ketemu (seharusnya tidak terjadi jika struktur benar)
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory!, "..", "..", "..", ".."));
    }


    // --- FUNGSI BARU UNTUK BACA LIST FILE UPLOAD ---
    private static List<string> LoadUploadFileList()
    {
        if (!File.Exists(UploadFilesListPath))
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: File '{UploadFilesListPath}' tidak ditemukan. Menggunakan daftar default.[/]");
            // Fallback ke daftar default jika file tidak ada
            return new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt" };
        }

        try
        {
            return File.ReadAllLines(UploadFilesListPath)
                       .Select(line => line.Trim())
                       .Where(line => !string.IsNullOrEmpty(line) && !line.StartsWith("#"))
                       .ToList();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error membaca '{UploadFilesListPath}': {ex.Message.EscapeMarkup()}. Menggunakan daftar default.[/]");
            return new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt" };
        }
    }
    // --- AKHIR FUNGSI BARU ---

    // --- FUNGSI UPLOAD CREDENTIAL (Sudah Dimodifikasi) ---
    private static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]═══ Uploading Credentials via gh cp ═══[/]");
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No bot config found or empty. Skipping credential upload.[/]");
            return;
        }

        // === PERBAIKAN: Baca daftar file dari file konfigurasi ===
        var credentialFilesToUpload = LoadUploadFileList();
        if (!credentialFilesToUpload.Any())
        {
             AnsiConsole.MarkupLine("[yellow]Daftar file upload kosong ('config/upload_files.txt'). Skipping credential upload.[/]");
             return;
        }
        AnsiConsole.MarkupLine($"[dim]   Checking for {credentialFilesToUpload.Count} potential files per bot (from config/upload_files.txt)[/]");
        // === AKHIR PERBAIKAN ===

        int botsProcessed = 0;
        int filesUploaded = 0;
        int filesSkipped = 0;

        string remoteWorkspacePath = $"/workspaces/{token.Repo}";

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Processing bots...[/]", new ProgressTaskSettings { MaxValue = config.BotsAndTools.Count });

                foreach (var bot in config.BotsAndTools)
                {
                    task.Description = $"[green]Checking:[/] {bot.Name}";
                    if (!bot.Enabled)
                    {
                        task.Increment(1);
                        continue;
                    }

                    string localBotDir = BotConfig.GetLocalBotPath(bot.Path);
                    if (!Directory.Exists(localBotDir))
                    {
                        filesSkipped++;
                        task.Increment(1);
                        continue;
                    }

                    string remoteBotDir = Path.Combine(remoteWorkspacePath, bot.Path).Replace('\\', '/');

                    bool botProcessed = false;
                    // === PERBAIKAN: Gunakan daftar file dari file config ===
                    foreach (var credFileName in credentialFilesToUpload)
                    // === AKHIR PERBAIKAN ===
                    {
                        string localFilePath = Path.Combine(localBotDir, credFileName);
                        if (File.Exists(localFilePath))
                        {
                            botProcessed = true;
                            string remoteFilePath = $"{remoteBotDir}/{credFileName}";
                            task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";

                            string localAbsPath = Path.GetFullPath(localFilePath);
                            string remoteTarget = $"{codespaceName}:{remoteFilePath}";

                             try
                             {
                                 string mkdirArgs = $"codespace ssh -c {codespaceName} -- mkdir -p \"{remoteBotDir}\"";
                                 await ShellHelper.RunGhCommand(token, mkdirArgs, SSH_PROBE_TIMEOUT_MS);
                             }
                             catch (Exception mkdirEx)
                             {
                                 AnsiConsole.MarkupLine($"[red]   Failed to create remote directory for {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()}[/]");
                                 filesSkipped++;
                                 continue;
                             }

                            try
                            {
                                string cpArgs = $"codespace cp \"{localAbsPath}\" \"{remoteTarget}\"";
                                await ShellHelper.RunGhCommand(token, cpArgs, SSH_COMMAND_TIMEOUT_MS);
                                filesUploaded++;
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]   ✗ Failed to upload {credFileName} for {bot.Name}: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                                filesSkipped++;
                            }
                             await Task.Delay(200);
                        }
                    }
                     if (botProcessed)
                     {
                         botsProcessed++;
                     }

                    task.Increment(1);
                }
            });

        AnsiConsole.MarkupLine($"[green]✓ Credential upload complete.[/]");
        AnsiConsole.MarkupLine($"[dim]   Bots checked: {config.BotsAndTools.Count} | Bots with files: {botsProcessed} | Files uploaded: {filesUploaded} | Files skipped/failed: {filesSkipped}[/]");
    }
    // --- AKHIR FUNGSI UPLOAD ---


    // ... (Fungsi EnsureHealthyCodespace - TIDAK BERUBAH dari versi sebelumnya) ...
    public static async Task<string> EnsureHealthyCodespace(TokenEntry token, string repoFullName)
    {
        AnsiConsole.MarkupLine("\n[cyan]═══ Ensuring Codespace Runner ═══[/]");
        CodespaceInfo? codespace = null;
        Stopwatch stopwatch = Stopwatch.StartNew();

        AnsiConsole.Markup("[dim]Checking repo last commit... [/]");
        var repoLastCommit = await GetRepoLastCommitDate(token);
        if (repoLastCommit.HasValue)
            AnsiConsole.MarkupLine($"[green]OK[/] [dim]({repoLastCommit.Value:yyyy-MM-dd HH:mm} UTC)[/]");
        else
            AnsiConsole.MarkupLine("[yellow]Unable to fetch (continuing)[/]");

        while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN)
        {
            AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Finding codespace... [/]");
            var (found, all) = await FindExistingCodespace(token);
            codespace = found;

            if (codespace == null) {
                AnsiConsole.MarkupLine("[yellow]Not found[/]");
                await CleanupStuckCodespaces(token, all, null);
                return await CreateNewCodespace(token, repoFullName);
            }

            AnsiConsole.MarkupLine($"[green]Found[/] [dim]{codespace.Name} ({codespace.State})[/]");

            if (repoLastCommit.HasValue && !string.IsNullOrEmpty(codespace.CreatedAt)) {
                 if (DateTime.TryParse(codespace.CreatedAt, out var csCreated)) {
                    csCreated = csCreated.ToUniversalTime();
                    if (repoLastCommit.Value > csCreated) {
                        var diff = repoLastCommit.Value - csCreated;
                        AnsiConsole.MarkupLine($"[yellow]⚠ Codespace outdated! Repo has new commit ({diff.TotalMinutes:F0}min newer)[/]");
                        AnsiConsole.MarkupLine("[yellow]Deleting outdated codespace...[/]");
                        await DeleteCodespace(token, codespace.Name);
                        codespace = null;
                        continue; // Kembali ke awal loop untuk create baru
                    }
                 }
            }

            switch (codespace.State)
            {
                case "Available":
                    AnsiConsole.MarkupLine("[cyan]State: Available. Checking SSH...[/]");
                    if (!await WaitForSshReadyWithRetry(token, codespace.Name)) {
                        AnsiConsole.MarkupLine($"[red]SSH failed despite Available state. Deleting...[/]");
                        await DeleteCodespace(token, codespace.Name);
                        codespace = null;
                        break;
                    }
                    await UploadCredentialsToCodespace(token, codespace.Name); // Upload dulu
                    AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                    try {
                        await TriggerStartupScript(token, codespace.Name);
                    } catch (Exception scriptEx) {
                         AnsiConsole.MarkupLine($"[yellow]Warn: Trigger failed (normal if clone slow): {scriptEx.Message.Split('\n').FirstOrDefault()}[/]");
                    }
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                        AnsiConsole.MarkupLine("[green]✓ Health check PASSED. Reusing.[/]");
                        stopwatch.Stop();
                        return codespace.Name;
                    }
                    AnsiConsole.MarkupLine($"[yellow]Health timeout but SSH OK. Assuming healthy.[/]");
                    stopwatch.Stop();
                    return codespace.Name;

                case "Stopped":
                case "Shutdown":
                    AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Starting...[/]");
                    await StartCodespace(token, codespace.Name);
                    if (!await WaitForState(token, codespace.Name, "Available", TimeSpan.FromMinutes(3)))
                        AnsiConsole.MarkupLine("[yellow]State timeout, checking SSH anyway...[/]");

                    if (!await WaitForSshReadyWithRetry(token, codespace.Name)) {
                        AnsiConsole.MarkupLine("[red]SSH failed after start. Deleting...[/]");
                        await DeleteCodespace(token, codespace.Name);
                        codespace = null;
                        break;
                    }
                    await UploadCredentialsToCodespace(token, codespace.Name); // Upload dulu
                    AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                     try {
                        await TriggerStartupScript(token, codespace.Name);
                    } catch (Exception scriptEx) {
                         AnsiConsole.MarkupLine($"[yellow]Warn: Trigger failed (normal if clone slow): {scriptEx.Message.Split('\n').FirstOrDefault()}[/]");
                    }
                    if (await CheckHealthWithRetry(token, codespace.Name)) {
                        AnsiConsole.MarkupLine("[green]✓ Health check PASSED. Reusing.[/]");
                        stopwatch.Stop();
                        return codespace.Name;
                    }
                    AnsiConsole.MarkupLine($"[yellow]Health timeout but SSH OK. Assuming healthy.[/]");
                    stopwatch.Stop();
                    return codespace.Name;

                case "Starting":
                case "Queued":
                case "Rebuilding":
                    AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Waiting {STATE_POLL_INTERVAL_SEC}s...[/]");
                    await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000);
                    continue;

                default:
                    AnsiConsole.MarkupLine($"[red]Bad state: {codespace.State}. Deleting...[/]");
                    await DeleteCodespace(token, codespace.Name);
                    codespace = null;
                    break;
            }

            if (codespace == null && stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                await Task.Delay(5000);
            }
        }

        stopwatch.Stop();
        if (codespace != null) {
            AnsiConsole.MarkupLine($"\n[yellow]CS still {codespace.State} after {STATE_POLL_MAX_DURATION_MIN}min[/]");
            AnsiConsole.MarkupLine($"[cyan]Attempting to use anyway...[/]");
            return codespace.Name;
        }

        AnsiConsole.MarkupLine($"\n[red]FATAL: No healthy codespace after {STATE_POLL_MAX_DURATION_MIN}min[/]");
        AnsiConsole.MarkupLine("[yellow]Attempting final create...[/]");
        var (_, allFinal) = await FindExistingCodespace(token);
        await CleanupStuckCodespaces(token, allFinal, null);
        try {
            return await CreateNewCodespace(token, repoFullName);
        }
        catch (Exception createEx) {
            AnsiConsole.WriteException(createEx);
            throw new Exception($"FATAL: Final create failed. {createEx.Message}");
        }
    }

    // ... (Fungsi CreateNewCodespace - TIDAK BERUBAH dari versi sebelumnya, upload dipanggil setelah SSH ready) ...
    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName)
    {
        AnsiConsole.MarkupLine($"\n[cyan]═══ Creating New Codespace ═══[/]");
        AnsiConsole.MarkupLine($"[dim]Machine: {MACHINE_TYPE}, Display: {CODESPACE_DISPLAY_NAME}[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        Stopwatch createStopwatch = Stopwatch.StartNew();
        string newName = "";
        try {
            newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS);
            createStopwatch.Stop();
            if (string.IsNullOrWhiteSpace(newName))
                throw new Exception("gh create returned empty name");

            AnsiConsole.MarkupLine($"[green]✓ Created: {newName}[/] [dim]({createStopwatch.Elapsed:mm\\:ss})[/]");
            AnsiConsole.MarkupLine("\n[cyan]═══ First Boot Optimization ═══[/]");
            AnsiConsole.MarkupLine("[yellow]Waiting for codespace initialization...[/]");
            await Task.Delay(45000); // Tunggu sebentar
            var currentState = await GetCodespaceState(token, newName);
            AnsiConsole.MarkupLine($"[dim]Current state: {currentState}[/]");

            if (currentState == "Available") {
                 AnsiConsole.MarkupLine("[yellow]Performing restart for clean boot...[/]");
                 await StopCodespace(token, newName);
                 await Task.Delay(8000);
                 await StartCodespace(token, newName);
                 AnsiConsole.MarkupLine("[cyan]Waiting for Available state...[/]");
                 if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(5))) {
                     AnsiConsole.MarkupLine("[yellow]State timeout, checking SSH anyway...[/]");
                 }
            } else {
                 AnsiConsole.MarkupLine($"[dim]Waiting for Available (current: {currentState})...[/]");
                 if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(6))) {
                     AnsiConsole.MarkupLine("[yellow]State timeout, checking SSH anyway...[/]");
                 }
            }

            AnsiConsole.MarkupLine("[cyan]Waiting for SSH ready...[/]");
             if (!await WaitForSshReadyWithRetry(token, newName)) {
                throw new Exception("SSH failed after initialization");
            }
            // SSH Ready, upload credentials
            await UploadCredentialsToCodespace(token, newName);

            AnsiConsole.MarkupLine("[dim]Finalizing workspace setup...[/]");
            await Task.Delay(5000); // Bisa dikurangi karena upload sudah selesai

            AnsiConsole.MarkupLine("[green]✓ Codespace ready for use[/]");
            AnsiConsole.MarkupLine("\n[cyan]═══ Triggering Auto-Start ═══[/]");
            await TriggerStartupScript(token, newName);

            AnsiConsole.MarkupLine("[green]✓ Codespace created & initialized successfully[/]");
            AnsiConsole.MarkupLine("[dim]Bots will start automatically. Use Menu 4 to monitor.[/]");
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
            throw new Exception($"FATAL: Create failed{info}. {ex.Message}");
        }
    }


    // ... (Fungsi FindExistingCodespace, GetCodespaceState, GetRepoLastCommitDate, DeleteCodespace, StopCodespace, StartCodespace, WaitForState, WaitForSshReadyWithRetry, CheckHealthWithRetry, CleanupStuckCodespaces, TriggerStartupScript, GetTmuxSessions, class CodespaceInfo tetap sama) ...
    // ... TIDAK ADA PERUBAHAN DI FUNGSI-FUNGSI INI DARI VERSI REALTIME PING SEBELUMNYA ...

     private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token)
    {
        string args = "codespace list --json name,displayName,state,createdAt";
        string jsonResult = "";
        List<CodespaceInfo> allCodespaces = new List<CodespaceInfo>();
        try {
            jsonResult = await ShellHelper.RunGhCommand(token, args);
            try {
                allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>();
            } catch (JsonException ex) {
                AnsiConsole.MarkupLine($"[red]JSON parse error: {ex.Message}[/]");
            }
        } catch (Exception ex) {
             AnsiConsole.MarkupLine($"[red]List codespace error: {ex.Message.Split('\n').FirstOrDefault()}[/]");
        }
        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");
        return (existing, allCodespaces);
    }

    private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName)
    {
        string args = $"codespace view --json state -c {codespaceName}";
        try {
            string json = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("state").GetString();
        } catch (Exception ex) {
            if (ex.Message.Contains("404")) return null; // Codespace tidak ditemukan
            // AnsiConsole.MarkupLine($"[dim]   (GetState error: {ex.Message.Split('\n').FirstOrDefault()})[/]"); // Optional debug
            return null; // Anggap state tidak diketahui jika ada error lain
        }
    }

     private static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token)
    {
        // Tetap sama
        try {
            using var client = TokenManager.CreateHttpClient(token);
            var url = $"https://api.github.com/repos/{token.Owner}/{token.Repo}/commits?per_page=1";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetArrayLength() == 0) return null;
            var commitDate = doc.RootElement[0].GetProperty("commit").GetProperty("committer").GetProperty("date").GetString();
            if (DateTime.TryParse(commitDate, out var result)) {
                return result.ToUniversalTime();
            }
            return null;
        } catch { return null; }
    }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName)
    {
        // Tetap sama
        AnsiConsole.MarkupLine($"[yellow]Deleting {codespaceName}...[/]");
        try {
            string args=$"codespace delete -c {codespaceName} --force";
            await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS);
            AnsiConsole.MarkupLine("[green]✓ Deleted[/]");
        } catch (Exception ex) {
            if (ex.Message.Contains("404") || ex.Message.Contains("Could not find"))
                AnsiConsole.MarkupLine($"[dim]Already gone[/]");
            else
                AnsiConsole.MarkupLine($"[yellow]Delete failed: {ex.Message.Split('\n').FirstOrDefault()}[/]");
        }
        await Task.Delay(3000); // Beri waktu sedikit setelah delete
    }

    private static async Task StopCodespace(TokenEntry token, string codespaceName)
    {
       // Tetap sama
        AnsiConsole.Markup($"[dim]Stopping {codespaceName}... [/]");
        try {
            string args = $"codespace stop --codespace {codespaceName}";
            await ShellHelper.RunGhCommand(token, args, STOP_TIMEOUT_MS);
            AnsiConsole.MarkupLine("[green]OK[/]");
        } catch (Exception ex) {
            if (ex.Message.Contains("already stopped") || ex.Message.Contains("not running"))
                 AnsiConsole.MarkupLine("[dim]Already stopped[/]");
            else
                AnsiConsole.MarkupLine($"[yellow]Stop error (continuing): {ex.Message.Split('\n').FirstOrDefault()}[/]");
        }
        await Task.Delay(2000);
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName)
    {
        // Tetap sama
        AnsiConsole.Markup($"[dim]Starting {codespaceName}... [/]");
        try {
            await ShellHelper.RunGhCommand(token, $"codespace start --codespace {codespaceName}", START_TIMEOUT_MS);
            AnsiConsole.MarkupLine("[green]OK[/]");
        } catch (Exception ex) {
            if(!ex.Message.Contains("is already available"))
                AnsiConsole.MarkupLine($"[yellow]Start warning: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            else
                AnsiConsole.MarkupLine($"[dim]Already available[/]");
        }
    }

     private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout)
    {
        // Tetap sama (dengan ping 2 detik)
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Waiting for state '{targetState}' (max {timeout.TotalMinutes:F1}min)...[/]");
        while(sw.Elapsed < timeout) {
            var state = await GetCodespaceState(token, codespaceName);
            if (state == targetState) {
                AnsiConsole.MarkupLine($"[green]✓ State: {targetState}[/]");
                sw.Stop();
                return true;
            }
            if (state == null) { AnsiConsole.MarkupLine($"[red]Codespace lost[/]"); sw.Stop(); return false; }
            if (state == "Failed" || state == "Error" || state.Contains("ShuttingDown") || state=="Deleted") {
                AnsiConsole.MarkupLine($"[red]Error state: {state}[/]");
                sw.Stop();
                return false;
            }
            await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000); // Ping 2 detik
        }
        sw.Stop();
        AnsiConsole.MarkupLine($"[yellow]State timeout[/]");
        return false;
    }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName)
    {
        // Tetap sama (dengan ping 2 detik)
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Waiting SSH ready (max {SSH_READY_MAX_DURATION_MIN}min)...[/]");
        while(sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
            try {
                string args = $"codespace ssh -c {codespaceName} -- echo ready";
                string res = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
                if (res.Contains("ready")) {
                    AnsiConsole.MarkupLine("[green]✓ SSH Ready[/]");
                    sw.Stop();
                    return true;
                }
            }
            catch (TaskCanceledException) { /* Timeout probe is OK */ }
            catch (Exception) { /* Other errors, retry */ }
            await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000); // Ping 2 detik
        }
        sw.Stop();
        AnsiConsole.MarkupLine($"[yellow]SSH timeout[/]");
        return false;
    }

    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName)
    {
        // Tetap sama
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.MarkupLine($"[cyan]Checking health (max {HEALTH_CHECK_MAX_DURATION_MIN}min)...[/]");
        int consecutiveSshSuccess = 0;
        const int SSH_SUCCESS_THRESHOLD = 2; // Butuh 2x SSH sukses sebelum anggap sehat jika file flag belum ada
        while(sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
            string result = "";
            try {
                string args = $"codespace ssh -c {codespaceName} -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
                result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);

                if (result.Contains("FAILED")) { AnsiConsole.MarkupLine($"[red]✗ Startup script failed[/]"); sw.Stop(); return false; }
                if (result.Contains("HEALTHY")) { AnsiConsole.MarkupLine("[green]✓ Healthy[/]"); sw.Stop(); return true; }
                if (result.Contains("NOT_READY")) { consecutiveSshSuccess++; }

                // Jika SSH udah stabil (2x sukses berturut2) dan udah jalan > 1 menit, anggap aja lagi startup
                if (consecutiveSshSuccess >= SSH_SUCCESS_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) {
                    AnsiConsole.MarkupLine($"[cyan]SSH stable. Assuming startup in progress...[/]");
                    sw.Stop();
                    return true;
                }
            }
            catch { consecutiveSshSuccess = 0; /* Reset counter on SSH error */ }
            await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000);
        }
        sw.Stop();
        AnsiConsole.MarkupLine($"[yellow]Health check timeout (may still be starting)[/]");
        return false; // Anggap tidak sehat jika timeout
    }

     private static async Task CleanupStuckCodespaces(TokenEntry token, List<CodespaceInfo> allCodespaces, string? currentCodespaceName)
    {
        // Tetap sama
        AnsiConsole.MarkupLine("[dim]Cleaning stuck codespaces...[/]");
        int cleaned=0;
        foreach (var cs in allCodespaces) {
            if (cs.Name == currentCodespaceName || cs.State == "Deleted") continue;
            // Hanya hapus yang display name nya sama persis
            if (cs.DisplayName == CODESPACE_DISPLAY_NAME) {
                 AnsiConsole.MarkupLine($"[yellow]Found stuck: {cs.Name} ({cs.State}). Deleting...[/]");
                 await DeleteCodespace(token, cs.Name);
                 cleaned++;
            }
        }
        if (cleaned == 0) AnsiConsole.MarkupLine("[dim]No stuck codespaces found[/]");
    }

    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
    {
       // Tetap sama (versi langsung eksekusi)
        AnsiConsole.MarkupLine("[cyan]Triggering auto-start.sh...[/]");
        string repoNameLower = token.Repo.ToLower();
        string workspacePath = $"/workspaces/{repoNameLower}";
        string remoteScript = $"{workspacePath}/auto-start.sh";

        AnsiConsole.Markup("[dim]Executing (detached)... [/]");
        string cmd = $"nohup bash {remoteScript} > /tmp/startup.log 2>&1 &";
        string args = $"codespace ssh -c {codespaceName} -- {cmd}";
        try {
            await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
            AnsiConsole.MarkupLine("[green]OK[/]");
        } catch (Exception ex) {
             AnsiConsole.MarkupLine($"[yellow]Warn (expected if clone slow): {ex.Message.Split('\n').FirstOrDefault()}[/]");
             AnsiConsole.MarkupLine("[dim]   (This is OK, postAttachCommand might take over)[/]");
        }
    }

     public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
    {
        // Tetap sama
        AnsiConsole.MarkupLine($"[dim]Fetching bot sessions from {codespaceName}...[/]");
        string args = $"codespace ssh -c {codespaceName} -- tmux list-windows -t automation_hub_bots -F \"#{{window_name}}\"";
        try {
            string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS);
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(s => s != "dashboard" && s != "bash") // Filter out default windows
                         .OrderBy(s => s).ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Failed to fetch tmux sessions: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            if (ex.Message.Contains("No sessions") || ex.Message.Contains("command not found")) // Handle tmux not ready
                AnsiConsole.MarkupLine("[yellow]Tmux session/command not found. Bots may not be started yet.[/]");
            return new List<string>();
        }
    }

    private class CodespaceInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";
        [JsonPropertyName("state")]
        public string State { get; set; } = "";
        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = "";
    }

} // Akhir class CodespaceManager
