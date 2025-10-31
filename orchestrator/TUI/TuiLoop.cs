using Spectre.Console;
using System;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Services; // Menggunakan Services
using Orchestrator.Codespace; // Menggunakan Codespace

namespace Orchestrator.TUI // Namespace baru
{
    internal static class TuiLoop
    {
        // Flag ini khusus untuk loop orkestrasi
        private static bool _isAttemptingIpAuth = false;

        // Konstanta dipindah ke sini
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes((3 * 60) + 30); // 3.5 jam
        private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);
        private const int MAX_CONSECUTIVE_ERRORS = 3;

        // --- Loop Orkestrasi Utama ---
        internal static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) // Ini adalah _mainCts.Token
        {
            AnsiConsole.Clear(); AnsiConsole.MarkupLine("[cyan]Starting Orchestrator Loop...[/]"); AnsiConsole.MarkupLine("[dim](Press Ctrl+C ONCE for graceful shutdown)[/]");
            int consecutiveErrors = 0;

            while (!cancellationToken.IsCancellationRequested) { 
                TokenEntry currentToken = TokenManager.GetCurrentToken(); TokenState currentState = TokenManager.GetState(); string? activeCodespace = currentState.ActiveCodespaceName;
                var username = currentToken.Username ?? "unknown";
                AnsiConsole.MarkupLine($"\n[cyan]Token #{currentState.CurrentIndex + 1}: @{username.EscapeMarkup()}[/]");
                try {
                    cancellationToken.ThrowIfCancellationRequested();
                    AnsiConsole.MarkupLine("Checking billing...");
                    // Menggunakan BillingService (dulu BillingManager)
                    var billingInfo = await BillingService.GetBillingInfo(currentToken); 
                    cancellationToken.ThrowIfCancellationRequested();
                    BillingService.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");

                    if (!billingInfo.IsQuotaOk) {
                        // Menggunakan BillingService.PersistentProxyError
                        if (billingInfo.Error == BillingService.PersistentProxyError && !_isAttemptingIpAuth) {
                            AnsiConsole.MarkupLine("[magenta]Proxy error detected. Attempting recovery...[/]");
                            _isAttemptingIpAuth = true;
                            // Menggunakan ProxyService (dulu ProxyManager)
                            bool ipAuthSuccess = await ProxyService.RunIpAuthorizationOnlyAsync(cancellationToken); 
                            _isAttemptingIpAuth = false; cancellationToken.ThrowIfCancellationRequested();
                            if (ipAuthSuccess) {
                                AnsiConsole.MarkupLine("[green]IP Auth OK. Testing & Reloading...[/]");
                                await ProxyService.RunProxyTestAndSaveAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); 
                                TokenManager.ReloadProxyListAndReassign(); currentToken = TokenManager.GetCurrentToken();
                                AnsiConsole.MarkupLine("[yellow]Retrying billing check...[/]"); await Task.Delay(5000, cancellationToken); continue;
                            } else AnsiConsole.MarkupLine("[red]IP Auth failed.[/]");
                        } else if (_isAttemptingIpAuth) AnsiConsole.MarkupLine("[yellow]IP Auth in progress, skipping redundant attempt.[/]");

                        AnsiConsole.MarkupLine("[yellow]Quota low/billing failed/recovery failed. Rotating token...[/]");
                        if (!string.IsNullOrEmpty(activeCodespace)) { 
                            AnsiConsole.MarkupLine($"[dim]Deleting {activeCodespace.EscapeMarkup()}...[/]"); 
                            // Menggunakan CodeManager (dulu CodespaceManager)
                            try { await CodeManager.DeleteCodespace(currentToken, activeCodespace); } catch {} 
                        } 
                        currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                        currentToken = TokenManager.SwitchToNextToken(); activeCodespace = null; consecutiveErrors = 0;
                        AnsiConsole.MarkupLine($"[cyan]Switched to token: @{(currentToken.Username ?? "unknown").EscapeMarkup()}[/]");
                        await Task.Delay(5000, cancellationToken); continue; 
                    }

                    cancellationToken.ThrowIfCancellationRequested(); AnsiConsole.MarkupLine("Ensuring codespace...");
                    string ensuredCodespaceName;
                    try { 
                        // Menggunakan CodeManager (dulu CodespaceManager)
                        ensuredCodespaceName = await CodeManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}", cancellationToken); 
                    } 
                    catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Codespace ensure cancelled.[/]"); throw; } 
                    catch (Exception csEx) { AnsiConsole.MarkupLine($"\n[red]ERROR ENSURING CODESPACE[/]"); AnsiConsole.WriteException(csEx); consecutiveErrors++; AnsiConsole.MarkupLine($"\n[yellow]Errors: {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS}. Retrying after delay...[/]"); try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { throw; } continue; } 

                    cancellationToken.ThrowIfCancellationRequested();
                    bool isNewOrRecreated = currentState.ActiveCodespaceName != ensuredCodespaceName;
                    currentState.ActiveCodespaceName = ensuredCodespaceName; TokenManager.SaveState(currentState); activeCodespace = ensuredCodespaceName;
                    if (isNewOrRecreated) AnsiConsole.MarkupLine($"[green]✓ New/Recreated: {activeCodespace.EscapeMarkup()}[/]"); else AnsiConsole.MarkupLine($"[green]✓ Reusing: {activeCodespace.EscapeMarkup()}[/]");
                    consecutiveErrors = 0;

                    AnsiConsole.MarkupLine($"\n[yellow]Sleeping for {KeepAliveInterval.TotalHours:F1} hours...[/]");
                    try { await Task.Delay(KeepAliveInterval, cancellationToken); } catch (OperationCanceledException) { throw; }

                    cancellationToken.ThrowIfCancellationRequested(); currentState = TokenManager.GetState(); activeCodespace = currentState.ActiveCodespaceName;
                    if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[yellow]No active codespace after sleep. Restarting cycle.[/]"); continue; }
                    AnsiConsole.MarkupLine("\n[yellow]Performing Keep-Alive check...[/]");
                    // Menggunakan CodeManager (dulu CodespaceManager)
                    if (!await CodeManager.CheckHealthWithRetry(currentToken, activeCodespace, cancellationToken)) { 
                        AnsiConsole.MarkupLine("[red]Keep-alive FAILED! Resetting state...[/]"); currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); continue;
                    } else {
                        AnsiConsole.MarkupLine("[green]Health OK.[/]");
                        try { 
                            AnsiConsole.MarkupLine("[dim]Triggering keep-alive script...[/]"); 
                            // Menggunakan CodeManager (dulu CodespaceManager)
                            await CodeManager.TriggerStartupScript(currentToken, activeCodespace); 
                            AnsiConsole.MarkupLine("[green]Keep-alive triggered.[/]"); 
                        } 
                        catch (Exception trigEx) { AnsiConsole.MarkupLine($"[yellow]Keep-alive trigger failed: {trigEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}. Resetting state...[/]"); currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); continue; }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { AnsiConsole.MarkupLine("\n[yellow]Orchestrator loop cancellation requested.[/]"); break; } 
                catch (Exception ex) { 
                    consecutiveErrors++; AnsiConsole.MarkupLine("\n[bold red]UNEXPECTED LOOP ERROR[/]"); AnsiConsole.WriteException(ex);
                    if (cancellationToken.IsCancellationRequested) break; 
                    if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                        AnsiConsole.MarkupLine($"\n[bold red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} errors! Emergency recovery...[/]");
                        // Menggunakan CodeManager (dulu CodespaceManager)
                        if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) { try { await CodeManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); } catch {} } 
                        currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); currentToken = TokenManager.SwitchToNextToken(); consecutiveErrors = 0;
                        AnsiConsole.MarkupLine($"[cyan]Recovery: Switched token. Waiting 30s...[/]");
                        try { await Task.Delay(30000, cancellationToken); } catch (OperationCanceledException) { break; } 
                    } else {
                        AnsiConsole.MarkupLine($"[yellow]Retrying loop in {ErrorRetryDelay.TotalMinutes} min... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                        try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { break; } 
                    }
                }
            } // End while loop
            AnsiConsole.MarkupLine("\n[cyan]Orchestrator Loop Stopped.[/]");
        }
    }
}
