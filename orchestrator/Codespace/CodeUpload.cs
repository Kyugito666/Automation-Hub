using Spectre.Console;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core; 
using Orchestrator.Services; 
using Orchestrator.Util; 
using System; 

namespace Orchestrator.Codespace
{
    internal static class CodeUpload
    {
        private static readonly string ProjectRoot = GetProjectRoot();
        private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
        private static readonly string UploadFilesListPath = Path.Combine(ConfigRoot, "upload_files.txt");
        
        private const int UPLOAD_RETRY_DELAY_MS = 5000;

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
            // Mengambil daftar dari file, tapi pastikan file-file config rahasia ADA
            var defaultList = new List<string> { "pk.txt", "privatekey.txt", "token.txt", "tokens.txt", ".env", "config.json", "data.txt", "query.txt", "wallet.txt", "settings.yaml", "mnemonics.txt" };
            
            if (!File.Exists(UploadFilesListPath)) {
                AnsiConsole.MarkupLine($"[yellow]Warn: '{UploadFilesListPath}' (dari commit {Program.AppCommitHash}) not found. Using defaults.[/]");
                return defaultList;
            }
            try {
                var lines = File.ReadAllLines(UploadFilesListPath)
                           .Select(l => l.Trim())
                           .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                           .ToList();
                
                // Pastikan file config RAHASIA (yg tidak di-commit) ada di daftar default
                if (!lines.Contains("apikeys.txt")) lines.Add("apikeys.txt");
                if (!lines.Contains("apilist.txt")) lines.Add("apilist.txt");

                return lines;

            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error reading '{UploadFilesListPath}': {ex.Message.EscapeMarkup()}. Using defaults.[/]");
                return defaultList; 
            }
        }

        private static List<string> GetFilesToUploadForBot(string localBotDir, List<string> allPossibleFiles)
        {
            var existingFiles = new List<string>();
            if (!Directory.Exists(localBotDir)) {
                 return existingFiles; 
            }
            foreach (var fileName in allPossibleFiles) {
                // Normalisasi nama file (buat jaga-jaga)
                var normalizedFileName = Path.GetFileName(fileName);
                var filePath = Path.Combine(localBotDir, normalizedFileName);
                if (File.Exists(filePath)) { existingFiles.Add(normalizedFileName); }
            }
            return existingFiles; 
        }

        internal static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("\n[cyan]═══ Uploading Credentials & Configs via SSH (Stdin Stream Mode) ═══[/]");
            var config = BotConfig.Load();
            if (config == null) { AnsiConsole.MarkupLine("[red]✗ Gagal load bots_config.json. Upload batal.[/]"); return; }

            var botCredentialFiles = LoadUploadFileList();
            int botsProcessed = 0; int filesUploaded = 0; int filesSkipped = 0; int botsSkipped = 0;
            
            string remoteWorkspacePath = $"/workspaces/{token.Repo}";

            AnsiConsole.MarkupLine($"[dim]Remote workspace: {remoteWorkspacePath}[/]");
            AnsiConsole.MarkupLine($"[dim]Scanning {botCredentialFiles.Count} possible credential files per bot...[/]");

            try {
                await AnsiConsole.Progress()
                    .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn() })
                    .StartAsync(async ctx => {
                        var task = ctx.AddTask("[green]Processing bots & configs...[/]", new ProgressTaskSettings { MaxValue = config.BotsAndTools.Count + 1 });

                        // STEP 1: Bot Credentials
                        foreach (var bot in config.BotsAndTools)
                        {
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                            task.Description = $"[green]Checking:[/] {bot.Name}";

                            if (bot.Name == "ProxySync-Tool") { task.Increment(1); continue; }
                            if (!bot.Enabled) { botsSkipped++; task.Increment(1); continue; }

                            string localBotDir = BotConfig.GetLocalBotPath(bot.Path);
                            var filesToUpload = GetFilesToUploadForBot(localBotDir, botCredentialFiles);
                            if (!filesToUpload.Any()) { botsSkipped++; task.Increment(1); continue; }

                            foreach (var credFileName in filesToUpload)
                            {
                                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                                
                                string localFilePath = Path.Combine(localBotDir, credFileName); 
                                string remoteFilePath = $"{remoteWorkspacePath}/{bot.Path}/{credFileName}".Replace('\\', '/');
                                string remoteBotDir = Path.GetDirectoryName(remoteFilePath)!.Replace('\\', '/');

                                bool uploadSuccess = false;
                                int retryCount = 0; 

                                while (true) 
                                {
                                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();

                                    string taskMessage;
                                    if (retryCount == 0) {
                                        taskMessage = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";
                                    } else {
                                        taskMessage = $"[yellow](Retry {retryCount})[/] {bot.Name}/{credFileName}";
                                    }
                                    task.Description = taskMessage;

                                    try
                                    {
                                        string cmd = $"mkdir -p '{remoteBotDir.Replace("'", "'\\''")}' && cat > '{remoteFilePath.Replace("'", "'\\''")}'";
                                        string sshArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{cmd}\"";
                                        
                                        var startInfo = ShellUtil.CreateStartInfo("gh", sshArgs, token, useProxy: false); 
                                        
                                        await ShellUtil.RunProcessWithFileStdinAsync(startInfo, localFilePath, cancellationToken);
                                        
                                        filesUploaded++; 
                                        uploadSuccess = true;
                                        break; 
                                    }
                                    catch (OperationCanceledException) { throw; } 
                                    catch (Exception cpEx)
                                    {
                                        // Ini logic retry "keras" buat koneksi gh lokal lu
                                        string errorMsg = cpEx.Message.ToLowerInvariant();
                                        bool isRetryableNetworkError = errorMsg.Contains("connection error") ||
                                                                       errorMsg.Contains("closed network connection") ||
                                                                       errorMsg.Contains("rpc error") ||
                                                                       errorMsg.Contains("unavailable desc") ||
                                                                       errorMsg.Contains("the pipe has been ended") ||
                                                                       errorMsg.Contains("error connecting"); 

                                        if (isRetryableNetworkError)
                                        {
                                            retryCount++;
                                            try { await Task.Delay(UPLOAD_RETRY_DELAY_MS, cancellationToken); } catch (OperationCanceledException) { throw; }
                                            continue; 
                                        }
                                        else
                                        {
                                            AnsiConsole.MarkupLine($"\n[red]✗ Upload FAILED (Fatal Error):[/] {bot.Name}/{credFileName}");
                                            AnsiConsole.MarkupLine($"[dim]   {cpEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                                            uploadSuccess = false;
                                            break; 
                                        }
                                    }
                                } 

                                if (!uploadSuccess)
                                {
                                    filesSkipped++; 
                                }
                                
                                try { await Task.Delay(50, cancellationToken); } catch (OperationCanceledException) { throw; }
                            }
                            botsProcessed++;
                            task.Increment(1);
                        } 

                        // STEP 2: ProxySync Configs (HANYA YANG RAHASIA)
                        task.Description = "[cyan]Uploading Secret Configs...";
                        
                        // === PERBAIKAN: Hapus paths.txt dan github_tokens.txt ===
                        var proxySyncConfigFiles = new List<string> { "apikeys.txt", "apilist.txt" };
                        // === AKHIR PERBAIKAN ===
                        
                        string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/config".Replace('\\', '/');
                        
                        foreach (var configFileName in proxySyncConfigFiles)
                        {
                             if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                             
                             string localConfigPath = Path.Combine(ConfigRoot, configFileName); 
                             string remoteConfigPath = $"{remoteProxySyncConfigDir}/{configFileName}".Replace('\\', '/');

                             if (!File.Exists(localConfigPath)) {
                                 AnsiConsole.MarkupLine($"[yellow]Warn: Secret config file '{configFileName}' not found locally. Skipping upload.[/]");
                                 continue;
                             }
                             
                             bool uploadSuccess = false;
                             int retryCount = 0; 

                             while (true)
                             {
                                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();

                                string taskMessage;
                                if (retryCount == 0) {
                                    taskMessage = $"[cyan]Uploading:[/] config/{configFileName}"; 
                                } else {
                                    taskMessage = $"[yellow](Retry {retryCount})[/] config/{configFileName}";
                                }
                                task.Description = taskMessage;

                                 try
                                 { 
                                    string cmd = $"mkdir -p '{remoteProxySyncConfigDir.Replace("'", "'\\''")}' && cat > '{remoteConfigPath.Replace("'", "'\\''")}'";
                                    string sshArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{cmd}\"";

                                    var startInfo = ShellUtil.CreateStartInfo("gh", sshArgs, token, useProxy: false); 

                                    await ShellUtil.RunProcessWithFileStdinAsync(startInfo, localConfigPath, cancellationToken);
                                    
                                    filesUploaded++; 
                                    uploadSuccess = true;
                                    break; 
                                 }
                                 catch (OperationCanceledException) { throw; }
                                 catch (Exception cpEx)
                                 {
                                        // Ini logic retry "keras" buat koneksi gh lokal lu
                                        string errorMsg = cpEx.Message.ToLowerInvariant();
                                        bool isRetryableNetworkError = errorMsg.Contains("connection error") ||
                                                                       errorMsg.Contains("closed network connection") ||
                                                                       errorMsg.Contains("rpc error") ||
                                                                       errorMsg.Contains("unavailable desc") ||
                                                                       errorMsg.Contains("the pipe has been ended") ||
                                                                       errorMsg.Contains("error connecting");

                                        if (isRetryableNetworkError)
                                        {
                                            retryCount++;
                                            try { await Task.Delay(UPLOAD_RETRY_DELAY_MS, cancellationToken); } catch (OperationCanceledException) { throw; }
                                            continue; 
                                        }
                                        else
                                        {
                                            AnsiConsole.MarkupLine($"\n[red]✗ Upload FAILED (Fatal Error):[/] config/{configFileName}"); 
                                            AnsiConsole.MarkupLine($"[dim]   {cpEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                                            uploadSuccess = false;
                                            break; 
                                        }
                                 }
                             } 
                             
                             if (!uploadSuccess)
                             {
                                 filesSkipped++; 
                             }
                             
                             try { await Task.Delay(50, cancellationToken); } catch (OperationCanceledException) { throw; }
                        }
                        task.Increment(1);
                    }); 
            } catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("\n[yellow]Upload cancelled.[/]"); 
                AnsiConsole.MarkupLine($"[dim]   Partial: Bots OK: {botsProcessed}, Skip: {botsSkipped} | Files OK: {filesUploaded}, Fail: {filesSkipped}[/]"); 
                throw; 
            }
            catch (Exception uploadEx) { 
                AnsiConsole.MarkupLine("\n[red]UNEXPECTED UPLOAD ERROR[/]"); 
                AnsiConsole.WriteException(uploadEx); 
                throw; 
            }
            AnsiConsole.MarkupLine($"\n[green]✓ Upload finished.[/]"); 
            AnsiConsole.MarkupLine($"[dim]   Bots OK: {botsProcessed}, Skip: {botsSkipped} | Files OK: {filesUploaded}, Fail: {filesSkipped}[/]");
        }
    }
}
