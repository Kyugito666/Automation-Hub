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
            if (File.Exists(filePath))
            {
                existingFiles.Add(fileName);
            }
        }
        
        return existingFiles;
    }

    private static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[cyan]═══ Uploading Credentials & Configs via gh cp ═══[/]"); // Judul diubah
        var config = BotConfig.Load();
        // === PERBAIKAN: Handle jika config null ===
        if (config == null) {
             AnsiConsole.MarkupLine("[red]✗ Gagal memuat bots_config.json. Upload dibatalkan.[/]");
             return;
        }
        // === AKHIR PERBAIKAN ===

        // Daftar file kredensial bot (dari upload_files.txt)
        var botCredentialFiles = LoadUploadFileList();

        int botsProcessed = 0; int filesUploaded = 0; int filesSkipped = 0; int botsSkipped = 0;
        string remoteWorkspacePath = $"/workspaces/{token.Repo.ToLowerInvariant()}";

        AnsiConsole.MarkupLine($"[dim]Remote workspace: {remoteWorkspacePath}[/]");
        AnsiConsole.MarkupLine($"[dim]Scanning {botCredentialFiles.Count} possible credential files per bot...[/]");

        try {
            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn() })
                .StartAsync(async ctx => {
                    // Total task = jumlah bot + 1 (untuk ProxySync configs)
                    var task = ctx.AddTask("[green]Processing bots & configs...[/]", new ProgressTaskSettings { MaxValue = config.BotsAndTools.Count + 1 });

                    // --- Loop untuk Bot Kredensial ---
                    foreach (var bot in config.BotsAndTools)
                    {
                        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();

                        task.Description = $"[green]Checking:[/] {bot.Name}";

                        // === PERBAIKAN: Logika Skip ProxySync di sini HANYA untuk kredensial bot ===
                        if (bot.Name == "ProxySync-Tool")
                        {
                            // Kita akan handle config ProxySync di luar loop ini
                            AnsiConsole.MarkupLine($"[dim]SKIP Creds: {bot.Name} (handled separately)[/]");
                            task.Increment(1); // Tetap increment task
                            continue; // Lanjut ke bot berikutnya
                        }
                        // === AKHIR PERBAIKAN ===

                        if (!bot.Enabled) {
                            AnsiConsole.MarkupLine($"[dim]SKIP Disabled: {bot.Name}[/]");
                            task.Increment(1);
                            continue;
                        }

                        string localBotDir = BotConfig.GetLocalBotPath(bot.Path);

                        if (!Directory.Exists(localBotDir))
                        {
                            AnsiConsole.MarkupLine($"[yellow]SKIP No Local Dir: {bot.Name} ({localBotDir.EscapeMarkup()})[/]");
                            botsSkipped++;
                            task.Increment(1);
                            continue;
                        }

                        // Cari file kredensial SPESIFIK untuk bot ini
                        var filesToUpload = GetFilesToUploadForBot(localBotDir, botCredentialFiles);
                        if (!filesToUpload.Any())
                        {
                            AnsiConsole.MarkupLine($"[dim]SKIP No Creds Found: {bot.Name}[/]");
                            botsSkipped++;
                            task.Increment(1);
                            continue;
                        }

                        // Path remote bot (case-sensitive)
                        string remoteBotDir = Path.Combine(remoteWorkspacePath, bot.Path).Replace('\\', '/');
                        
                        task.Description = $"[grey]Creating dir:[/] {bot.Name}";
                        
                        try {
                            // Buat direktori remote bot
                            string mkdirCmd = $"mkdir -p '{remoteBotDir.Replace("'", "'\\''")}'";
                            string sshArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                            // === PERBAIKAN: Timeout mkdir ditambah jadi 60s ===
                            await ShellHelper.RunGhCommand(token, sshArgs, 60000); 
                        }
                        catch (OperationCanceledException) { throw; } // Tangkap cancel
                        catch (Exception mkdirEx) {
                            AnsiConsole.MarkupLine($"[red]✗ Failed mkdir {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()}[/]");
                            filesSkipped += filesToUpload.Count; // Hitung semua file sebagai gagal
                            botsSkipped++;
                            task.Increment(1);
                            continue; // Lanjut ke bot berikutnya
                        }

                        // === PERBAIKAN: Delay 1s untuk mencegah race condition scp ===
                        try { await Task.Delay(1000, cancellationToken); } catch (OperationCanceledException) { throw; } 

                        // Upload file kredensial satu per satu
                        foreach (var credFileName in filesToUpload)
                        {
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();

                            string localFilePath = Path.Combine(localBotDir, credFileName);
                            string remoteFilePath = $"{remoteBotDir}/{credFileName}";
                            task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";
                            string localAbsPath = Path.GetFullPath(localFilePath);

                            string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteFilePath}\"";

                            try {
                                await ShellHelper.RunGhCommand(token, cpArgs, 120000); // Timeout normal untuk cp
                                filesUploaded++;
                            }
                            catch (OperationCanceledException) { throw; } // Tangkap cancel
                            catch { // Gagal upload 1 file (error sudah di log oleh ShellHelper)
                                filesSkipped++;
                                // Tidak perlu log lagi di sini
                            }

                            // Delay kecil antar file
                            try { await Task.Delay(100, cancellationToken); } catch (OperationCanceledException) { throw; }
                        }

                        botsProcessed++;
                        task.Increment(1); // Selesai 1 bot
                    } // Akhir loop bot

                    // === PERBAIKAN: Upload Config Khusus untuk ProxySync ===
                    task.Description = "[cyan]Uploading ProxySync Configs...";
                    
                    // Definisikan file config ProxySync yang dibutuhkan di remote
                    var proxySyncConfigFiles = new List<string> { "apikeys.txt", "apilist.txt" };
                    // Definisikan path remote
                    string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/proxysync/config";

                    bool proxySyncConfigUploadSuccess = true; // Asumsi sukses

                    try {
                        // 1. Buat direktori config di proxysync (remote), -p aman jika sudah ada
                        string mkdirCmd = $"mkdir -p '{remoteProxySyncConfigDir.Replace("'", "'\\''")}'";
                        string sshArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                        await ShellHelper.RunGhCommand(token, sshArgs, 30000);

                        // 2. Loop dan upload setiap file config
                        foreach (var configFileName in proxySyncConfigFiles)
                        {
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();

                            string localConfigPath = Path.Combine(ConfigRoot, configFileName); // Ambil dari config/ lokal
                            string remoteConfigPath = $"{remoteProxySyncConfigDir}/{configFileName}";

                            if (!File.Exists(localConfigPath))
                            {
                                AnsiConsole.MarkupLine($"[yellow]WARN: Local ProxySync config '{configFileName}' not found. Skipping.[/]");
                                continue; // Lewati file ini jika tidak ada di lokal
                            }

                            task.Description = $"[cyan]Uploading:[/] proxysync/{configFileName}";
                            string localAbsPath = Path.GetFullPath(localConfigPath);
                            string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteConfigPath}\"";

                            try {
                                await ShellHelper.RunGhCommand(token, cpArgs, 60000); // Timeout sedang untuk file kecil
                                filesUploaded++; // Hitung sebagai file sukses
                            }
                            catch (OperationCanceledException) { throw; } // Tangkap cancel
                            catch { // Gagal upload config (error sudah di log ShellHelper)
                                filesSkipped++;
                                proxySyncConfigUploadSuccess = false; // Tandai gagal
                                AnsiConsole.MarkupLine($"[red]✗ Failed to upload {configFileName} for ProxySync.[/]");
                                // Lanjut coba upload file config berikutnya
                            }
                            try { await Task.Delay(100, cancellationToken); } catch (OperationCanceledException) { throw; }
                        }
                    }
                    catch (OperationCanceledException) { throw; } // Tangkap cancel saat mkdir
                    catch (Exception ex) {
                         AnsiConsole.MarkupLine($"[red]✗ Error setting up ProxySync config dir: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                         filesSkipped += proxySyncConfigFiles.Count; // Anggap semua gagal
                         proxySyncConfigUploadSuccess = false;
                    }

                    if (proxySyncConfigUploadSuccess) {
                        AnsiConsole.MarkupLine("[green]✓ ProxySync configs uploaded.[/]");
                    } else {
                        AnsiConsole.MarkupLine("[yellow]! Some ProxySync configs failed to upload.[/]");
                    }
                    task.Increment(1); // Selesai task ProxySync config
                    // === AKHIR PERBAIKAN ===
                }); // Akhir Progress
        } catch (OperationCanceledException) {
            AnsiConsole.MarkupLine("\n[yellow]Upload process cancelled by user.[/]");
            // Tampilkan status parsial
            AnsiConsole.MarkupLine($"[dim]   Partial Status: Bots Processed: {botsProcessed}, Files OK: {filesUploaded}, Files Failed: {filesSkipped}[/]");
            throw; // Lempar ulang agar operasi utama berhenti
        } catch (Exception uploadEx) {
            AnsiConsole.MarkupLine("\n[red]━━━ UNEXPECTED UPLOAD ERROR ━━━[/]");
            AnsiConsole.WriteException(uploadEx);
            throw; // Lempar ulang agar operasi utama berhenti
        }

        AnsiConsole.MarkupLine($"\n[green]✓ Upload process finished.[/]");
        AnsiConsole.MarkupLine($"[dim]   Bots Creds: {botsProcessed} processed, {botsSkipped} skipped | Files (incl. ProxySync): {filesUploaded} OK, {filesSkipped} failed[/]");
    } // Akhir UploadCredentialsToCodespace


    public static async Task<string> EnsureHealthyCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[cyan]Ensuring Codespace...[/]");
        CodespaceInfo? codespace = null; Stopwatch stopwatch = Stopwatch.StartNew();

        try {
            AnsiConsole.Markup("[dim]Checking repo commit... [/]");
            var repoLastCommit = await GetRepoLastCommitDate(token); cancellationToken.ThrowIfCancellationRequested();
            if (repoLastCommit.HasValue) AnsiConsole.MarkupLine($"[green]OK ({repoLastCommit.Value:yyyy-MM-dd HH:mm} UTC)[/]"); else AnsiConsole.MarkupLine("[yellow]Fetch failed[/]");

            while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN)
            {
                // Selalu cek cancel di awal setiap iterasi
                cancellationToken.ThrowIfCancellationRequested();
                AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Finding CS '{CODESPACE_DISPLAY_NAME}'... [/]");
                
                var codespaceList = await ListAllCodespaces(token); // ListAllCodespaces punya error handling internal
                cancellationToken.ThrowIfCancellationRequested(); // Cek setelah network call
                codespace = codespaceList.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");

                if (codespace == null) {
                    AnsiConsole.MarkupLine("[yellow]Not found.[/]");
                    // Coba buat, EnsureHealthyCodespace akan menangani cancel di dalamnya
                    return await CreateNewCodespace(token, repoFullName, cancellationToken);
                }

                AnsiConsole.MarkupLine($"[green]Found:[/] [blue]{codespace.Name}[/] [dim]({codespace.State})[/]");
                
                // Cek Outdated (setelah network call)
                cancellationToken.ThrowIfCancellationRequested(); 
                if (repoLastCommit.HasValue && !string.IsNullOrEmpty(codespace.CreatedAt)) {
                     if (DateTime.TryParse(codespace.CreatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var csCreated)) {
                        // csCreated = csCreated.ToUniversalTime(); // AdjustToUniversal sudah membuatnya UTC
                        if (repoLastCommit.Value > csCreated) { 
                            AnsiConsole.MarkupLine($"[yellow]⚠ Codespace created ({csCreated:yyyy-MM-dd HH:mm}) before last commit ({repoLastCommit.Value:yyyy-MM-dd HH:mm}). Deleting...[/]"); 
                            // DeleteCodespace tidak pakai cancel token, biarkan selesai
                            await DeleteCodespace(token, codespace.Name); 
                            codespace = null; 
                            AnsiConsole.MarkupLine("[dim]Waiting 5s before retry...[/]");
                            await Task.Delay(5000); // Tunggu sebentar setelah delete
                            continue; // Coba cari/buat lagi
                        }
                     } else { AnsiConsole.MarkupLine($"[yellow]Warn: Could not parse codespace creation date '{codespace.CreatedAt}'[/]"); }
                }

                // Cek State (setelah network call)
                cancellationToken.ThrowIfCancellationRequested();
                switch (codespace.State)
                {
                    case "Available":
                        AnsiConsole.MarkupLine("[cyan]State: Available. Verifying SSH & Uploading...[/]");
                        // WaitForSshReady & UploadCredentials akan handle cancel internal
                        if (!await WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken)) { 
                            AnsiConsole.MarkupLine($"[red]SSH verification failed for {codespace.Name}. Deleting...[/]"); 
                            await DeleteCodespace(token, codespace.Name); 
                            codespace = null; 
                            break; // Keluar switch, akan delay & retry loop
                        }
                        await UploadCredentialsToCodespace(token, codespace.Name, cancellationToken); // Handle cancel internal
                        
                        AnsiConsole.MarkupLine("[cyan]Triggering startup & checking health...[/]");
                        // Trigger & CheckHealth akan handle cancel internal
                        try { await TriggerStartupScript(token, codespace.Name); } catch {} // Abaikan error trigger
                        
                        // Jika CheckHealth berhasil atau timeout tapi SSH OK, kita anggap OK
                        if (await CheckHealthWithRetry(token, codespace.Name, cancellationToken)) { 
                            AnsiConsole.MarkupLine("[green]✓ Health OK. Codespace ready.[/]"); 
                            stopwatch.Stop(); return codespace.Name; 
                        } else {
                            // Jika CheckHealth gagal total (misal startup script error)
                            // atau timeout DAN SSH juga tidak stabil (dari log CheckHealth)
                            // Kita coba lihat state terakhir, jika masih Available, pakai saja
                            var lastState = await GetCodespaceState(token, codespace.Name);
                            if(lastState == "Available") {
                                AnsiConsole.MarkupLine($"[yellow]WARN: Health check timed out, but state is 'Available'. Using codespace anyway.[/]");
                                stopwatch.Stop(); return codespace.Name;
                            } else {
                                AnsiConsole.MarkupLine($"[red]Health check failed and state is '{lastState ?? "Unknown"}'. Deleting unhealthy codespace...[/]");
                                await DeleteCodespace(token, codespace.Name);
                                codespace = null;
                                break; // Keluar switch, akan delay & retry loop
                            }
                        }

                    case "Stopped": case "Shutdown":
                        AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Attempting to start...[/]");
                        // StartCodespace tidak pakai cancel token
                        await StartCodespace(token, codespace.Name); 
                        // Tunggu state Available (handle cancel internal)
                        if (!await WaitForState(token, codespace.Name, "Available", TimeSpan.FromMinutes(4), cancellationToken)) {
                            AnsiConsole.MarkupLine("[red]Failed to reach 'Available' state after start. Deleting...[/]");
                            await DeleteCodespace(token, codespace.Name);
                            codespace = null; 
                            break; // Keluar switch
                        }
                        // Jika start berhasil, state jadi Available, loop berikutnya akan handle
                        AnsiConsole.MarkupLine("[green]Started. Re-checking status in next loop cycle...[/]");
                        await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000, cancellationToken); // Tunggu interval normal
                        continue; // Langsung ke iterasi loop berikutnya

                    case "Starting": case "Queued": case "Rebuilding": case "Creating": // Tambah Creating
                        AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Waiting {STATE_POLL_INTERVAL_SEC}s...[/]");
                        await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000, cancellationToken); // Tunggu & cek cancel
                        continue; // Lanjut ke iterasi loop berikutnya

                    default: // State aneh (Error, Failed, Unknown, dll)
                        AnsiConsole.MarkupLine($"[red]Unhealthy or unknown state: '{codespace.State}'. Deleting...[/]"); 
                        await DeleteCodespace(token, codespace.Name); 
                        codespace = null; 
                        break; // Keluar switch
                }
                
                // Jika codespace dihapus (karena error/outdated/state buruk)
                if (codespace == null) { 
                    AnsiConsole.MarkupLine("[dim]Waiting 5s before retry...[/]");
                    await Task.Delay(5000, cancellationToken); // Tunggu & cek cancel
                    // Loop akan otomatis mencoba mencari atau membuat baru
                }
            } // Akhir while loop polling
        } catch (OperationCanceledException) {
            AnsiConsole.MarkupLine("\n[yellow]EnsureHealthyCodespace cancelled by user.[/]");
            stopwatch.Stop();
            throw; // Biarkan pemanggil (RunOrchestratorLoopAsync) menangkap ini
        } catch (Exception ex) {
            stopwatch.Stop();
            AnsiConsole.MarkupLine($"\n[red]FATAL error during EnsureHealthyCodespace:[/]");
            AnsiConsole.WriteException(ex);
            // Coba delete codespace jika ada namanya
            if (codespace != null && !string.IsNullOrEmpty(codespace.Name)) {
                AnsiConsole.MarkupLine($"[yellow]Attempting to delete potentially broken codespace {codespace.Name}...[/]");
                try { await DeleteCodespace(token, codespace.Name); } catch {}
            }
            throw; // Lempar ulang error fatal
        }

        // Jika loop timeout (STATE_POLL_MAX_DURATION_MIN tercapai)
        stopwatch.Stop();
        AnsiConsole.MarkupLine($"\n[red]FATAL: Could not ensure a healthy codespace within {STATE_POLL_MAX_DURATION_MIN} minutes.[/]");
        // Coba delete codespace terakhir yang dicek jika ada
        if (codespace != null && !string.IsNullOrEmpty(codespace.Name)) {
             AnsiConsole.MarkupLine($"[yellow]Attempting to delete last known codespace {codespace.Name}...[/]");
             try { await DeleteCodespace(token, codespace.Name); } catch {}
        }
        throw new Exception($"Failed to ensure healthy codespace after multiple attempts."); 
        
    } // Akhir EnsureHealthyCodespace


    private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"\n[cyan]Attempting to create a new codespace...[/]");
        string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
        Stopwatch createStopwatch = Stopwatch.StartNew();
        string newName = "";
        try {
            // Jalankan create command (handle cancel internal)
            newName = await ShellHelper.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS); 
            // Cek cancel SETELAH perintah selesai atau timeout
            cancellationToken.ThrowIfCancellationRequested(); 
            
            if (string.IsNullOrWhiteSpace(newName)) throw new Exception("gh codespace create command returned an empty name");

            AnsiConsole.MarkupLine($"[green]✓ Create command succeeded: {newName}[/] ({createStopwatch.Elapsed:mm\\:ss})");
            
            // --- Proses setelah create command sukses ---
            AnsiConsole.MarkupLine("[cyan]Waiting for codespace to become fully available...[/]"); 
            
            // 1. Tunggu state Available (handle cancel internal)
            if (!await WaitForState(token, newName, "Available", TimeSpan.FromMinutes(6), cancellationToken)) { 
                throw new Exception($"Codespace '{newName}' failed to reach 'Available' state within timeout."); 
            }
            
            // 2. Tunggu SSH Ready (handle cancel internal)
            AnsiConsole.MarkupLine("[cyan]State is Available. Verifying SSH connection...[/]");
            if (!await WaitForSshReadyWithRetry(token, newName, cancellationToken)) { 
                throw new Exception($"SSH connection to '{newName}' failed after becoming available."); 
            }

            // 3. Upload Credentials (handle cancel internal)
            AnsiConsole.MarkupLine("[cyan]SSH OK. Uploading credentials and configs...[/]");
            await UploadCredentialsToCodespace(token, newName, cancellationToken);

            // 4. Finalisasi & Trigger (tidak perlu cancel token)
            AnsiConsole.MarkupLine("[dim]Finalizing setup...[/]"); 
            await Task.Delay(5000); // Delay singkat

            AnsiConsole.MarkupLine("[cyan]Triggering auto-start script...[/]");
            await TriggerStartupScript(token, newName); // Tidak pakai cancel token

            createStopwatch.Stop();
            AnsiConsole.MarkupLine($"[bold green]✓ New codespace '{newName}' created and initialized successfully.[/] ({createStopwatch.Elapsed:mm\\:ss})"); 
            return newName; // Kembalikan nama codespace baru

        } catch (OperationCanceledException) {
             AnsiConsole.MarkupLine("[yellow]Codespace creation cancelled by user.[/]");
             // Jika nama sudah ada (create command sempat selesai), coba stop/delete
             if (!string.IsNullOrWhiteSpace(newName)) {
                 AnsiConsole.MarkupLine($"[yellow]Attempting to clean up partially created codespace {newName}...[/]");
                 // Tidak pakai cancel token untuk cleanup
                 try { await StopCodespace(token, newName); } catch {} 
                 try { await DeleteCodespace(token, newName); } catch {} 
             }
             throw; // Lempar ulang cancel
        } catch (Exception ex) {
            createStopwatch.Stop(); 
            AnsiConsole.MarkupLine($"\n[red]━━━ ERROR CREATING CODESPACE ━━━[/]");
            AnsiConsole.WriteException(ex);
             // Jika nama sudah ada, coba delete
            if (!string.IsNullOrWhiteSpace(newName)) { 
                AnsiConsole.MarkupLine($"[yellow]Attempting to delete failed codespace {newName}...[/]");
                try { await DeleteCodespace(token, newName); } catch {} 
            }
            string info = ""; 
            if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) info = " (Quota limit likely reached)"; 
            else if (ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase)) info = " (Invalid token or permissions issue)";
            else if (ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase)) info = " (Forbidden - possibly rate limit or permissions)";
            // Lempar error baru yang lebih deskriptif
            throw new Exception($"FATAL: Failed to create codespace{info}. Original error: {ex.Message}"); 
        }
    } // Akhir CreateNewCodespace


    private static async Task<List<CodespaceInfo>> ListAllCodespaces(TokenEntry token)
    {
        // Fungsi ini tidak perlu CancellationToken karena timeout ditangani ShellHelper
        string args = "codespace list --json name,displayName,state,createdAt";
        try {
            // ShellHelper akan retry jika network error
            string jsonResult = await ShellHelper.RunGhCommand(token, args); 
            if (string.IsNullOrWhiteSpace(jsonResult) || jsonResult == "[]") return new List<CodespaceInfo>();
            // Parsing JSON bisa gagal, tangkap exception
            try {
                return JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>();
            } catch (JsonException jEx) {
                 AnsiConsole.MarkupLine($"[yellow]Warn: Failed to parse codespace list JSON: {jEx.Message}[/]");
                 return new List<CodespaceInfo>(); // Kembalikan list kosong jika parse gagal
            }
        } catch (Exception ex) { 
            // Tangkap error dari ShellHelper (misal token invalid, rate limit)
            AnsiConsole.MarkupLine($"[red]Error listing codespaces: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            return new List<CodespaceInfo>(); // Kembalikan list kosong jika command gagal total
        }
    }

    private static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName) 
    { 
        // Fungsi ini tidak perlu CancellationToken
        try { 
            string json = await ShellHelper.RunGhCommand(token, $"codespace view --json state -c \"{codespaceName}\"", SSH_PROBE_TIMEOUT_MS); 
            using var doc = JsonDocument.Parse(json); 
            return doc.RootElement.TryGetProperty("state", out var p) ? p.GetString() : null; 
        } catch { 
            // Jika ShellHelper gagal (network error, timeout, 404), kembalikan null
            return null; 
        } 
    }
    
    private static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token) 
    { 
        // Fungsi ini tidak perlu CancellationToken
        try { 
            using var client = TokenManager.CreateHttpClient(token); 
            // Set timeout yang wajar untuk API call
            client.Timeout = TimeSpan.FromSeconds(30); 
            var response = await client.GetAsync($"https://api.github.com/repos/{token.Owner}/{token.Repo}/commits?per_page=1"); 
            
            // Handle error API (rate limit, not found, dll)
            if (!response.IsSuccessStatusCode) {
                 AnsiConsole.MarkupLine($"[yellow]Warn: Failed to fetch last commit ({response.StatusCode}).[/]");
                 return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(); 
            using var doc = JsonDocument.Parse(json); 
            
            // Handle jika repo kosong (tidak ada commit)
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) {
                 AnsiConsole.MarkupLine($"[yellow]Warn: No commits found in repo?[/]");
                 return null;
            }
            
            // Ambil tanggal commit
            var dateString = doc.RootElement[0]
                .GetProperty("commit")
                .GetProperty("committer")
                .GetProperty("date")
                .GetString(); 
                
            // Parse tanggal (sudah dihandle AdjustToUniversal)
            return DateTime.TryParse(dateString, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null; 
        } catch (Exception ex) { 
             AnsiConsole.MarkupLine($"[red]Error fetching last commit: {ex.Message.Split('\n').FirstOrDefault()}[/]");
             return null; 
        } 
    }

    // Delete tidak perlu CancellationToken, biarkan berjalan
    public static async Task DeleteCodespace(TokenEntry token, string codespaceName) 
    { 
        AnsiConsole.MarkupLine($"[yellow]Attempting to delete codespace '{codespaceName}'...[/]"); 
        try { 
            // ShellHelper akan handle retry network error
            await ShellHelper.RunGhCommand(token, $"codespace delete -c \"{codespaceName}\" --force", SSH_COMMAND_TIMEOUT_MS); 
            AnsiConsole.MarkupLine($"[green]✓ Delete command sent successfully for '{codespaceName}'.[/]"); 
        } catch (Exception ex) { 
            // Handle error spesifik 'not found'
            if (ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("find", StringComparison.OrdinalIgnoreCase)) {
                AnsiConsole.MarkupLine($"[dim]Codespace '{codespaceName}' already gone or never existed.[/]"); 
            } else {
                // Log error lain tapi jangan lempar exception
                AnsiConsole.MarkupLine($"[yellow]Warn: Delete command failed for '{codespaceName}': {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
            }
        } 
        // Delay singkat setelah mencoba delete
        await Task.Delay(3000); 
    }
    
    // Stop tidak perlu CancellationToken
    public static async Task StopCodespace(TokenEntry token, string codespaceName) 
    { 
        AnsiConsole.Markup($"[dim]Attempting to stop codespace '{codespaceName}'... [/]"); 
        try { 
            // ShellHelper akan handle retry network error
            await ShellHelper.RunGhCommand(token, $"codespace stop --codespace \"{codespaceName}\"", STOP_TIMEOUT_MS); 
            AnsiConsole.MarkupLine("[green]OK[/]"); 
        } catch (Exception ex) { 
            // Handle jika sudah stopped
            if (ex.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase)) {
                AnsiConsole.MarkupLine("[dim]Already stopped.[/]"); 
            } else {
                 AnsiConsole.MarkupLine($"[yellow]Warn: Stop command failed: {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
            }
        } 
        // Delay singkat setelah mencoba stop
        await Task.Delay(2000); 
    }

    // Start tidak perlu CancellationToken
    private static async Task StartCodespace(TokenEntry token, string codespaceName) 
    { 
        AnsiConsole.Markup($"[dim]Attempting to start codespace '{codespaceName}'... [/]"); 
        try { 
             // ShellHelper akan handle retry network error
            await ShellHelper.RunGhCommand(token, $"codespace start --codespace \"{codespaceName}\"", START_TIMEOUT_MS); 
            AnsiConsole.MarkupLine("[green]OK[/]"); 
        } catch (Exception ex) { 
            // Handle jika sudah available
            if(!ex.Message.Contains("available", StringComparison.OrdinalIgnoreCase)) {
                AnsiConsole.MarkupLine($"[yellow]Warn: Start command failed: {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
            } else {
                AnsiConsole.MarkupLine($"[dim]Already available.[/]"); 
            }
        } 
    }
    
    // WaitForState HARUS handle CancellationToken
    private static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken) 
    { 
        Stopwatch sw = Stopwatch.StartNew(); 
        AnsiConsole.Markup($"[cyan]Waiting for state '{targetState}' (max {timeout.TotalMinutes:F0} min)...[/]"); 
        while(sw.Elapsed < timeout) { 
            cancellationToken.ThrowIfCancellationRequested(); // Cek cancel di awal loop
            var state = await GetCodespaceState(token, codespaceName); // Get state (handle error internal)
            cancellationToken.ThrowIfCancellationRequested(); // Cek cancel setelah network call
            
            if (state == targetState) { 
                AnsiConsole.MarkupLine($"[green]✓ Reached '{targetState}'[/]"); 
                return true; 
            } 
            // State Buruk / Gagal
            if (state == null || state == "Failed" || state == "Error" || state.Contains("Shutting") || state == "Deleted") { 
                AnsiConsole.MarkupLine($"[red]✗ Reached failure state ('{state ?? "Unknown/Deleted"}')[/]"); 
                return false; 
            } 
            // State Transisi, tunggu
            AnsiConsole.Markup($"[dim].[/]"); // Tunjukkan progress
            try {
                await Task.Delay(STATE_POLL_INTERVAL_SEC * 1000, cancellationToken); // Tunggu & cek cancel
            } catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for state '{targetState}'[/]");
                 throw; // Lempar ulang cancel
            }
        } 
        AnsiConsole.MarkupLine($"[yellow]Timeout waiting for state '{targetState}'[/]"); 
        return false; // Timeout
    } 
    
    // WaitForSshReady HARUS handle CancellationToken
    private static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken) 
    { 
        Stopwatch sw = Stopwatch.StartNew(); 
        AnsiConsole.Markup($"[cyan]Waiting for SSH connection (max {SSH_READY_MAX_DURATION_MIN} min)...[/]"); 
        while(sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) { 
            cancellationToken.ThrowIfCancellationRequested(); // Cek cancel di awal
            try { 
                // Jalankan 'echo ready' via SSH (handle cancel internal di ShellHelper)
                string res = await ShellHelper.RunGhCommand(token, $"codespace ssh -c \"{codespaceName}\" -- echo ready", SSH_PROBE_TIMEOUT_MS); 
                 cancellationToken.ThrowIfCancellationRequested(); // Cek cancel setelah network call
                
                // Jika command berhasil DAN output benar
                if (res != null && res.Contains("ready")) { 
                    AnsiConsole.MarkupLine("[green]✓ SSH Ready[/]"); 
                    return true; 
                }
                // Jika command berhasil tapi output salah (jarang terjadi)
                AnsiConsole.Markup($"[dim]?[/] "); 
            } catch (OperationCanceledException) { 
                 AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for SSH[/]");
                 throw; // Lempar ulang cancel
            } catch { 
                // Jika ShellHelper gagal (network error, timeout, dll), coba lagi
                AnsiConsole.Markup($"[dim]x[/]"); 
            } 
            
            // Tunggu sebelum retry
            try {
                await Task.Delay(SSH_READY_POLL_INTERVAL_SEC * 1000, cancellationToken); // Tunggu & cek cancel
            } catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine($"[yellow]Cancelled while waiting for SSH[/]");
                 throw; // Lempar ulang cancel
            }
        } 
        AnsiConsole.MarkupLine($"[yellow]Timeout waiting for SSH connection[/]"); 
        return false; // Timeout
    } 

    // CheckHealth HARUS handle CancellationToken
    public static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken) 
    { 
        Stopwatch sw = Stopwatch.StartNew(); 
        AnsiConsole.Markup($"[cyan]Checking startup script health (max {HEALTH_CHECK_MAX_DURATION_MIN} min)...[/]"); 
        int successfulSshChecks = 0;
        const int SSH_STABILITY_THRESHOLD = 2; // Butuh 2 cek SSH+Script stabil berturut-turut

        while(sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) { 
            cancellationToken.ThrowIfCancellationRequested(); // Cek cancel di awal
            string result = ""; 
            try { 
                // Perintah SSH untuk cek file status
                string args = $"codespace ssh -c \"{codespaceName}\" -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\""; 
                // Jalankan command (handle cancel internal di ShellHelper)
                result = await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); 
                cancellationToken.ThrowIfCancellationRequested(); // Cek cancel setelah network call

                if (result.Contains("FAILED")) { 
                    AnsiConsole.MarkupLine($"[red]✗ Startup script failed (found fail flag)[/]"); 
                    return false; // Gagal permanen
                } 
                if (result.Contains("HEALTHY")) { 
                    AnsiConsole.MarkupLine("[green]✓ Healthy (found done flag)[/]"); 
                    return true; // Sukses
                } 
                if (result.Contains("NOT_READY")) { 
                    // Script belum selesai ATAU belum jalan
                    AnsiConsole.Markup($"[dim]_[/]"); 
                    successfulSshChecks++; // Hitung cek SSH+Script yang berhasil
                    // Jika SSH sudah stabil (beberapa kali cek NOT_READY) setelah 1 menit, anggap OK
                    if (successfulSshChecks >= SSH_STABILITY_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) {
                         AnsiConsole.MarkupLine($"[cyan]✓ SSH stable & script not failed. Assuming OK.[/]");
                         return true;
                    }
                } else {
                     // Output tidak terduga dari SSH
                     AnsiConsole.Markup($"[yellow]?[/]");
                     successfulSshChecks = 0; // Reset counter jika output aneh
                }

            } catch (OperationCanceledException) { 
                 AnsiConsole.MarkupLine($"[yellow]Cancelled while checking health[/]");
                 throw; // Lempar ulang cancel
            } catch { 
                // Jika ShellHelper gagal (network error, timeout), reset counter & coba lagi
                AnsiConsole.Markup($"[red]x[/]"); // Tanda SSH gagal
                successfulSshChecks = 0; 
            } 
            
            // Tunggu sebelum retry
             try {
                await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000, cancellationToken); // Tunggu & cek cancel
            } catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine($"[yellow]Cancelled while checking health[/]");
                 throw; // Lempar ulang cancel
            }
        } 
        AnsiConsole.MarkupLine($"[yellow]Timeout waiting for health flag[/]"); 
        return false; // Timeout
    } 
    
    // Trigger tidak perlu CancellationToken
    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName) 
    { 
        AnsiConsole.MarkupLine("[cyan]Triggering remote auto-start.sh script...[/]"); 
        string repo = token.Repo.ToLower(); 
        string scriptPath = $"/workspaces/{repo}/auto-start.sh"; 
        AnsiConsole.Markup("[dim]Executing command in background (nohup)... [/]"); 
        // Perintah nohup untuk menjalankan di background & log ke /tmp/startup.log
        string command = $"nohup bash \"{scriptPath}\" > /tmp/startup.log 2>&1 &"; 
        string args = $"codespace ssh -c \"{codespaceName}\" -- {command}"; 
        try { 
            // ShellHelper akan handle retry network error
            await ShellHelper.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); // Timeout pendek cukup
            AnsiConsole.MarkupLine("[green]OK[/]"); 
        } catch (Exception ex) { 
            // Log error tapi jangan lempar exception, trigger tidak kritikal jika gagal sesekali
            AnsiConsole.MarkupLine($"[yellow]Warn: Failed to trigger auto-start: {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
        } 
    }

    // GetTmuxSessions tidak perlu CancellationToken
    public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName) 
    { 
        AnsiConsole.MarkupLine($"[dim]Fetching running bot sessions (tmux)...[/]"); 
        // Perintah tmux untuk list windows di sesi target
        string args = $"codespace ssh -c \"{codespaceName}\" -- tmux list-windows -t automation_hub_bots -F \"#{{window_name}}\""; 
        try { 
             // ShellHelper akan handle retry network error
            string result = await ShellHelper.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); 
            // Filter hasil: hapus baris kosong, dashboard, bash, lalu urutkan
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(s => s != "dashboard" && s != "bash")
                         .OrderBy(s => s)
                         .ToList(); 
        } catch (Exception ex) { 
            // Handle jika sesi tmux tidak ada atau command gagal
            AnsiConsole.MarkupLine($"[red]Failed to fetch tmux sessions: {ex.Message.Split('\n').FirstOrDefault()}[/]"); 
            AnsiConsole.MarkupLine($"[dim](This is normal if bots haven't started yet or if the codespace is new)[/]");
            return new List<string>(); // Kembalikan list kosong
        } 
    }
    
    private class CodespaceInfo { [JsonPropertyName("name")] public string Name{get;set;}=""; [JsonPropertyName("displayName")] public string DisplayName{get;set;}=""; [JsonPropertyName("state")] public string State{get;set;}=""; [JsonPropertyName("createdAt")] public string CreatedAt{get;set;}=""; }
} // Akhir class CodespaceManager
