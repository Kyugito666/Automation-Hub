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
        // Fungsi ini yang dipanggil TuiLoop
        public static async Task RunUploadsAsync(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            var config = BotConfig.Load();
            if (config == null)
            {
                AnsiConsole.MarkupLine("[red]✗ Gagal memuat bots_config.json.[/]");
                return;
            }

            var botsToUpload = config.BotsAndTools
                .Where(b => b.Enabled && !string.IsNullOrEmpty(b.RepoUrl) && (b.RepoUrl.StartsWith("D:") || b.RepoUrl.StartsWith("C:") || b.RepoUrl.StartsWith("E:") || b.RepoUrl.StartsWith("F:"))) // Filter drive lokal
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
                    string localPath = bot.RepoUrl; // Ini path D:/SC/...
                    string remotePath = $"/workspaces/Automation-Hub/{bot.Path}"; // Ini path /workspaces/.../bots/token/nama-bot
                    
                    AnsiConsole.Markup($"[dim]  - Uploading [yellow]{bot.Name.EscapeMarkup()}[/] (termasuk file kredensial) -> [blue]{remotePath.EscapeMarkup()}[/]... [/]");
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
            
            // === BLOK BARU: UPLOAD WRAPPER SCRIPT ===
            AnsiConsole.MarkupLine("[cyan]Meng-upload script wrapper PExpect...[/]");
            try
            {
                await UploadWrapperScriptsAsync(token, codespaceName, cancellationToken);
                AnsiConsole.MarkupLine("[green]✓ Wrapper script berhasil di-upload.[/]");
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine($"[yellow]CANCELLED[/]"); throw; }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ FATAL: Gagal upload wrapper script: {ex.Message.EscapeMarkup()}[/]");
                throw; // Ini fatal, kita harus stop
            }
            // === AKHIR BLOK BARU ===
        }

        // === FUNGSI BARU UNTUK UPLOAD WRAPPER ===
        private static async Task UploadWrapperScriptsAsync(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            // Path ini diambil dari start_bots.sh. GANTI JIKA SALAH.
            // Pastikan path ini menggunakan backslash (Windows style)
            string localWrapperPath = @"D:\SC\myproject\tmux\wrapper.py";
            string localConfigPath = @"D:\SC\myproject\tmux\bots_config.json";
            
            // Path ini adalah target di remote codespace (Linux style)
            string remoteDir = "/workspaces/Automation-Hub/setup_tools";
            string remoteWrapperPath = $"{remoteDir}/wrapper.py";
            string remoteConfigPath = $"{remoteDir}/bots_config.json";

            if (!File.Exists(localWrapperPath))
            {
                throw new FileNotFoundException($"File wrapper.py tidak ditemukan di: {localWrapperPath}", localWrapperPath);
            }
            if (!File.Exists(localConfigPath))
            {
                throw new FileNotFoundException($"File bots_config.json (milik wrapper) tidak ditemukan di: {localConfigPath}", localConfigPath);
            }
            
            // 1. Upload wrapper.py
            AnsiConsole.Markup($"[dim]    - Uploading [yellow]wrapper.py[/]... [/]");
            await CodeActions.UploadFileAsync(token, codespaceName, localWrapperPath, remoteWrapperPath, cancellationToken, useProxy: false);
            AnsiConsole.MarkupLine($"[green]✓[/]");

            // 2. Upload bots_config.json (milik wrapper)
            cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Markup($"[dim]    - Uploading [yellow]bots_config.json[/] (milik wrapper)... [/]");
            await CodeActions.UploadFileAsync(token, codespaceName, localConfigPath, remoteConfigPath, cancellationToken, useProxy: false);
            AnsiConsole.MarkupLine($"[green]✓[/]");
        }
        // === AKHIR FUNGSI BARU ===

        // Fungsi UploadDirectoryAsync (tidak berubah)
        private static async Task UploadDirectoryAsync(TokenEntry token, string codespaceName, string localPath, string remotePath, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(localPath))
            {
                throw new DirectoryNotFoundException($"Local path not found: {localPath}");
            }

            // Path .zip sementara
            string zipPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
            // Path target .zip di remote (root, biar gampang)
            string remoteZipPath = $"/workspaces/Automation-Hub/{Guid.NewGuid()}.zip";

            try
            {
                // 1. Buat Zip
                ZipFile.CreateFromDirectory(localPath, zipPath, CompressionLevel.Fastest, false);
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // 2. Upload Zip
                await CodeActions.UploadFileAsync(token, codespaceName, zipPath, remoteZipPath, cancellationToken, useProxy: false);
                
                cancellationToken.ThrowIfCancellationRequested();

                // 3. Buat folder target di remote
                string mkdirCommand = $"mkdir -p \"{remotePath}\"";
                await CodeActions.RunCommandAsync(token, codespaceName, mkdirCommand, cancellationToken, useProxy: false);

                // 4. Unzip di remote (overwrite)
                string unzipCommand = $"unzip -o \"{remoteZipPath}\" -d \"{remotePath}\"";
                await CodeActions.RunCommandAsync(token, codespaceName, unzipCommand, cancellationToken, useProxy: false);

                // 5. Hapus .zip di remote
                string rmCommand = $"rm \"{remoteZipPath}\"";
                await CodeActions.RunCommandAsync(token, codespaceName, rmCommand, cancellationToken, useProxy: false);
            }
            finally
            {
                // Hapus .zip lokal
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }
            }
        }
    }
}
