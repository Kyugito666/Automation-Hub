using Spectre.Console;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core; 
using Orchestrator.Services; 
using Orchestrator.Util; // <-- WAJIB
using System; // <-- WAJIB

namespace Orchestrator.Codespace
{
    internal static class CodeUpload
    {
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
                var filePath = Path.Combine(localBotDir, fileName);
                if (File.Exists(filePath)) { existingFiles.Add(fileName); }
            }
            return existingFiles; 
        }

        // === PERBAIKAN: Menggunakan RunProcessWithFileStdinAsync ===
        internal static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("\n[cyan]═══ Uploading Credentials & Configs via SSH (Stdin Stream Mode) ═══[/]");
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

                                task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";

                                try
                                {
                                    // Buat command gabungan
                                    string cmd = $"mkdir -p '{remoteBotDir.Replace("'", "'\\''")}' && cat > '{remoteFilePath.Replace("'", "'\\''")}'";
                                    string sshArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{cmd}\"";
                                    
                                    // Siapkan ProcessStartInfo
                                    var startInfo = ShellUtil.CreateStartInfo("gh", sshArgs, token, useProxy: false); // No proxy untuk 'gh'
                                    
                                    // Panggil helper baru untuk streaming
                                    await ShellUtil.RunProcessWithFileStdinAsync(startInfo, localFilePath, cancellationToken);
                                    
                                    filesUploaded++; 
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception cpEx)
                                {
                                    AnsiConsole.MarkupLine($"[red]✗ Upload FAILED (Stream):[/] {bot.Name}/{credFileName}");
                                    AnsiConsole.MarkupLine($"[dim]   {cpEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                                    filesSkipped++; 
                                }
                                try { await Task.Delay(50, cancellationToken); } catch (OperationCanceledException) { throw; }
                            }
                            botsProcessed++;
                            task.Increment(1);
                        } 

                        // STEP 2: ProxySync Configs
                        task.Description = "[cyan]Uploading ProxySync Configs...";
                        var proxySyncConfigFiles = new List<string> { "apikeys.txt", "apilist.txt" };
                        string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/proxysync/config".Replace('\\', '/');
                        
                        foreach (var configFileName in proxySyncConfigFiles)
                        {
                             if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                             
                             string localConfigPath = Path.Combine(ConfigRoot, configFileName); 
                             string remoteConfigPath = $"{remoteProxySyncConfigDir}/{configFileName}".Replace('\\', '/');

                             if (!File.Exists(localConfigPath)) { continue; }
                             
                             task.Description = $"[cyan]Uploading:[/] proxysync/{configFileName}";

                             try
                             { 
                                // Buat command gabungan
                                string cmd = $"mkdir -p '{remoteProxySyncConfigDir.Replace("'", "'\\''")}' && cat > '{remoteConfigPath.Replace("'", "'\\''")}'";
                                string sshArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{cmd}\"";

                                // Siapkan ProcessStartInfo
                                var startInfo = ShellUtil.CreateStartInfo("gh", sshArgs, token, useProxy: false); // No proxy untuk 'gh'

                                // Panggil helper baru untuk streaming
                                await ShellUtil.RunProcessWithFileStdinAsync(startInfo, localConfigPath, cancellationToken);
                                
                                filesUploaded++; 
                             }
                             catch (OperationCanceledException) { throw; }
                             catch (Exception cpEx)
                             {
                                AnsiConsole.MarkupLine($"[red]✗ Upload FAILED (Stream):[/] proxysync/{configFileName}");
                                AnsiConsole.MarkupLine($"[dim]   {cpEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                                filesSkipped++; 
                             }
                             try { await Task.Delay(50, cancellationToken); } catch (OperationCanceledException) { throw; }
                        }
                        task.Increment(1);
                        // === SELESAI BLOK PERBAIKAN ===
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
