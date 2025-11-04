using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core;
using Orchestrator.Util;

namespace Orchestrator.Codespace
{
    public static class CodeManager
    {
        // Fungsi ini TIDAK BERUBAH
        public static async Task<string> CreateCodespaceAsync(TokenEntry token, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[cyan]Mencoba membuat codespace baru...[/]");
            var (stdout, stderr, exitCode) = await CodeActions.RunCommandAsync(token, 
                "kyugito666/automation-hub", 
                "gh codespace create -r kyugito666/automation-hub -b main --default-image-name 'mcr.microsoft.com/devcontainers/universal:2' -m 'standardLinux' --retention-period '1d' --display-name 'AutomationHubRunner'", 
                cancellationToken, 
                useProxy: false);

            if (exitCode != 0)
            {
                if (stderr.Contains("could not create codespace") && stderr.Contains("quota"))
                {
                    throw new Exception("Gagal membuat codespace: Kuota habis. Hapus codespace lama.");
                }
                throw new Exception($"Gagal membuat codespace (Exit Code: {exitCode}): {stderr}");
            }
            
            // Cari nama codespace dari output
            string? newName = null;
            if (stdout.Contains("Creating codespace"))
            {
                 AnsiConsole.MarkupLine("[yellow]Codespace dibuat, mengambil nama...[/]");
                 await Task.Delay(5000, cancellationToken); // Kasih jeda 5 detik biar API update
                 var foundName = await FindActiveCodespaceAsync(token, cancellationToken);
                 if(foundName == null)
                 {
                    throw new Exception("Gagal mengambil nama codespace setelah dibuat. Coba lagi.");
                 }
                 newName = foundName;
            } 
            else 
            {
                // Fallback jika output beda
                newName = stdout.Trim();
                if (string.IsNullOrEmpty(newName)) throw new Exception("Gagal parse nama codespace dari output gh.");
            }
            
            AnsiConsole.MarkupLine($"[green]✓ Codespace [blue]{newName.EscapeMarkup()}[/] berhasil dibuat.[/]");
            return newName;
        }

        // Fungsi ini TIDAK BERUBAH
        public static async Task<string?> FindActiveCodespaceAsync(TokenEntry token, CancellationToken cancellationToken)
        {
            var (stdout, stderr, exitCode) = await CodeActions.RunCommandAsync(token, 
                "kyugito666/automation-hub", 
                "gh codespace list --json name,repository,state,displayName --jq '.[] | select(.repository.nameWithOwner == \"kyugito666/automation-hub\" and .state == \"Available\" and .displayName == \"AutomationHubRunner\") | .name'", 
                cancellationToken, 
                useProxy: false);

            if (exitCode != 0)
            {
                throw new Exception($"Gagal list codespace (Exit Code: {exitCode}): {stderr}");
            }

            var codespaceName = stdout.Trim().Split('\n').FirstOrDefault();
            return string.IsNullOrEmpty(codespaceName) ? null : codespaceName;
        }

        // Fungsi ini TIDAK BERUBAH (Sangat penting untuk StartAllEnabledBotsAsync)
        public static async Task StartTmuxSessionAsync(TokenEntry token, string codespaceName, string sessionName, string windowName, string command, CancellationToken cancellationToken)
        {
            // Ganti karakter ilegal untuk nama window tmux
            string safeWindowName = Regex.Replace(windowName, @"[:\.]", "-");
            // Escape quotes di dalam command untuk shell
            string safeCommand = command.Replace("\"", "\\\"");

            // 1. Cek jika sesi ada
            string checkSessionCmd = $"gh codespace ssh --codespace \"{codespaceName}\" -- tmux has-session -t {sessionName}";
            var (_, _, exitCode) = await CodeActions.RunCommandAsync(token, null, checkSessionCmd, cancellationToken, useProxy: false, timeoutMs: 10000);

            if (exitCode != 0)
            {
                // Sesi belum ada, buat baru
                string newSessionCmd = $"gh codespace ssh --codespace \"{codespaceName}\" -- tmux new-session -d -s {sessionName} -n \"{safeWindowName}\" \"{safeCommand}\"";
                await CodeActions.RunCommandAsync(token, null, newSessionCmd, cancellationToken, useProxy: false);
            }
            else
            {
                // Sesi sudah ada, buat window baru
                string newWindowCmd = $"gh codespace ssh --codespace \"{codespaceName}\" -- tmux new-window -t {sessionName} -n \"{safeWindowName}\" \"{safeCommand}\"";
                await CodeActions.RunCommandAsync(token, null, newWindowCmd, cancellationToken, useProxy: false);
            }
        }

        // === FUNGSI BARU: PENGGANTI START_BOTS.SH ===
        /// <summary>
        /// Loop semua bot di config dan jalankan di sesi tmux 'automation_hub_bots'.
        /// Ini adalah pengganti start_bots.sh, tapi di remote.
        /// </summary>
        public static async Task StartAllEnabledBotsAsync(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            // Penting: Ini baca config C#, bukan config wrapper
            var config = Core.BotConfig.Load();
            if (config == null)
            {
                AnsiConsole.MarkupLine("[red]✗ Gagal memuat config/bots_config.json saat StartAllEnabledBotsAsync.[/]");
                return;
            }

            AnsiConsole.MarkupLine("[cyan]=> Memulai proses 'Start All Bots' (via Wrapper.py) di remote tmux...[/]");
            
            var enabledBots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).ToList();
            if (!enabledBots.Any())
            {
                AnsiConsole.MarkupLine("[yellow]Tidak ada bot aktif (IsBot=true) yang perlu dijalankan.[/]");
                return;
            }

            string tmuxSessionName = "automation_hub_bots";
            
            // Ini adalah path wrapper.py yang kita upload di CodeUpload.cs
            string wrapperScriptPath = "/workspaces/Automation-Hub/setup_tools/wrapper.py";

            foreach (var bot in enabledBots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                AnsiConsole.Markup($"[dim]  -> Menyiapkan [yellow]{bot.Name.EscapeMarkup()}[/]... [/]");

                // Ini adalah CWD di dalem codespace
                string botRemotePath = $"/workspaces/Automation-Hub/{bot.Path}";
                
                // Ini adalah command yang SAMA PERSIS dengan start_bots.sh
                // "cd \"/path/bot\" && python3 /path/wrapper.py \"Bot Name\""
                string command = $"cd \"{botRemotePath}\" && python3 {wrapperScriptPath} \"{bot.Name}\"";

                try
                {
                    // Panggil fungsi yang udah ada buat bikin window + jalanin command
                    await StartTmuxSessionAsync(
                        token, 
                        codespaceName, 
                        tmuxSessionName, 
                        bot.Name, // Nama window = Nama Bot
                        command,  // Command yang mau dijalanin
                        cancellationToken
                    );
                    
                    AnsiConsole.MarkupLine($"[green] ✓[/]");
                    await Task.Delay(500, cancellationToken); // Kasih jeda antar bot
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red] ✗ Gagal start bot [yellow]{bot.Name.EscapeMarkup()}[/]: {ex.Message.EscapeMarkup()}[/]");
                }
            }
            
            AnsiConsole.MarkupLine("[green]=> [bold]Selesai memberi perintah start ke semua bot.[/][/]");
            AnsiConsole.MarkupLine("[dim]Sesi Tmux 'automation_hub_bots' sekarang berjalan di remote.[/]");
        }

        // === FUNGSI LAMA DIHAPUS ===
        // GetTmuxSessions() dihapus karena Menu 4 (Attach) sudah tidak ada.
    }
}
