using Spectre.Console;
using System;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core;
using Orchestrator.Codespace;
using Orchestrator.Services;

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

            bool isNewCodespace = false;

            await AnsiConsole.Live(panel)
                .StartAsync(async ctx =>
                {
                    try
                    {
                        panel.Header = new PanelHeader("Step 1/7: Validating Token").SetStyle(Style.Parse("cyan bold"));
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
                        panel.Header = new PanelHeader("Step 2/7: Checking Billing").SetStyle(Style.Parse("cyan bold"));
                        panel.Content = "[yellow]Checking billing info...[/]";
                        ctx.Refresh();
                        
                        var billingInfo = await BillingService.GetBillingInfo(currentToken);
                        linkedCtsMenuToken.ThrowIfCancellationRequested();
                        
                        if (!billingInfo.IsQuotaOk)
                        {
                            panel.Content = $"[red]✗ KUOTA HABIS. Cek billing manual.[/]\n[dim]Error: {billingInfo.Error?.EscapeMarkup() ?? "Unknown"}[/]";
                            return;
                        }
                        panel.Content = $"[green]✓[/] Billing OK. Sisa ~{billingInfo.HoursRemaining:F1} jam.";
                        ctx.Refresh();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 3/7: Finding Active Codespace").SetStyle(Style.Parse("cyan bold"));
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
                            
                            panel.Content = $"[green]✓[/] Codespace baru [blue]{activeCodespace.EscapeMarkup()}[/] dibuat. Menunggu SSH ready...";
                            ctx.Refresh();
                            await CodeHealth.WaitForSshReadyWithRetry(currentToken, activeCodespace, linkedCtsMenuToken, useFastPolling: false);
                            isNewCodespace = true;
                        }
                        else
                        {
                            panel.Content = $"[green]✓[/] Codespace ditemukan: [blue]{activeCodespace.EscapeMarkup()}[/]. Menghidupkan via SSH...";
                            ctx.Refresh();
                            await CodeHealth.WaitForSshReadyWithRetry(currentToken, activeCodespace, linkedCtsMenuToken, useFastPolling: false);
                            isNewCodespace = false;
                        }
                        
                        TokenManager.SetState(activeCodespace);
                        panel.Content = $"[green]✓[/] Menggunakan codespace: [blue]{activeCodespace.EscapeMarkup()}[/]";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 4/7: Uploading Bot Repos").SetStyle(Style.Parse("cyan bold"));
                        panel.Content = "[cyan]Memulai proses upload direktori bot...[/]";
                        ctx.Refresh();
                        await CodeUpload.RunUploadsAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        panel.Content = "[green]✓[/] Proses upload direktori bot selesai.";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 5/7: Syncing Secrets").SetStyle(Style.Parse("cyan bold"));
                        panel.Content = "[cyan]Mengambil dan mengatur secrets...[/]";
                        ctx.Refresh();
                        await SecretService.SetAllSecretsAsync(currentToken, activeCodespace, linkedCtsMenuToken);
                        panel.Content = "[green]✓[/] Secrets berhasil disinkronkan.";
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

                        await Task.Delay(250, linkedCtsMenuToken);
                        panel.Header = new PanelHeader("Step 6/7: Triggering Remote Setup & Bot Launcher").SetStyle(Style.Parse("cyan bold"));
                        panel.Content = "[cyan]Menjalankan auto-start.sh di codespace...[/]";
                        ctx.Refresh();
                        bool startupSuccess = await CodeActions.RunStartupScriptAndStreamLogs(currentToken, activeCodespace, isNewCodespace, linkedCtsMenuToken);
                        
                        if (startupSuccess)
                        {
                            panel.Content = "[green]✓[/] Startup script selesai.";
                        }
                        else
                        {
                            panel.Content = "[red]✗[/] Startup script failed. Check logs.";
                        }
                        ctx.Refresh();
                        linkedCtsMenuToken.ThrowIfCancellationRequested();

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
