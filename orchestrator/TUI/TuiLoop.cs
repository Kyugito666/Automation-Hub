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
            var panel = new Panel(string.Empty)
                .Header(new PanelHeader("Initializing...").SetStyle(Style.Parse("cyan bold")))
                .Border(BoxBorder.Rounded)
                .Expand();

            await AnsiConsole.Live(panel)
                .StartAsync(async ctx =>
                {
                    try
                    {
                        panel.Header = new PanelHeader("Step 1/6: Validating Token").SetStyle(Style.Parse("cyan bold"));
                        ctx.Refresh();
                        var currentToken = TokenManager.GetCurrentToken();
                        if (currentToken == null)
                        {
                            panel.Content = "[red]✗ Token GitHub utama tidak valid. Jalankan Menu 2 (Setup).[/]";
                            return;
                        }
                        panel.Content = $"[green]✓[/] Token [blue]{currentToken.Username.EscapeMarkup()}[/] OK.";
                        ctx.Refresh();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 2/6: Checking 'gh' CLI").SetStyle(Style.Parse("cyan bold"));
                        ctx.Refresh();
                        if (!await GhService.CheckGhCliAsync(currentToken, linkedCtsMenuToken))
                        {
                            panel.Content = "[red]✗ 'gh' CLI error. Pastikan ter-install dan login.[/]";
                            return;
                        }
                        panel.Content = "[green]✓ 'gh' CLI siap.[/]";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 3/6: Finding Active Codespace").SetStyle(Style.Parse("cyan bold"));
                        panel.Content = "[yellow]Mencari codespace 'AutomationHubRunner' yang aktif...[/]";
                        ctx.Refresh();
                        string? activeCodespace = await CodeManager.FindActiveCodespaceAsync(currentToken, linkedCtsMenuToken);

                        if (string.IsNullOrEmpty(activeCodespace))
                        {
                            panel.Content = "[yellow]Codespace aktif tidak ditemukan.[/]";
                            ctx.Refresh();
                            if (!AnsiConsole.Confirm("[bold yellow]Buat codespace baru?[/]", true)) return;
                            
                            panel.Content = "[cyan]Membuat codespace baru... (Bisa 2-3 menit)[/]";
                            ctx.Refresh();
                            activeCodespace = await CodeManager.CreateCodespaceAsync(currentToken, linkedCtsMenuToken);
                            
                            panel.Content = $"[green]✓[/] Codespace baru [blue]{activeCodespace.EscapeMarkup()}[/] dibuat. Menunggu state 'Available'...";
                            ctx.Refresh();
                            await CodeHealth.WaitForCodespaceAvailableAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        }
                        
                        TokenManager.SetState(activeCodespace);
                        panel.Content = $"[green]✓[/] Menggunakan codespace: [blue]{activeCodespace.EscapeMarkup()}[/]";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 4/6: Codespace Health Check").SetStyle(Style.Parse("cyan bold"));
                        await CodeHealth.RunHealthCheckAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        panel.Content = "[green]✓[/] Health check lolos (git, tmux, python, node).";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 5/6: Uploading Bot Repos & Wrapper").SetStyle(Style.Parse("cyan bold"));
                        panel.Content = "[cyan]Memulai proses upload direktori bot (termasuk kredensial file) & wrapper.py...[/]";
                        ctx.Refresh();
                        await CodeUpload.RunUploadsAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        panel.Content = "[green]✓[/] Proses upload direktori bot & wrapper selesai.";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 6/6: Starting Bots via Wrapper.py").SetStyle(Style.Parse("green bold"));
                        panel.Padding = new Padding(2, 1);
                        panel.Content = "Memerintahkan codespace untuk menjalankan semua bot aktif\nvia wrapper.py di tmux...";
                        ctx.Refresh();

                        try
                        {
                            await CodeManager.StartAllEnabledBotsAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        }
                        catch (OperationCanceledException) { throw; } 
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Error saat auto-start bots: {ex.Message.EscapeMarkup()}[/]");
                        }
                        if (linkedCtsMenuToken.IsCancellationRequested) return;
                        
                        panel.Content = "[green]✓[/] Perintah start bot terkirim.";
                        ctx.Refresh();

                        await Task.Delay(500, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("✓ Loop Setup Selesai").SetStyle(Style.Parse("green bold"));
                        panel.Padding = new Padding(2, 1);
                        panel.Content = $"[green]Setup untuk [blue]{activeCodespace.EscapeMarkup()}[/] selesai.[/]\nLoop akan masuk mode monitoring (idle).";
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
                        panel.Header = new PanelHeader("ERROR").SetStyle(Style.Parse("red bold"));
                        panel.Content = $"[red]Loop gagal: {ex.Message.EscapeMarkup()}[/]\nLihat log untuk detail.";
                        AnsiConsole.WriteException(ex);
                    }
                });

            if (_lastRun == DateTime.MinValue) return; 

            while (!linkedCtsMenuToken.IsCancellationRequested)
            {
                await Task.Delay(1000, linkedCtsMenuToken);
            }
        }
    }
}
