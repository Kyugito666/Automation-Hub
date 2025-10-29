using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Spectre.Console;
using System;
using Spectre.Console;
using System;
// ... (using lain) ...
using System.Threading; // Pastikan using Threading ada
using System.Threading.Tasks;

namespace Orchestrator;

internal static class Program
{
    private static CancellationTokenSource _mainCts = new CancellationTokenSource();
    private static CancellationTokenSource? _interactiveCts;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes((3 * 60) + 30);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task Main(string[] args)
    {
        // ... (Handler CancelKeyPress tetap sama) ...
        Console.CancelKeyPress += (sender, e) => { /* ... sama ... */ };
        try {
            TokenManager.Initialize();
            if (args.Length > 0 && args[0].ToLower() == "--run") { await RunOrchestratorLoopAsync(_mainCts.Token); }
            else { await RunInteractiveMenuAsync(_mainCts.Token); }
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine("\n[red]FATAL ERROR:[/]"); AnsiConsole.WriteException(ex); }
        finally { AnsiConsole.MarkupLine("\n[dim]Shutdown complete.[/]"); }
    }

    // Fungsi RunInteractiveMenuAsync tetap sama
     private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken) { /* ... sama ... */ }
    // Fungsi ShowSetupMenuAsync tetap sama
     private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) { /* ... sama ... */ }
    // Fungsi ShowLocalMenuAsync tetap sama
     private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) { /* ... sama ... */ }
    // Fungsi ShowAttachMenuAsync tetap sama
     private static async Task ShowAttachMenuAsync(CancellationToken mainCancellationToken) { /* ... sama ... */ }


    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) // <-- Terima CancellationToken
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[cyan]Loop Started[/]"); // Disingkat
        const int MAX_CONSECUTIVE_ERRORS = 3; int consecutiveErrors = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken();
            TokenState currentState = TokenManager.GetState();
            string? activeCodespace = currentState.ActiveCodespaceName;
            var username = currentToken.Username ?? "unknown";
            AnsiConsole.MarkupLine($"\n[cyan]Token #{currentState.CurrentIndex + 1}: @{username}[/]");

            try {
                cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
                AnsiConsole.MarkupLine("Checking billing...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");

                if (!billingInfo.IsQuotaOk) {
                    if (billingInfo.Error == BillingManager.PersistentProxyError) {
                        AnsiConsole.MarkupLine("[magenta]Proxy error. Attempting IP Auth...[/]");
                        bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
                        if (ipAuthSuccess) {
                            AnsiConsole.MarkupLine("[green]IP Auth OK. Testing proxies...[/]");
                            bool testSuccess = await ProxyManager.RunProxyTestAndSaveAsync(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
                            if(testSuccess) {
                                AnsiConsole.MarkupLine("[green]Test OK. Reloading proxies...[/]");
                                TokenManager.ReloadProxyListAndReassign(); // <-- Panggil fungsi baru
                                currentToken = TokenManager.GetCurrentToken();
                                AnsiConsole.MarkupLine("[yellow]Retrying same token...[/]");
                                await Task.Delay(5000, cancellationToken); continue;
                            } else { AnsiConsole.MarkupLine("[red]Test failed.[/]"); }
                        } else { AnsiConsole.MarkupLine("[red]IP Auth failed.[/]"); }
                    }
                    AnsiConsole.MarkupLine("[yellow]Rotating token...[/]");
                    if (!string.IsNullOrEmpty(activeCodespace)) { try { await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); } catch {} }
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                    currentToken = TokenManager.SwitchToNextToken(); activeCodespace = null; consecutiveErrors = 0;
                    await Task.Delay(5000, cancellationToken); continue;
                }

                // Billing OK
                cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel
                AnsiConsole.MarkupLine("Ensuring codespace...");
                // === PERBAIKAN: Kirim CancellationToken ===
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}", cancellationToken);
                // === AKHIR PERBAIKAN ===
                cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel

                // ... (sisa loop: save state, sleep, keep-alive - tetap sama, tapi kirim CancellationToken ke CheckHealthWithRetry) ...
                bool isNewOrRecreatedCodespace = currentState.ActiveCodespaceName != activeCodespace;
                currentState.ActiveCodespaceName = activeCodespace; TokenManager.SaveState(currentState);
                if (isNewOrRecreatedCodespace) { AnsiConsole.MarkupLine($"[green]✓ New/Recreated CS: {activeCodespace}[/]"); }
                else { AnsiConsole.MarkupLine($"[green]✓ Reusing CS: {activeCodespace}[/]"); }
                consecutiveErrors = 0;

                AnsiConsole.MarkupLine($"\n[yellow]Sleeping {KeepAliveInterval.TotalHours:F1}h...[/]");
                await Task.Delay(KeepAliveInterval, cancellationToken); // Delay + Cek Cancel
                cancellationToken.ThrowIfCancellationRequested(); // <<< Cek Cancel setelah bangun

                currentState = TokenManager.GetState(); activeCodespace = currentState.ActiveCodespaceName;
                if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[yellow]No CS after sleep.[/]"); continue; }

                AnsiConsole.MarkupLine("\n[yellow]Keep-Alive Check...[/]");
                // === PERBAIKAN: Kirim CancellationToken ===
                if (!await CodespaceManager.CheckHealthWithRetry(currentToken, activeCodespace, cancellationToken)) {
                // === AKHIR PERBAIKAN ===
                    AnsiConsole.MarkupLine("[red]Health FAILED![/]"); AnsiConsole.MarkupLine("[yellow]Marking for recreation...[/]");
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); continue;
                } else {
                    AnsiConsole.MarkupLine("[green]Health OK.[/]");
                    try {
                        AnsiConsole.MarkupLine("[dim]Triggering keep-alive script...[/]");
                        await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                        AnsiConsole.MarkupLine("[green]Triggered.[/]");
                    } catch (Exception ex) {
                        AnsiConsole.MarkupLine($"[yellow]Keep-alive trigger failed: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                        AnsiConsole.MarkupLine("[yellow]Marking for recreation...[/]");
                        currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); continue;
                    }
                }

            } // Akhir Try Utama
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Loop cancelled.[/]"); break; } // Keluar while
            catch (Exception ex) {
                consecutiveErrors++; AnsiConsole.MarkupLine("\n[red]Loop ERROR:[/]"); AnsiConsole.WriteException(ex);
                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                    AnsiConsole.MarkupLine($"\n[red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} errors![/]"); AnsiConsole.MarkupLine("[yellow]Recovery: Rotating token + reset...[/]");
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) { try { await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); } catch {} }
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                    currentToken = TokenManager.SwitchToNextToken(); consecutiveErrors = 0;
                    AnsiConsole.MarkupLine("[cyan]Waiting 30s...[/]");
                    try { await Task.Delay(30000, cancellationToken); } catch (OperationCanceledException) { break; } // Keluar while jika cancel
                } else {
                    AnsiConsole.MarkupLine($"[yellow]Retrying in {ErrorRetryDelay.TotalMinutes} min... (Err {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                    try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { break; } // Keluar while jika cancel
                }
            }
        } // End while

        AnsiConsole.MarkupLine("\n[cyan]Loop Stopped[/]"); // Disingkat
    }


    // Fungsi Pause tetap sama
     private static void Pause(string message, CancellationToken cancellationToken)
    {
       if (cancellationToken.IsCancellationRequested) return;
        Console.WriteLine(); AnsiConsole.Markup($"[dim]{message}[/]");
        try { while (!Console.KeyAvailable) { if (cancellationToken.IsCancellationRequested) return; Thread.Sleep(100); } Console.ReadKey(true); }
        catch (InvalidOperationException) { AnsiConsole.MarkupLine("[yellow](Auto-continue...)[/]"); Thread.Sleep(2000); }
    }

} // End class Program
