using Spectre.Console;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core; 
using Orchestrator.Services; 

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
                 AnsiConsole.MarkupLine($"[yellow]Warn: Local dir not found: {localBotDir.EscapeMarkup()}[/]");
                 return existingFiles; 
            }
            foreach (var fileName in allPossibleFiles) {
                var filePath = Path.Combine(localBotDir, fileName);
                if (File.Exists(filePath)) { existingFiles.Add(fileName); }
            }
            return existingFiles; 
        }

        internal static async Task UploadCredentialsToCodespace(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
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

                        // STEP 1: MKDIR SEMUA FOLDER DULU
                        AnsiConsole.MarkupLine("\n[bold cyan]STEP 1: Creating all directories inside codespace...[/]");
                        var allBotPaths = new List<string>();
                        
                        foreach (var bot in config.BotsAndTools)
                        {
                            if (bot.Name == "ProxySync-Tool") continue;
                            if (!bot.Enabled) continue;
                            
                            string localBotDir = BotConfig.GetLocalBotPath(bot.Path);
                            if (!Directory.Exists(localBotDir)) continue;
                            
                            var filesToUpload = GetFilesToUploadForBot(localBotDir, botCredentialFiles);
                            if (!filesToUpload.Any()) continue;
                            
                            string remoteBotDir = Path.Combine(remoteWorkspacePath, bot.Path).Replace('\\', '/');
                            allBotPaths.Add(remoteBotDir);
                        }
                        
                        string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/proxysync/config";
                        allBotPaths.Add(remoteProxySyncConfigDir);
                        
                        if (allBotPaths.Any())
                        {
                            AnsiConsole.MarkupLine($"[cyan]Creating {allBotPaths.Count} directories via SSH...[/]");
                            string allPathsEscaped = string.Join(" ", allBotPaths.Select(p => $"'{p.Replace("'", "'\\''")}'"));
                            string mkdirCmd = $"mkdir -p {allPathsEscaped}";
                            string sshMkdirArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                            
                            try {
                                await GhService.RunGhCommandNoProxyAsync(token, sshMkdirArgs, 120000);
                                
                                // FIX: Ganti verification ping dengan simple delay
                                AnsiConsole.MarkupLine($"[green]✓ All {allBotPaths.Count} directories created[/]");
                                AnsiConsole.MarkupLine("[dim]   Waiting 6 seconds for filesystem sync...[/]");
                                await Task.Delay(6000, cancellationToken);
                                AnsiConsole.MarkupLine("[green]   ✓ Sync delay complete[/]");
                                
                            } catch (Exception mkdirEx) {
                                AnsiConsole.MarkupLine($"[red]✗ Failed create directories: {mkdirEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                                throw new Exception("Critical: Directory creation failed");
                            }
                        }

                        // STEP 2: UPLOAD FILES
                        AnsiConsole.MarkupLine("\n[bold cyan]STEP 2: Uploading files from local...[/]");
                        
                        foreach (var bot in config.BotsAndTools)
                        {
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                            task.Description = $"[green]Checking:[/] {bot.Name}";

                            if (bot.Name == "ProxySync-Tool") { task.Increment(1); continue; }
                            if (!bot.Enabled) { botsSkipped++; task.Increment(1); continue; }

                            string localBotDir = BotConfig.GetLocalBotPath(bot.Path);
                            if (!Directory.Exists(localBotDir)) { botsSkipped++; task.Increment(1); continue; }

                            var filesToUpload = GetFilesToUploadForBot(localBotDir, botCredentialFiles);
                            if (!filesToUpload.Any()) { botsSkipped++; task.Increment(1); continue; }

                            string remoteBotDir = Path.Combine(remoteWorkspacePath, bot.Path).Replace('\\', '/');

                            foreach (var credFileName in filesToUpload) {
                                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                                string localFilePath = Path.Combine(localBotDir, credFileName); 
                                string remoteFilePath = $"{remoteBotDir}/{credFileName}";
                                task.Description = $"[cyan]Uploading:[/] {bot.Name}/{credFileName}";
                                string localAbsPath = Path.GetFullPath(localFilePath); 
                                string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteFilePath}\"";
                                
                                try { 
                                    await GhService.RunGhCommandNoProxyAsync(token, cpArgs, 300000); 
                                    filesUploaded++; 
                                }
                                catch (OperationCanceledException) { throw; }
                                catch { filesSkipped++; }
                                try { await Task.Delay(200, cancellationToken); } catch (OperationCanceledException) { throw; }
                            }
                            botsProcessed++;
                            task.Increment(1);
                        } 

                        // Upload ProxySync configs
                        task.Description = "[cyan]Uploading ProxySync Configs...";
                        var proxySyncConfigFiles = new List<string> { "apikeys.txt", "apilist.txt" };
                        string remoteProxySyncConfigDir2 = $"{remoteWorkspacePath}/proxysync/config";
                        
                        foreach (var configFileName in proxySyncConfigFiles) {
                             if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                             string localConfigPath = Path.Combine(ConfigRoot, configFileName); 
                             string remoteConfigPath = $"{remoteProxySyncConfigDir2}/{configFileName}";
                             if (!File.Exists(localConfigPath)) { continue; }
                             task.Description = $"[cyan]Uploading:[/] proxysync/{configFileName}";
                             string localAbsPath = Path.GetFullPath(localConfigPath); 
                             string cpArgs = $"codespace cp -c \"{codespaceName}\" \"{localAbsPath}\" \"remote:{remoteConfigPath}\"";
                             
                             try { 
                                 await GhService.RunGhCommandNoProxyAsync(token, cpArgs, 120000); 
                                 filesUploaded++; 
                             }
                             catch (OperationCanceledException) { throw; }
                             catch { filesSkipped++; }
                             try { await Task.Delay(100, cancellationToken); } catch (OperationCanceledException) { throw; }
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
