using Spectre.Console;
using System;
// ... (using lain) ...
using System.Threading.Tasks;

namespace Orchestrator;

internal static class Program
{
    // ... (variabel _mainCts, _interactiveCts, KeepAliveInterval, ErrorRetryDelay tetap sama) ...
    private static CancellationTokenSource _mainCts = new CancellationTokenSource();
    private static CancellationTokenSource? _interactiveCts;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes((3 * 60) + 30); // 3 jam 30 menit
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);


    // ... (Main, RunInteractiveMenuAsync, ShowSetupMenuAsync, ShowLocalMenuAsync, ShowAttachMenuAsync - TIDAK BERUBAH) ...

    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[cyan]   ORCHESTRATOR LOOP STARTED[/]");
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");

        const int MAX_CONSECUTIVE_ERRORS = 3;
        int consecutiveErrors = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken();
            TokenState currentState = TokenManager.GetState();
            string? activeCodespace = currentState.ActiveCodespaceName;

            var username = currentToken.Username ?? "unknown";
            AnsiConsole.MarkupLine($"\n[cyan]═══════════════════════════════════════════════════════════════[/]");
            AnsiConsole.MarkupLine($"[cyan]   TOKEN #{currentState.CurrentIndex + 1}: @{username}[/]");
            AnsiConsole.MarkupLine($"[cyan]═══════════════════════════════════════════════════════════════[/]");

            try
            {
                AnsiConsole.MarkupLine("Checking billing quota...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");

                // === PERBAIKAN LOGIKA ERROR BILLING ===
                if (!billingInfo.IsQuotaOk)
                {
                    // Prioritas: Cek apakah error karena proxy bandel
                    if (billingInfo.Error == BillingManager.PersistentProxyError)
                    {
                        AnsiConsole.MarkupLine("[magenta]Persistent proxy error detected during billing check.[/]");

                        // Langkah 1: Coba IP Auth
                        AnsiConsole.MarkupLine("[magenta]Attempting automatic IP Authorization...[/]");
                        bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(cancellationToken);

                        if (ipAuthSuccess)
                        {
                            AnsiConsole.MarkupLine("[green]IP Authorization finished successfully.[/]");
                            // Langkah 2: Coba Test Proxy & Save
                            AnsiConsole.MarkupLine("[magenta]Attempting automatic Proxy Test & Save...[/]");
                            bool testSuccess = await ProxyManager.RunProxyTestAndSaveAsync(cancellationToken);

                            if(testSuccess)
                            {
                                AnsiConsole.MarkupLine("[green]Proxy Test & Save finished. Reloading configurations...[/]");
                                // Langkah 3: Reload config biar pake proxy baru
                                TokenManager.ReloadAllConfigs();
                                // Ambil ulang token saat ini setelah reload (penting!)
                                currentToken = TokenManager.GetCurrentToken();
                                AnsiConsole.MarkupLine("[yellow]Retrying operation with the same token and refreshed proxies...[/]");
                                await Task.Delay(5000, cancellationToken); // Tunggu sebentar
                                // Langkah 4: JANGAN GANTI TOKEN, coba lagi dari awal loop
                                continue;
                            }
                            else
                            {
                                AnsiConsole.MarkupLine("[red]Automatic Proxy Test & Save failed.[/]");
                                AnsiConsole.MarkupLine("[yellow]Proceeding with token rotation as fallback...[/]");
                                // Biarkan rotasi token di bawah berjalan
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[red]Automatic IP Authorization failed.[/]");
                            AnsiConsole.MarkupLine("[yellow]Proceeding with token rotation as fallback...[/]");
                            // Biarkan rotasi token di bawah berjalan
                        }
                    }

                    // Jika BUKAN error proxy persisten (misal kuota habis),
                    // ATAU jika IP Auth/Test Proxy gagal, Lakukan rotasi token
                    AnsiConsole.MarkupLine("[yellow]⚠ Rotating to next token (due to low quota or unrecoverable proxy error)...[/]");
                    if (!string.IsNullOrEmpty(activeCodespace)) {
                         try { await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); }
                         catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn: Failed delete old codespace {activeCodespace}: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
                         currentState.ActiveCodespaceName = null;
                         TokenManager.SaveState(currentState);
                    }
                    currentToken = TokenManager.SwitchToNextToken(); // Ganti Token
                    activeCodespace = null;
                    consecutiveErrors = 0; // Reset error setelah rotasi
                    await Task.Delay(5000, cancellationToken);
                    continue; // Lanjut ke iterasi berikutnya dengan token baru
                }
                // === AKHIR PERBAIKAN LOGIKA ERROR BILLING ===


                // Jika billing OK, lanjut ke manage codespace
                AnsiConsole.MarkupLine("Ensuring healthy codespace...");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}");

                // ... (sisa loop tidak berubah: save state, sleep, keep-alive check, trigger, error handling) ...
                bool isNewOrRecreatedCodespace = currentState.ActiveCodespaceName != activeCodespace;
                currentState.ActiveCodespaceName = activeCodespace;
                TokenManager.SaveState(currentState);

                if (isNewOrRecreatedCodespace) {
                    AnsiConsole.MarkupLine($"[green]✓ New/Recreated codespace activated: {activeCodespace}[/]");
                    AnsiConsole.MarkupLine("[dim]Bots should be starting automatically via auto-start.sh[/]");
                } else {
                    AnsiConsole.MarkupLine($"[green]✓ Reusing existing codespace: {activeCodespace}[/]");
                }
                consecutiveErrors = 0;

                AnsiConsole.MarkupLine($"\n[yellow]⏱ Keep-Alive:[/] Sleeping for {KeepAliveInterval.TotalHours:F1} hours...");
                AnsiConsole.MarkupLine($"[dim]Next check at: {DateTime.Now.Add(KeepAliveInterval):yyyy-MM-dd HH:mm:ss}[/]");
                await Task.Delay(KeepAliveInterval, cancellationToken);

                currentState = TokenManager.GetState();
                activeCodespace = currentState.ActiveCodespaceName;
                if (string.IsNullOrEmpty(activeCodespace)) {
                    AnsiConsole.MarkupLine("[yellow]⚠ No active codespace found after sleep. Will check/recreate next cycle.[/]");
                    continue;
                }

                AnsiConsole.MarkupLine("\n[yellow]⏱ Keep-Alive Check:[/] Verifying codespace health...");
                if (!await CodespaceManager.CheckHealthWithRetry(currentToken, activeCodespace)) {
                    AnsiConsole.MarkupLine("[red]✗ Keep-Alive: Health check FAILED![/]");
                    AnsiConsole.MarkupLine("[yellow]Marking codespace for recreation...[/]");
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    continue;
                } else {
                    AnsiConsole.MarkupLine("[green]✓ Keep-Alive: Health check OK.[/]");
                    try {
                        AnsiConsole.MarkupLine("[dim]Triggering startup script (git pull & restart bots if needed)...[/]");
                        await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                        AnsiConsole.MarkupLine("[green]✓ Startup script triggered successfully[/]");
                    } catch (Exception ex) {
                        AnsiConsole.MarkupLine($"[yellow]⚠ Warning: Keep-alive startup trigger failed: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                        AnsiConsole.MarkupLine("[yellow]Assuming codespace issue, marking for recreation...[/]");
                        currentState.ActiveCodespaceName = null;
                        TokenManager.SaveState(currentState);
                        continue;
                    }
                }

            } // Akhir Try Utama
            catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("\n[yellow]⚠ Loop cancelled by user.[/]");
                break;
            }
            catch (Exception ex) { // Tangkap error lain (misal dari EnsureHealthyCodespace)
                consecutiveErrors++;
                AnsiConsole.MarkupLine("\n[red]✗ ERROR in orchestrator loop:[/]");
                AnsiConsole.WriteException(ex);
                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                    AnsiConsole.MarkupLine($"\n[red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} consecutive errors![/]");
                    AnsiConsole.MarkupLine("[yellow]Attempting full recovery (token rotation + codespace reset)...[/]");
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) {
                        try { await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); }
                        catch { /* Abaikan */ }
                    }
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    currentToken = TokenManager.SwitchToNextToken(); // Rotasi token sebagai fallback error
                    consecutiveErrors = 0;
                    AnsiConsole.MarkupLine("[cyan]Waiting 30 seconds before retry with new token...[/]");
                    await Task.Delay(30000, cancellationToken);
                } else {
                    AnsiConsole.MarkupLine($"[yellow]⚠ Retrying in {ErrorRetryDelay.TotalMinutes} minutes... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                    await Task.Delay(ErrorRetryDelay, cancellationToken);
                }
            }
        } // Akhir while loop

        AnsiConsole.MarkupLine("\n[cyan]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[cyan]   ORCHESTRATOR LOOP STOPPED[/]");
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
    }

    // Fungsi Pause tetap sama
    private static void Pause(string message, CancellationToken cancellationToken)
    {
       if (cancellationToken.IsCancellationRequested) return;
        Console.WriteLine();
        AnsiConsole.Markup($"[dim]{message}[/]");
        try {
            while (!Console.KeyAvailable) {
                if (cancellationToken.IsCancellationRequested) return;
                System.Threading.Thread.Sleep(100);
            }
            Console.ReadKey(true);
        } catch (InvalidOperationException) {
            AnsiConsole.MarkupLine("[yellow] (Auto-continuing after 2 seconds...)[/]");
            System.Threading.Thread.Sleep(2000);
        }
    }

} // Akhir class Program
