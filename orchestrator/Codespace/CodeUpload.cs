using Spectre.Console;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core;
using Orchestrator.Util;

namespace Orchestrator.Codespace
{
    public static class CodeUpload
    {
        public static async Task RunUploadsAsync(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            var config = BotConfig.Load();
            if (config == null)
            {
                AnsiConsole.MarkupLine("[red]✗ Gagal memuat bots_config.json.[/]");
                return;
            }

            var botsToUpload = config.BotsAndTools
                .Where(b => b.Enabled && !string.IsNullOrEmpty(b.RepoUrl) && (b.RepoUrl.StartsWith("D:") || b.RepoUrl.StartsWith("C:") || b.RepoUrl.StartsWith("E:") || b.RepoUrl.StartsWith("F:")))
                .ToList();

            if (!botsToUpload.Any())
            {
                AnsiConsole.MarkupLine("[yellow]Tidak ada bot/tool (di drive lokal) yang perlu di-upload.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[cyan]Mendeteksi [white]{botsToUpload.Count}[/] bot/tool untuk di-upload...[/]");
                
                foreach (var bot in botsToUpload)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string localPath = bot.RepoUrl; 
                    string remotePath = $"/workspaces/Automation-Hub/{bot.Path}"; 
                    
                    AnsiConsole.Markup($"[dim]  - Uploading [yellow]{bot.Name.EscapeMarkup()}[/] -> [blue]{remotePath.EscapeMarkup()}[/]... [/]");
                    try
                    {
                        await UploadDirectoryAsync(token, codespaceName, localPath, remotePath, cancellationToken);
                        AnsiConsole.MarkupLine($"[green]✓[/]");
                    }
                    catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]CANCELLED[/]"); throw; }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗ ERROR: {ex.Message.EscapeMarkup()}[/]");
                    }
                }
            }
        }
        
        private static async Task UploadDirectoryAsync(TokenEntry token, string codespaceName, string localPath, string remotePath, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(localPath))
            {
                throw new DirectoryNotFoundException($"Local path not found: {localPath}");
            }

            string zipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            string remoteZipPath = $"/workspaces/Automation-Hub/{Guid.NewGuid()}.zip";

            try
            {
                ZipFile.CreateFromDirectory(localPath, zipPath, CompressionLevel.Fastest, false);
                
                cancellationToken.ThrowIfCancellationRequested();
                
                await CodeActions.UploadFileAsync(token, codespaceName, zipPath, remoteZipPath, cancellationToken, useProxy: false);
                
                cancellationToken.ThrowIfCancellationRequested();

                string mkdirCommand = $"mkdir -p \"{remotePath}\"";
                await CodeActions.RunCommandAsync(token, codespaceName, mkdirCommand, cancellationToken, useProxy: false);

                string unzipCommand = $"unzip -o \"{remoteZipPath}\" -d \"{remotePath}\"";
                await CodeActions.RunCommandAsync(token, codespaceName, unzipCommand, cancellationToken, useProxy: false);

                string rmCommand = $"rm \"{remoteZipPath}\"";
                await CodeActions.RunCommandAsync(token, codespaceName, rmCommand, cancellationToken, useProxy: false);
            }
            finally
            {
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }
            }
        }
    }
}
