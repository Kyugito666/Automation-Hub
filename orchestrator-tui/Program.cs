using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
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
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested) {
                AnsiConsole.MarkupLine("\n[yellow]Ctrl+C: Stopping interactive...[/]");
                _interactiveCts.Cancel();
            } else if (!_mainCts.IsCancellationRequested) {
                AnsiConsole.MarkupLine("\n[red]Ctrl+C: Shutting down...[/]");
                _mainCts.Cancel();
            } else { AnsiConsole.MarkupLine("[yellow]Shutdown in progress...[/]"); }
        };
        try {
            TokenManager.Initialize();
            if (args.Length > 0 && args[0].ToLower() == "--run") { await RunOrchestratorLoopAsync(_mainCts.Token); }
            else { await RunInteractiveMenuAsync(_mainCts.Token); }
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Cancelled.[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine("\n[red]FATAL ERROR:[/]"); AnsiConsole.WriteException(ex); }
        finally { AnsiConsole.MarkupLine("\n[dim]Shutdown complete.[/]"); }
    }

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Local Control, Remote Execution[/]");

            // === BALIKIN NAMA MENU ===
            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold white]MAIN MENU[/]")
                    .PageSize(7)
                    .WrapAround(true)
                    .AddChoices(new[] {
                        "1. Start/Manage Codespace Runner (Continuous Loop)", // <-- Balikin
                        "2. Token & Collaborator Management",                 // <-- Balikin
                        "3. Proxy Management (Local TUI Proxy)",              // <-- Balikin
                        "4. Attach to Bot Session (Remote)",                  // <-- Balikin
                        "0. Exit"
                    }));
            // === AKHIR BALIKIN NAMA MENU ===

            var choice = selection[0].ToString();
            try {
                switch (choice) {
                    case "1": await RunOrchestratorLoopAsync(cancellationToken); if (cancellationToken.IsCancellationRequested) return; break;
                    case "2": await ShowSetupMenuAsync(cancellationToken); break;
                    case "3": await ShowLocalMenuAsync(cancellationToken); break;
                    case "4": await ShowAttachMenuAsync(cancellationToken); break;
                    case "0": AnsiConsole.MarkupLine("Exiting..."); _mainCts.Cancel(); return;
                }
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { AnsiConsole.MarkupLine("\n[yellow]Sub-menu cancelled.[/]"); Pause("Press Enter...", CancellationToken.None); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { AnsiConsole.MarkupLine("\n[yellow]Main cancelled.[/]"); return; }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); AnsiConsole.WriteException(ex); Pause("Press Enter...", CancellationToken.None); }
        }
    }

     private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Color(Color.Yellow));
             var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                     .Title("\n[bold white]TOKEN & COLLABORATOR[/]").PageSize(10).WrapAround(true)
                     .AddChoices(new[] { "1. Validate Tokens", "2. Invite Collaborators", "3. Accept Invitations", "4. Show Status", "0. Back" }));
             var sel = selection[0].ToString(); if (sel == "0") return;
             try {
                 switch (sel) {
                     case "1": await CollaboratorManager.ValidateAllTokens(cancellationToken); break;
                     case "2": await CollaboratorManager.InviteCollaborators(cancellationToken); break;
                     case "3": await CollaboratorManager.AcceptInvitations(cancellationToken); break;
                     case "4": await Task.Run(() => TokenManager.ShowStatus(), cancellationToken); break;
                 }
                 Pause("Press Enter...", cancellationToken);
             } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Cancelled.[/]"); return;
             } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        }
     }

     private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Proxy").Color(Color.Green));
             var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                     .Title("\n[bold white]LOCAL PROXY MGMT[/]").PageSize(10).WrapAround(true)
                     .AddChoices(new[] { "1. Run ProxySync (Interactive)", "0. Back" }));
             var sel = selection[0].ToString(); if (sel == "0") return;
             try {
                if (sel == "1") await ProxyManager.DeployProxies(cancellationToken);
                Pause("Press Enter...", cancellationToken);
             } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Cancelled.[/]"); return;
             } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        }
    }

     private static async Task ShowAttachMenuAsync(CancellationToken mainCancellationToken) {
        if (mainCancellationToken.IsCancellationRequested) return;
        AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Attach").Color(Color.Blue));
        var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;
        if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace.[/]"); Pause("Press Enter...", mainCancellationToken); return; }
        List<string> sessions;
        try { sessions = await CodespaceManager.GetTmuxSessions(currentToken, activeCodespace); }
        catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Cancelled.[/]"); return; }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetching sessions: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); return; }
        if (!sessions.Any()) { AnsiConsole.MarkupLine("[yellow]No running bot sessions found.[/]"); Pause("Press Enter...", mainCancellationToken); return; }
        var backOption = "[ (Back) ]"; sessions.Add(backOption);
        var selectedBot = AnsiConsole.Prompt( new SelectionPrompt<string>().Title($"Attach to (in [green]{activeCodespace}[/]):").PageSize(15).WrapAround(true).AddChoices(sessions) );
        if (selectedBot == backOption) return;
        AnsiConsole.MarkupLine($"\n[cyan]Attaching to [yellow]{selectedBot}[/].[/]"); AnsiConsole.MarkupLine("[dim](Ctrl+B, D to detach)[/]"); AnsiConsole.MarkupLine("[red]⚠ Ctrl+C TWICE to force quit.[/]");
        _interactiveCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, mainCancellationToken);
        try {
            string tmuxSessionName = "automation_hub_bots"; string escapedBotName = selectedBot.Replace("\"", "\\\"");
            string args = $"codespace ssh --codespace {activeCodespace} -- tmux attach-session -t {tmuxSessionName} \\; select-window -t \"{escapedBotName}\"";
            await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCts.Token);
        } catch (OperationCanceledException) {
            if (_interactiveCts?.IsCancellationRequested == true) AnsiConsole.MarkupLine("\n[yellow]✓ Detached.[/]");
            else if (mainCancellationToken.IsCancellationRequested) AnsiConsole.MarkupLine("\n[yellow]Main cancelled.[/]"); else AnsiConsole.MarkupLine("\n[yellow]Attach cancelled.[/]");
        } catch (Exception ex) { AnsiConsole.MarkupLine($"\n[red]Attach error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        finally { _interactiveCts?.Dispose(); _interactiveCts = null; }
    }


    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Clear(); AnsiConsole.MarkupLine("[cyan]Loop Started[/]");
        const int MAX_CONSECUTIVE_ERRORS = 3; int consecutiveErrors = 0;
        while (!cancellationToken.IsCancellationRequested) {
            TokenEntry currentToken = TokenManager.GetCurrentToken(); TokenState currentState = TokenManager.GetState(); string? activeCodespace = currentState.ActiveCodespaceName;
            var username = currentToken.Username ?? "unknown";
            AnsiConsole.MarkupLine($"\n[cyan]Token #{currentState.CurrentIndex + 1}: @{username}[/]");
            try {
                cancellationToken.ThrowIfCancellationRequested(); AnsiConsole.MarkupLine("Checking billing...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                cancellationToken.ThrowIfCancellationRequested(); BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");
                if (!billingInfo.IsQuotaOk) {
                    if (billingInfo.Error == BillingManager.PersistentProxyError) {
                        AnsiConsole.MarkupLine("[magenta]Proxy error. IP Auth...[/]");
                        bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (ipAuthSuccess) {
                            AnsiConsole.MarkupLine("[green]IP Auth OK. Testing proxies...[/]");
                            bool testSuccess = await ProxyManager.RunProxyTestAndSaveAsync(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if(testSuccess) {
                                AnsiConsole.MarkupLine("[green]Test OK. Reloading proxies...[/]");
                                TokenManager.ReloadProxyListAndReassign();
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
                cancellationToken.ThrowIfCancellationRequested(); AnsiConsole.MarkupLine("Ensuring codespace...");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}", cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                bool isNew = currentState.ActiveCodespaceName != activeCodespace; currentState.ActiveCodespaceName = activeCodespace; TokenManager.SaveState(currentState);
                if (isNew) { AnsiConsole.MarkupLine($"[green]✓ New/Recreated CS: {activeCodespace}[/]"); } else { AnsiConsole.MarkupLine($"[green]✓ Reusing CS: {activeCodespace}[/]"); }
                consecutiveErrors = 0;
                AnsiConsole.MarkupLine($"\n[yellow]Sleeping {KeepAliveInterval.TotalHours:F1}h...[/]");
                await Task.Delay(KeepAliveInterval, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                currentState = TokenManager.GetState(); activeCodespace = currentState.ActiveCodespaceName;
                if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[yellow]No CS after sleep.[/]"); continue; }
                AnsiConsole.MarkupLine("\n[yellow]Keep-Alive Check...[/]");
                if (!await CodespaceManager.CheckHealthWithRetry(currentToken, activeCodespace, cancellationToken)) {
                    AnsiConsole.MarkupLine("[red]Health FAILED![/]"); currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); continue;
                } else {
                    AnsiConsole.MarkupLine("[green]Health OK.[/]");
                    try { AnsiConsole.MarkupLine("[dim]Triggering keep-alive...[/]"); await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace); AnsiConsole.MarkupLine("[green]Triggered.[/]"); }
                    catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Keep-alive trigger failed: {ex.Message.Split('\n').FirstOrDefault()}[/]"); currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); continue; }
                }
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Loop cancelled.[/]"); break; }
            catch (Exception ex) {
                consecutiveErrors++; AnsiConsole.MarkupLine("\n[red]Loop ERROR:[/]"); AnsiConsole.WriteException(ex);
                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                    AnsiConsole.MarkupLine($"\n[red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} errors![/]"); AnsiConsole.MarkupLine("[yellow]Recovery: Rotating token + reset...[/]");
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) { try { await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); } catch {} }
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); currentToken = TokenManager.SwitchToNextToken(); consecutiveErrors = 0;
                    AnsiConsole.MarkupLine("[cyan]Waiting 30s...[/]");
                    try { await Task.Delay(30000, cancellationToken); } catch (OperationCanceledException) { break; }
                } else {
                    AnsiConsole.MarkupLine($"[yellow]Retrying in {ErrorRetryDelay.TotalMinutes} min... (Err {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                    try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { break; }
                }
            }
        }
        AnsiConsole.MarkupLine("\n[cyan]Loop Stopped[/]");
    }

     private static void Pause(string message, CancellationToken cancellationToken)
    {
       if (cancellationToken.IsCancellationRequested) return;
        Console.WriteLine(); AnsiConsole.Markup($"[dim]{message}[/]");
        try { while (!Console.KeyAvailable) { if (cancellationToken.IsCancellationRequested) return; Thread.Sleep(100); } Console.ReadKey(true); }
        catch (InvalidOperationException) { AnsiConsole.MarkupLine("[yellow](Auto-continue...)[/]"); Thread.Sleep(2000); }
    }

} // End class Program
