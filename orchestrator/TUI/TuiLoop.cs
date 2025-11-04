using Spectre.Console;
using System;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core;
using Orchestrator.Codespace;
using Orchestrator.Services;
using Orchestrator.Util;

namespace Orchestrator.TUI
{
    internal static class TuiLoop
    {
        private static DateTime _lastRun = DateTime.MinValue;
        private static bool _firstRun = true;

        internal static async Task RunOrchestratorLoopAsync(CancellationToken linkedCtsMenuToken)
        {
            var statusPanel = new Panel(string.Empty)
                .Header(new PanelHeader("Initializing...", Style.Parse("cyan bold")))
                .Border(BoxBorder.Rounded)
                .Expand();

            await AnsiConsole.Live(statusPanel)
                .StartAsync(async ctx =>
                {
                    try
                    {
                        // === 1. Validasi Token ===
                        statusPanel.Header = new PanelHeader("Step 1/6: Validating Token", Style.Parse("cyan bold"));
                        ctx.Refresh();
                        var currentToken = TokenManager.GetCurrentToken();
                        if (currentToken == null)
                        {
                            statusPanel.Content = "[red]✗ Token GitHub utama tidak valid. Jalankan Menu 2 (Setup).[/]";
                            return;
                        }
                        statusPanel.Content = $"[green]✓[/] Token [blue]{currentToken.Username.EscapeMarkup()}[/] OK.";
                        ctx.Refresh();

                        // === 2. Cek 'gh' CLI ===
                        await Task.Delay(250, linkedCtsMenuToken);
                        statusPanel.Header = new PanelHeader("Step 2/6: Checking 'gh' CLI", Style.Parse("cyan bold"));
                        ctx.Refresh();
                        if (!await GhService.CheckGhCliAsync(currentToken, linkedCtsMenuToken))
                        {
                            statusPanel.Content = "[red]✗ 'gh' CLI error. Pastikan ter-install dan login.[/]";
                            return;
                        }
                        statusPanel.Content = "[green]✓ 'gh' CLI siap.[/]";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        // === 3. Cari Codespace Aktif ===
                        await Task.Delay(250, linkedCtsMenuToken);
                        statusPanel.Header = new PanelHeader("Step 3/6: Finding Active Codespace", Style.Parse("cyan bold"));
                        statusPanel.Content = "[yellow]Mencari codespace 'AutomationHubRunner' yang aktif...[/]";
                        ctx.Refresh();
                        string? activeCodespace = await CodeManager.FindActiveCodespaceAsync(currentToken, linkedCtsMenuToken);

                        if (string.IsNullOrEmpty(activeCodespace))
                        {
                            statusPanel.Content = "[yellow]Codespace aktif tidak ditemukan.[/]";
                            ctx.Refresh();
                            if (!AnsiConsole.Confirm("[bold yellow]Buat codespace baru?[/]", true)) return;
                            
                            statusPanel.Content = "[cyan]Membuat codespace baru... (Bisa 2-3 menit)[/]";
                            ctx.Refresh();
                            activeCodespace = await CodeManager.CreateCodespaceAsync(currentToken, linkedCtsMenuToken);
                            
                            statusPanel.Content = $"[green]✓[/] Codespace baru [blue]{activeCodespace.EscapeMarkup()}[/] dibuat. Menunggu state 'Available'...";
                            ctx.Refresh();
                            await CodeHealth.WaitForCodespaceAvailableAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        }
                        
                        TokenManager.SetState(activeCodespace);
                        statusPanel.Content = $"[green]✓[/] Menggunakan codespace: [blue]{activeCodespace.EscapeMarkup()}[/]";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        // === 4. Cek Kesehatan Codespace ===
                        await Task.Delay(250, linkedCtsMenuToken);
                        statusPanel.Header = new PanelHeader("Step 4/6: Codespace Health Check", Style.Parse("cyan bold"));
                        await CodeHealth.RunHealthCheckAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        statusPanel.Content = "[green]✓[/] Health check lolos (git, tmux, python, node).";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        // === 5. Upload Kredensial (Bot Repos) + Wrapper ===
                        await Task.Delay(250, linkedCtsMenuToken);
                        statusPanel.Header = new PanelHeader("Step 5/6: Uploading Bot Repos & Wrapper", Style.Parse("cyan bold"));
                        statusPanel.Content = "[cyan]Memulai proses upload direktori bot (termasuk kredensial file) & wrapper.py...[/]";
                        ctx.Refresh();
                        // CodeUpload.cs SEKARANG meng-upload KEDUA-NYA (bot & wrapper)
                        await CodeUpload.RunUploadsAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        statusPanel.Content = "[green]✓[/] Proses upload direktori bot & wrapper selesai.";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        // =================================================================
                        // === STEP 6: SYNCING SECRETS (DIHAPUS TOTAL) ===
                        // =================================================================

                        // =================================================================
                        // === STEP 6 BARU: Start Bots via Wrapper ===
                        // =================================================================
                        await Task.Delay(250, linkedCtsMenuToken);
                        statusPanel.Header = new PanelHeader("Step 6/6: Starting Bots via Wrapper.py", Style.Parse("green bold"));
                        statusPanel.Content = "Memerintahkan codespace untuk menjalankan semua bot aktif\nvia wrapper.py di tmux...";
                        ctx.Refresh();

                        try
                        {
                            // PANGGIL FUNGSI DARI CODEMANAGER
                            await CodeManager.StartAllEnabledBotsAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        }
                        catch (OperationCanceledException) { throw; } // Biar ditangkep di atas
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Error saat auto-start bots: {ex.Message.EscapeMarkup()}[/]");
                        }
                        if (linkedCtsMenuToken.IsCancellationRequested) return;
                        
                        statusPanel.Content = "[green]✓[/] Perintah start bot terkirim.";
                        ctx.Refresh();
                        // =================================================================
                        // === AKHIR BLOK BARU ===
                        // =================================================================


                        // Final
                        await Task.Delay(500, linkedCtsMenuToken);
                        statusPanel.Header = new PanelHeader("✓ Loop Setup Selesai", Style.Parse("green bold"));
                        statusPanel.Padding = new Padding(2, 1);
                        statusPanel.Content = $"[green]Setup untuk [blue]{activeCodespace.EscapeMarkup()}[/] selesai.[/]\nLoop akan masuk mode monitoring (idle).";
                        ctx.Refresh();

                        _lastRun = DateTime.Now;
                        _firstRun = false;
                    }
                    catch (OperationCanceledException)
                    {
                        AnsiConsole.MarkupLine("\n[yellow]Operasi loop dibatalkan (Ctrl+C).[/]");
                    }
                    catch (Exception ex)
                    {
                        statusPanel.Header = new PanelHeader("ERROR", Style.Parse("red bold"));
                        statusPanel.Content = $"[red]Loop gagal: {ex.Message.EscapeMarkup()}[/]\nLihat log untuk detail.";
                        AnsiConsole.WriteException(ex);
                    }
                });

            // Loop monitoring (setelah setup selesai)
            if (_lastRun == DateTime.MinValue) return; // Jika setup gagal, jangan lanjut

            while (!linkedCtsMenuToken.IsCancellationRequested)
            {
                // Mode monitoring...
                await Task.Delay(1000, linkedCtsMenuToken);
                // (Logika monitoring bisa ditambah di sini nanti)
            }
        }
    }
}
