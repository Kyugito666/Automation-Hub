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
    // --- Konstanta (tidak berubah) ---
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux32gb";
    private const int SSH_COMMAND_TIMEOUT_MS = 120000;
    private const int CREATE_TIMEOUT_MS = 600000; // 10 menit
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

    // --- Fungsi GetProjectRoot (tidak berubah) ---
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

    // --- LoadUploadFileList (tidak berubah, tapi pastikan ada return di catch) ---
    private static List<string> LoadUploadFileList()
    {
        if (!File.Exists(UploadFilesListPath)) {
            AnsiConsole.MarkupLine($"[yellow]Warn: '{UploadFilesListPath}' not found. Using defaults.[/]");
            return new List<string> { /* ... file default ... */ };
        }
        try {
            return File.ReadAllLines(UploadFilesListPath)
                       .Select(l => l.Trim())
                       .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                       .ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error reading '{UploadFilesListPath}': {ex.Message.EscapeMarkup()}. Using defaults.[/]");
            // === FIX CS0161: Tambahkan return di sini ===
            return new List<string> { /* ... file default ... */ };
        }
    }

    // --- GetFilesToUploadForBot (tidak berubah, sudah ada return) ---
    private static List<string> GetFilesToUploadForBot(string localBotDir, List<string> allPossibleFiles)
    {
        var existingFiles = new List<string>();
        foreach (var fileName in allPossibleFiles) {
            var filePath = Path.Combine(localBotDir, fileName);
            if (File.Exists(filePath)) { existingFiles.Add(fileName); }
        }
        return existingFiles; // Return sudah ada
    }

    // --- UploadCredentialsToCodespace (tidak berubah dari versi terakhir) ---
    private static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        // ... implementasi SAMA PERSIS seperti jawaban sebelumnya ...
        // (yang sudah ada verifikasi 'test -d' setelah mkdir)
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
                            mkdirSuccess = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception mkdirEx) { AnsiConsole.MarkupLine($"[red]✗ Failed mkdir command for {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }

                        // 2. Verifikasi Direktori dengan 'test -d'
                        bool dirExists = false;
                        if (mkdirSuccess) {
                            task.Description = $"[grey]Verifying dir:[/] {bot.Name}";
                            try {
                                await Task.Delay(500, cancellationToken); // Delay singkat
                                string testCmd = $"test -d {escapedRemoteBotDir}";
                                string sshTestArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{testCmd}\"";
                                await ShellHelper.RunGhCommand(token, sshTestArgs, 30000); // Timeout 30s
                                dirExists = true;
                                AnsiConsole.MarkupLine($"[green]✓ Directory verified for {bot.Name}[/]");
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception testEx) { AnsiConsole.MarkupLine($"[red]✗ Directory verification FAILED for {bot.Name}.[/]"); AnsiConsole.MarkupLine($"[grey]   Verification error: {testEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }
                        } else { AnsiConsole.MarkupLine($"[red]✗ Skipping verification for {bot.Name} because mkdir failed.[/]"); }

                        // 3. Upload File HANYA JIKA Direktori Ada
                        if (dirExists) {
                            foreach (var credFileName in filesToUpload) { /* ... (logika cp sama) ... */ }
                            botsProcessed++;
                        }
                        else { AnsiConsole.MarkupLine($"[red]✗ Skipping uploads for {bot.Name} due to directory failure.[/]"); filesSkipped += filesToUpload.Count; botsSkipped++; }
                        task.Increment(1);
                    } // Akhir foreach bot

                    // --- Upload Config ProxySync (logika sama dengan verifikasi) ---
                    task.Description = "[cyan]Uploading ProxySync Configs...";
                    var proxySyncConfigFiles = new List<string> { "apikeys.txt", "apilist.txt" };
                    string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/proxysync/config";
                    string escapedRemoteProxySyncDir = $"'{remoteProxySyncConfigDir.Replace("'", "'\\''")}'";
                    bool proxySyncConfigUploadSuccess = true;
                    bool proxySyncDirExists = false;
                    try {
                        string mkdirCmd = $"mkdir -p {escapedRemoteProxySyncDir}";
                        string sshMkdirArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                        await ShellHelper.RunGhCommand(token, sshMkdirArgs, 60000);
                        await Task.Delay(500, cancellationToken);
                        string testCmd = $"test -d {escapedRemoteProxySyncDir}";
                        string sshTestArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{testCmd}\"";
                        await ShellHelper.RunGhCommand(token, sshTestArgs, 30000);
                        proxySyncDirExists = true; AnsiConsole.MarkupLine($"[green]✓ ProxySync config directory verified.[/]");
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception dirEx) { AnsiConsole.MarkupLine($"[red]✗ Error creating/verifying ProxySync config dir: {dirEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); filesSkipped += proxySyncConfigFiles.Count; proxySyncConfigUploadSuccess = false; }

                    if (proxySyncDirExists) { foreach (var configFileName in proxySyncConfigFiles) { /* ... (logika cp sama) ... */ } }
                    else { AnsiConsole.MarkupLine($"[red]✗ Skipping ProxySync uploads.[/]"); }

                    if (proxySyncConfigUploadSuccess && proxySyncDirExists) AnsiConsole.MarkupLine("[green]✓ ProxySync configs uploaded.[/]");
                    else AnsiConsole.MarkupLine("[yellow]! Some ProxySync configs failed.[/]");
                    task.Increment(1);
                });
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Upload cancelled.[/]"); /* ... (log parsial sama) ... */ throw; }
        catch (Exception uploadEx) { AnsiConsole.MarkupLine("\n[red]UNEXPECTED UPLOAD ERROR[/]"); AnsiConsole.WriteException(uploadEx); throw; }
        AnsiConsole.MarkupLine($"\n[green]✓ Upload finished.[/]"); AnsiConsole.MarkupLine($"[dim]   Bots OK: {botsProcessed}, Skip: {botsSkipped} | Files OK: {filesUploaded}, Fail: {filesSkipped}[/]");
    }

    // --- EnsureHealthyCodespace & CreateNewCodespace (tidak berubah dari versi terakhir) ---
    public static async Task<string> EnsureHealthyCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken) { /* ... implementasi SAMA PERSIS seperti jawaban sebelumnya ... */ }
    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken) { /* ... implementasi SAMA PERSIS seperti jawaban sebelumnya (tanpa --web) ... */ }


    // === IMPLEMENTASI LENGKAP FUNGSI HELPER ===

    private static async Task<List<CodespaceInfo>> ListAllCodespaces(TokenEntry token)
    {
        string args = "codespace list --json name,displayName,state,createdAt";
        try {
            // ShellHelper menangani retry & error dasar
            string jsonResult = await ShellHelper.RunGhCommand(token, args);
            if (string.IsNullOrWhiteSpace(jsonResult) || jsonResult == "[]") {
                return new List<CodespaceInfo>(); // List kosong jika tidak ada codespace
            }
            // Parsing JSON
            try {
                // Deserialize atau kembalikan list kosong jika null
                return JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>();
            } catch (JsonException jEx) {
                 AnsiConsole.MarkupLine($"[yellow]Warn: Failed to parse codespace list JSON: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                 return new List<CodespaceInfo>(); // Return list kosong jika parse gagal
            }
        } catch (Exception ex) {
            // Tangkap error dari ShellHelper (misal token invalid, rate limit fatal)
            AnsiConsole.MarkupLine($"[red]Error listing codespaces: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            return new List<CodespaceInfo>(); // Return list kosong jika command gagal total
        }
        // === FIX CS0161: Compiler bisa anggap path ini mungkin tercapai jika ada GOTO atau struktur aneh ===
        // Meskipun secara logika tidak akan tercapai, tambahkan return default
        // return new List<CodespaceInfo>();
    }

    private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName)
    {
        try {
            string args = $"codespace view --json state -c \"{codespaceName}\"";
            string json = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); // Timeout pendek
            using var doc = JsonDocument.Parse(json);
            // Kembalikan state jika ada, null jika tidak
            return doc.RootElement.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
        } catch (JsonException jEx) {
            AnsiConsole.MarkupLine($"[yellow]Warn: Failed parse codespace state JSON: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            return null; // Gagal parse -> state tidak diketahui
        } catch (Exception ex) {
            // Tangkap error dari ShellHelper (timeout, network, 404) -> state tidak diketahui
            // Tidak perlu log error lagi karena ShellHelper sudah log
            // AnsiConsole.MarkupLine($"[yellow]Warn: Failed get codespace state for {codespaceName}: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            return null;
        }
    }

    private static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token)
    {
        // Fungsi ini pakai HttpClient langsung, BUKAN ShellHelper
        try {
            using var client = TokenManager.CreateHttpClient(token); // Buat HttpClient (sudah ada token & proxy)
            client.Timeout = TimeSpan.FromSeconds(30); // Timeout wajar
            var response = await client.GetAsync($"https://api.github.com/repos/{token.Owner}/{token.Repo}/commits?per_page=1");

            if (!response.IsSuccessStatusCode) {
                 AnsiConsole.MarkupLine($"[yellow]Warn: Failed to fetch last commit ({response.StatusCode}).[/]");
                 return null; // Gagal API
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) {
                 AnsiConsole.MarkupLine($"[yellow]Warn: No commits found in repo?[/]");
                 return null; // Repo kosong?
            }

            // Ambil tanggal dari JSON
            var dateString = doc.RootElement[0]
                .GetProperty("commit")
                .GetProperty("committer")
                .GetProperty("date")
                .GetString();

            // Parse tanggal
            if (DateTime.TryParse(dateString, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)) {
                return dt; // Sukses parse
            } else {
                 AnsiConsole.MarkupLine($"[yellow]Warn: Failed to parse commit date string: {dateString}[/]");
                 return null; // Gagal parse
            }
        } catch (JsonException jEx) {
             AnsiConsole.MarkupLine($"[red]Error parsing commit JSON: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
             return null;
        } catch (HttpRequestException httpEx) {
             AnsiConsole.MarkupLine($"[red]Error fetching last commit (Network): {httpEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
             return null;
        } catch (Exception ex) { // Tangkap error lain (misal timeout HttpClient)
             AnsiConsole.MarkupLine($"[red]Error fetching last commit: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
             return null;
        }
    }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[yellow]Attempting delete codespace '{codespaceName.EscapeMarkup()}'...[/]");
        try {
            string args = $"codespace delete -c \"{codespaceName}\" --force";
            await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); // Timeout 2 menit
            AnsiConsole.MarkupLine($"[green]✓ Delete command sent for '{codespaceName.EscapeMarkup()}'.[/]");
        } catch (Exception ex) {
            // Handle 404 (sudah dihapus/tidak ada)
            if (ex.Message.Contains("404") || ex.Message.Contains("find")) {
                AnsiConsole.MarkupLine($"[dim]Codespace '{codespaceName.EscapeMarkup()}' already gone.[/]");
            } else { // Error lain
                AnsiConsole.MarkupLine($"[yellow]Warn: Delete command failed for '{codespaceName.EscapeMarkup()}': {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
        }
        await Task.Delay(3000); // Jeda setelah delete
    }

    public static async Task StopCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.Markup($"[dim]Attempting stop codespace '{codespaceName.EscapeMarkup()}'... [/]");
        try {
            string args = $"codespace stop --codespace \"{codespaceName}\"";
            await ShellHelper.RunGhCommand(token, args, STOP_TIMEOUT_MS); // Timeout 2 menit
            AnsiConsole.MarkupLine("[green]OK[/]");
        } catch (Exception ex) {
            // Handle jika sudah stopped
            if (ex.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase)) {
                AnsiConsole.MarkupLine("[dim]Already stopped.[/]");
            } else { // Error lain
                 AnsiConsole.MarkupLine($"[yellow]Warn: Stop command failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
        }
        await Task.Delay(2000); // Jeda setelah stop
    }

    private static async Task StartCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.Markup($"[dim]Attempting start codespace '{codespaceName.EscapeMarkup()}'... [/]");
        try {
            string args = $"codespace start --codespace \"{codespaceName}\"";
            await ShellHelper.RunGhCommand(token, args, START_TIMEOUT_MS); // Timeout 5 menit
            AnsiConsole.MarkupLine("[green]OK[/]");
        } catch (Exception ex) {
            // Handle jika sudah available
            if(ex.Message.Contains("available", StringComparison.OrdinalIgnoreCase)) {
                AnsiConsole.MarkupLine($"[dim]Already available.[/]");
            } else { // Error lain
                AnsiConsole.MarkupLine($"[yellow]Warn: Start command failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
        }
        // Tidak perlu delay setelah start, biarkan WaitForState yang handle
    }

    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken, bool useFastPolling = false)
    {
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.Markup($"[cyan]Waiting for state '{targetState}' (max {timeout.TotalMinutes:F0} min)...[/]");
        int pollIntervalMs = useFastPolling ? STATE_POLL_INTERVAL_FAST_MS : STATE_POLL_INTERVAL_SLOW_SEC * 1000;

        while (sw.Elapsed < timeout) {
            cancellationToken.ThrowIfCancellationRequested(); // Cek cancel awal loop
            string? state = await GetCodespaceState(token, codespaceName);
            cancellationToken.ThrowIfCancellationRequested(); // Cek cancel setelah await

            if (state == targetState) { AnsiConsole.MarkupLine($"[green]✓ Reached '{targetState}'[/]"); return true; } // Sukses
            if (state == null || state == "Failed" || state == "Error" || state.Contains("Shutting") || state == "Deleted") { AnsiConsole.MarkupLine($"[red]✗ Reached failure state ('{state ?? "Unknown/Deleted"}')[/]"); return false; } // Gagal
            AnsiConsole.Markup($"[dim].[/]"); // Progress
            try { await Task.Delay(pollIntervalMs, cancellationToken); } // Delay & Cek Cancel
            catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for state '{targetState}'[/]"); throw; } // Lempar cancel
        }
        AnsiConsole.MarkupLine($"[yellow]Timeout waiting for state '{targetState}'[/]");
        return false; // Timeout
    }

    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken, bool useFastPolling = false)
    {
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.Markup($"[cyan]Waiting for SSH connection (max {SSH_READY_MAX_DURATION_MIN} min)...[/]");
        int pollIntervalMs = useFastPolling ? SSH_READY_POLL_INTERVAL_FAST_MS : SSH_READY_POLL_INTERVAL_SLOW_SEC * 1000;

        while (sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
            cancellationToken.ThrowIfCancellationRequested(); // Cek cancel awal loop
            try {
                string args = $"codespace ssh -c \"{codespaceName}\" -- echo ready";
                string res = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); // Timeout probe pendek
                cancellationToken.ThrowIfCancellationRequested(); // Cek cancel setelah await
                if (res != null && res.Contains("ready")) { AnsiConsole.MarkupLine("[green]✓ SSH Ready[/]"); return true; } // Sukses
                AnsiConsole.Markup($"[dim]?[/] "); // Output aneh?
            } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for SSH[/]"); throw; } // Lempar cancel
            catch { AnsiConsole.Markup($"[dim]x[/]"); } // Error -> coba lagi
            try { await Task.Delay(pollIntervalMs, cancellationToken); } // Delay & Cek Cancel
            catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for SSH[/]"); throw; } // Lempar cancel
        }
        AnsiConsole.MarkupLine($"[yellow]Timeout waiting for SSH connection[/]");
        return false; // Timeout
    }

    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        AnsiConsole.Markup($"[cyan]Checking startup script health (max {HEALTH_CHECK_MAX_DURATION_MIN} min)...[/]");
        int successfulSshChecks = 0; const int SSH_STABILITY_THRESHOLD = 2;

        while (sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
            cancellationToken.ThrowIfCancellationRequested(); // Cek cancel awal loop
            string result = "";
            try {
                string args = $"codespace ssh -c \"{codespaceName}\" -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
                result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
                cancellationToken.ThrowIfCancellationRequested(); // Cek cancel setelah await

                if (result.Contains("FAILED")) { AnsiConsole.MarkupLine($"[red]✗ Startup script failed (found fail flag)[/]"); return false; } // Gagal
                if (result.Contains("HEALTHY")) { AnsiConsole.MarkupLine("[green]✓ Healthy (found done flag)[/]"); return true; } // Sukses
                if (result.Contains("NOT_READY")) { AnsiConsole.Markup($"[dim]_[/]"); successfulSshChecks++; if (successfulSshChecks >= SSH_STABILITY_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) { AnsiConsole.MarkupLine($"[cyan]✓ SSH stable & script not failed. Assuming OK.[/]"); return true; } } // Belum selesai / OK
                else { AnsiConsole.Markup($"[yellow]?[/]"); successfulSshChecks = 0; } // Output aneh
            } catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while checking health[/]"); throw; } // Lempar cancel
            catch { AnsiConsole.Markup($"[red]x[/]"); successfulSshChecks = 0; } // Error SSH/cmd -> coba lagi
            try { await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000, cancellationToken); } // Delay & Cek Cancel
            catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]Cancelled while checking health[/]"); throw; } // Lempar cancel
        }
        AnsiConsole.MarkupLine($"[yellow]Timeout waiting for health flag[/]");
        return false; // Timeout
    }

    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("[cyan]Triggering remote auto-start.sh script...[/]");
        string repo = token.Repo.ToLower();
        string scriptPath = $"/workspaces/{repo}/auto-start.sh";
        AnsiConsole.Markup("[dim]Executing command in background (nohup)... [/]");
        string command = $"nohup bash \"{scriptPath.Replace("\"", "\\\"")}\" > /tmp/startup.log 2>&1 &"; // Escape path jika perlu
        string args = $"codespace ssh -c \"{codespaceName}\" -- {command}"; // Command langsung setelah --
        try {
            // Tidak perlu output, timeout pendek cukup
            await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
            AnsiConsole.MarkupLine("[green]OK[/]");
        } catch (Exception ex) {
            // Log warning tapi jangan gagalkan proses
            AnsiConsole.MarkupLine($"[yellow]Warn: Failed to trigger auto-start: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
        }
    }

    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[dim]Fetching running bot sessions (tmux)...[/]");
        string args = $"codespace ssh -c \"{codespaceName}\" -- tmux list-windows -t automation_hub_bots -F \"#{{window_name}}\"";
        try {
            string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS);
            // Filter dan urutkan
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(s => s != "dashboard" && s != "bash") // Hapus dashboard/bash
                         .OrderBy(s => s) // Urutkan A-Z
                         .ToList();
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Failed to fetch tmux sessions: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine($"[dim](Normal if bots haven't started or codespace is new)[/]");
            return new List<string>(); // Return list kosong jika gagal
        }
        // === FIX CS0161: Tambahkan return default di akhir ===
        // return new List<string>(); // Seharusnya tidak tercapai
    }

    // Class DTO JSON (tidak berubah)
    private class CodespaceInfo { [JsonPropertyName("name")] public string Name { get; set; } = ""; [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; [JsonPropertyName("state")] public string State { get; set; } = ""; [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = ""; }

} // Akhir class CodespaceManager
