using Spectre.Console;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core; // Menggunakan BotConfig
using Orchestrator.Services; // Menggunakan GhService

namespace Orchestrator.Codespace
{
    internal static class CodeUpload
    {
        private static readonly string ProjectRoot = GetProjectRoot();
        private static readonly string ConfigRoot = Path.Combine(ProjectRoot, "config");
        private static readonly string UploadFilesListPath = Path.Combine(ConfigRoot, "upload_files.txt");

        private static string GetProjectRoot()
        {
            // Logika GetProjectRoot (sama seperti di BotConfig)
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

        // --- Fungsi Utama (dari CodespaceManager) ---
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
                                // Menggunakan GhService
                                await GhService.RunGhCommand(token, sshMkdirArgs, 90000); mkdirSuccess = true;
                            } catch (OperationCanceledException) { throw; }
                            catch (Exception mkdirEx) { AnsiConsole.MarkupLine($"[red]✗ Failed mkdir for {bot.Name}: {mkdirEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); }

                            bool dirExists = false;
                            if (mkdirSuccess) {
                                task.Description = $"[grey]Verifying dir:[/] {bot.Name}";
                                try {
                                    await Task.Delay(500, cancellationToken); string testCmd = $"test -d {escapedRemoteBotDir}";
                                    string sshTestArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{testCmd}\"";
                                    // Menggunakan GhService
                                    await GhService.RunGhCommand(token, sshTestArgs, 30000); dirExists = true;
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
                                    // Menggunakan GhService
                                    try { await GhService.RunGhCommand(token, cpArgs, 120000); filesUploaded++; }
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
                        string remoteProxySyncConfigDir = $"{remoteWorkspacePath}/proxysync/config"; string escapedRemoteProxySyncDir = $"'{remoteProxySyncConfigConfigDir.Replace("'", "'\\''")}'";
                        bool proxySyncConfigUploadSuccess = true; bool proxySyncDirExists = false;
                        try {
                            string mkdirCmd = $"mkdir -p {escapedRemoteProxySyncDir}"; string sshMkdirArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{mkdirCmd}\"";
                            await GhService.RunGhCommand(token, sshMkdirArgs, 60000);
                            await Task.Delay(500, cancellationToken); string testCmd = $"test -d {escapedRemoteProxySyncDir}";
                            string sshTestArgs = $"codespace ssh -c \"{codespaceName}\" -- \"{testCmd}\"";
                            await GhService.RunGhCommand(token, sshTestArgs, 30000);
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
                                 try { await GhService.RunGhCommand(token, cpArgs, 60000); filesUploaded++; }
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
    }
}
