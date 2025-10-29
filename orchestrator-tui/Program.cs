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
    private static volatile bool _isShuttingDown = false;

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes((3 * 60) + 30);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task Main(string[] args)
    {
        bool forceQuitRequested = false;
        object shutdownLock = new object();

        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            lock (shutdownLock)
            {
                if (_isShuttingDown)
                {
                    if (forceQuitRequested) {
                        AnsiConsole.MarkupLine("\n[red]Force quit confirmed. Exiting immediately...[/]");
                        Environment.Exit(1);
                    }
                    AnsiConsole.MarkupLine("\n[red]Shutdown already in progress. Press Ctrl+C again to force quit.[/]");
                    forceQuitRequested = true;
                    Task.Delay(3000).ContinueWith(_ => forceQuitRequested = false);
                    return;
                }

                if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested) {
                    AnsiConsole.MarkupLine("\n[yellow]Ctrl+C: Stopping interactive operation...[/]");
                    _interactiveCts.Cancel();
                } else {
                    AnsiConsole.MarkupLine("\n[red]SHUTDOWN TRIGGERED! Attempting to stop remote codespace...[/]");
                    AnsiConsole.MarkupLine("[dim](This might take up to 2 minutes. Press Ctrl+C again to force quit.)[/]");
                    _isShuttingDown = true;
                    forceQuitRequested = true;
                    Task.Delay(3000).ContinueWith(_ => forceQuitRequested = false);
                    try {
                        PerformGracefulShutdownAsync().Wait();
                    } catch (Exception shutdownEx) {
                        AnsiConsole.MarkupLine($"[red]Error during graceful shutdown attempt: {shutdownEx.Message.Split('\n').FirstOrDefault()}[/]");
                    } finally {
                        if (!_mainCts.IsCancellationRequested) {
                             AnsiConsole.MarkupLine("[yellow]Signalling main loop to exit...[/]");
                            _mainCts.Cancel();
                        }
                    }
                }
            }
        };

        try {
            TokenManager.Initialize();
            if (args.Length > 0 && args[0].ToLower() == "--run") { await RunOrchestratorLoopAsync(_mainCts.Token); }
            else { await RunInteractiveMenuAsync(_mainCts.Token); }
        }
        catch (OperationCanceledException) when (_mainCts.IsCancellationRequested) { AnsiConsole.MarkupLine("\n[yellow]Main loop cancelled.[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine("\n[red]FATAL ERROR:[/]"); AnsiConsole.WriteException(ex); }
        finally { AnsiConsole.MarkupLine("\n[dim]Application shutdown complete.[/]"); }
    }

    private static async Task PerformGracefulShutdownAsync()
    {
        AnsiConsole.MarkupLine("[dim]Executing graceful shutdown steps...[/]");
        try {
            var token = TokenManager.GetCurrentToken();
            var state = TokenManager.GetState();
            var activeCodespace = state.ActiveCodespaceName;
            if (!string.IsNullOrEmpty(activeCodespace)) {
                AnsiConsole.MarkupLine($"[yellow]Sending STOP command to codespace '{activeCodespace}'...[/]");
                await CodespaceManager.StopCodespace(token, activeCodespace);
                AnsiConsole.MarkupLine($"[green]✓ Stop command attempt finished for '{activeCodespace}'.[/]");
            } else { AnsiConsole.MarkupLine("[yellow]No active codespace recorded in state file to stop.[/]"); }
        } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Exception during codespace stop attempt: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
        AnsiConsole.MarkupLine("[dim]Graceful shutdown steps finished.[/]");
    }

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Local Control, Remote Execution[/]");

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold white]MAIN MENU[/]")
                    // === PERBAIKAN: Tambah item menu, sesuaikan PageSize ===
                    .PageSize(9)
                    .WrapAround(true)
                    .AddChoices(new[] {
                        "1. Start/Manage Codespace Runner (Continuous Loop)",
                        "2. Token & Collaborator Management",
                        "3. Proxy Management (Local TUI Proxy)",
                        "4. Attach to Bot Session (Remote Tmux)", // Perjelas
                        "5. Migrasi Kredensial Lokal (Jalankan 1x)",
                        "6. Open Remote Shell (Codespace)", // <-- MENU BARU
                        "0. Exit"
                    }));
            // === AKHIR PERBAIKAN ===

            var choice = selection[0].ToString();
            try {
                switch (choice) {
                    case "1": await RunOrchestratorLoopAsync(cancellationToken); if (cancellationToken.IsCancellationRequested) return; break;
                    case "2": await ShowSetupMenuAsync(cancellationToken); break;
                    case "3": await ShowLocalMenuAsync(cancellationToken); break;
                    case "4": await ShowAttachMenuAsync(cancellationToken); break;
                    case "5": await CredentialMigrator.RunMigration(cancellationToken); Pause("Tekan Enter...", cancellationToken); break;
                    // === PERBAIKAN: Handle menu baru ===
                    case "6": await ShowRemoteShellAsync(cancellationToken); break;
                    // === AKHIR PERBAIKAN ===
                    case "0": AnsiConsole.MarkupLine("Exiting..."); if (!_isShuttingDown) _mainCts.Cancel(); return; // Jangan cancel jika sudah shutdown
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_mainCts.IsCancellationRequested) { AnsiConsole.MarkupLine("\n[yellow]Sub-menu operation cancelled.[/]"); Pause("Press Enter...", CancellationToken.None); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) { AnsiConsole.MarkupLine("\n[yellow]Main operation cancelled.[/]"); return; }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); AnsiConsole.WriteException(ex); Pause("Press Enter...", CancellationToken.None); }
        }
    }

     private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested && !_mainCts.IsCancellationRequested) {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Color(Color.Yellow));
             var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                     .Title("\n[bold white]TOKEN & COLLABORATOR[/]").PageSize(10).WrapAround(true)
                     .AddChoices(new[] { "1. Validate Tokens", "2. Invite Collaborators", "3. Accept Invitations", "4. Show Status", "0. Back" }));
             var sel = selection[0].ToString(); if (sel == "0") return;

             using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mainCts.Token);
             try {
                 switch (sel) {
                     case "1": await CollaboratorManager.ValidateAllTokens(linkedCts.Token); break;
                     case "2": await CollaboratorManager.InviteCollaborators(linkedCts.Token); break;
                     case "3": await CollaboratorManager.AcceptInvitations(linkedCts.Token); break;
                     case "4": await Task.Run(() => TokenManager.ShowStatus(), linkedCts.Token); break;
                 }
                 Pause("Press Enter...", linkedCts.Token);
             } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); if (_mainCts.IsCancellationRequested) return;
             } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        }
     }

     private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
         while (!cancellationToken.IsCancellationRequested && !_mainCts.IsCancellationRequested) {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Proxy").Color(Color.Green));
             var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                     .Title("\n[bold white]LOCAL PROXY MGMT[/]").PageSize(10).WrapAround(true)
                     .AddChoices(new[] { "1. Run ProxySync (Interactive)", "0. Back" }));
             var sel = selection[0].ToString(); if (sel == "0") return;

             using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mainCts.Token);
             try {
                if (sel == "1") await ProxyManager.DeployProxies(linkedCts.Token);
                Pause("Press Enter...", linkedCts.Token);
             } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); if (_mainCts.IsCancellationRequested) return;
             } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        }
    }

     private static async Task ShowAttachMenuAsync(CancellationToken mainCancellationToken) {
        if (mainCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
        AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Attach").Color(Color.Blue));
        var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;
        if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace recorded in state.[/]"); Pause("Press Enter...", mainCancellationToken); return; }

        AnsiConsole.MarkupLine($"[dim]Checking active codespace: [blue]{activeCodespace}[/][/]");
        List<string> sessions;
        try { sessions = await CodespaceManager.GetTmuxSessions(currentToken, activeCodespace); }
        catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Fetching sessions cancelled.[/]"); return; }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetching tmux sessions: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); return; }

        if (!sessions.Any()) { AnsiConsole.MarkupLine("[yellow]No running bot sessions found in tmux.[/]"); Pause("Press Enter...", mainCancellationToken); return; }
        var backOption = "[ << Back ]";
        sessions.Insert(0, backOption); // Tambahkan Back di awal

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Attach to bot session (in [green]{activeCodespace}[/]):")
                .PageSize(15)
                .WrapAround(true)
                .AddChoices(sessions)
        );

        if (selectedBot == backOption || mainCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;

        AnsiConsole.MarkupLine($"\n[cyan]Attempting to attach to tmux window [yellow]{selectedBot}[/].[/]");
        AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B[/] then [bold]D[/] to detach from tmux window)[/]");
        AnsiConsole.MarkupLine("[red](Use Ctrl+C carefully inside attach. TWICE might force quit TUI.)[/]");

        _interactiveCts = new CancellationTokenSource();
        using var linkedCtsAttach = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, mainCancellationToken, _mainCts.Token);
        try {
            string tmuxSessionName = "automation_hub_bots";
            // Escape nama window untuk command tmux
            string escapedBotName = selectedBot.Replace("\"", "\\\"");
            // Command: Masuk ke codespace -> attach ke sesi (-A: buat jika belum ada), pilih window (-t "nama")
            string args = $"codespace ssh --codespace \"{activeCodespace}\" -- tmux attach-session -t {tmuxSessionName} \\; select-window -t \"{escapedBotName}\"";

            await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCtsAttach.Token);
        } catch (OperationCanceledException) {
            if (_interactiveCts?.IsCancellationRequested == true) AnsiConsole.MarkupLine("\n[yellow]✓ Detached / Interactive operation cancelled.[/]");
            else if (mainCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) AnsiConsole.MarkupLine("\n[yellow]Main application cancelled during attach.[/]");
            else AnsiConsole.MarkupLine("\n[yellow]Attach operation cancelled unexpectedly.[/]");
        } catch (Exception ex) { AnsiConsole.MarkupLine($"\n[red]Attach error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        finally { _interactiveCts?.Dispose(); _interactiveCts = null; }
    }

    // === FUNGSI BARU: ShowRemoteShellAsync ===
    private static async Task ShowRemoteShellAsync(CancellationToken mainCancellationToken)
    {
        if (mainCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
        AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Shell").Color(Color.Magenta));
        var currentToken = TokenManager.GetCurrentToken();
        var state = TokenManager.GetState();
        var activeCodespace = state.ActiveCodespaceName;

        if (string.IsNullOrEmpty(activeCodespace))
        {
            AnsiConsole.MarkupLine("[red]No active codespace recorded in state.[/]");
            AnsiConsole.MarkupLine("[dim]Please start the codespace first (Menu 1).[/]");
            Pause("Press Enter...", mainCancellationToken);
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Attempting to open interactive shell in [green]{activeCodespace}[/]...[/]");
        AnsiConsole.MarkupLine("[dim](Type [bold]exit[/] or press [bold]Ctrl+D[/] to close the shell)[/]");
        AnsiConsole.MarkupLine("[red](Use Ctrl+C carefully inside shell. TWICE might force quit TUI.)[/]");

        _interactiveCts = new CancellationTokenSource();
        // Gabungkan token interaktif dengan token utama
        using var linkedCtsShell = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, mainCancellationToken, _mainCts.Token);
        try
        {
            // Command sederhana: masuk ke codespace via SSH tanpa argumen tambahan
            string args = $"codespace ssh --codespace \"{activeCodespace}\"";

            // Gunakan RunInteractiveWithFullInput agar Ctrl+C bisa ditangani TUI
            await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCtsShell.Token);

            // Jika keluar normal (exit/Ctrl+D), tidak akan ada exception
             AnsiConsole.MarkupLine("\n[yellow]✓ Remote shell closed.[/]");

        }
        catch (OperationCanceledException)
        {
            // Tangkap jika user Ctrl+C saat shell aktif
            if (_interactiveCts?.IsCancellationRequested == true) AnsiConsole.MarkupLine("\n[yellow]✓ Interactive shell cancelled by user.[/]");
            else if (mainCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) AnsiConsole.MarkupLine("\n[yellow]Main application cancelled during shell session.[/]");
            else AnsiConsole.MarkupLine("\n[yellow]Shell session cancelled unexpectedly.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]Remote shell error: {ex.Message.EscapeMarkup()}[/]");
            Pause("Press Enter...", CancellationToken.None); // Pause jika ada error
        }
        finally
        {
            _interactiveCts?.Dispose();
            _interactiveCts = null;
        }
    }
    // === AKHIR FUNGSI BARU ===


    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Clear(); AnsiConsole.MarkupLine("[cyan]Loop Started[/]");
        const int MAX_CONSECUTIVE_ERRORS = 3; int consecutiveErrors = 0;

        while (!cancellationToken.IsCancellationRequested) {
            TokenEntry currentToken = TokenManager.GetCurrentToken(); TokenState currentState = TokenManager.GetState(); string? activeCodespace = currentState.ActiveCodespaceName;
            var username = currentToken.Username ?? "unknown";
            AnsiConsole.MarkupLine($"\n[cyan]Token #{currentState.CurrentIndex + 1}: @{username}[/]");
            try {
                cancellationToken.ThrowIfCancellationRequested();
                AnsiConsole.MarkupLine("Checking billing...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);

                cancellationToken.ThrowIfCancellationRequested();
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");

                if (!billingInfo.IsQuotaOk) {
                    if (billingInfo.Error == BillingManager.PersistentProxyError) {
                        AnsiConsole.MarkupLine("[magenta]Proxy error detected during billing. Attempting IP Auth...[/]");
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
                                AnsiConsole.MarkupLine("[yellow]Retrying billing check with potentially new proxy...[/]");
                                await Task.Delay(5000, cancellationToken); continue;
                            } else { AnsiConsole.MarkupLine("[red]Proxy test failed after IP Auth.[/]"); }
                        } else { AnsiConsole.MarkupLine("[red]IP Auth failed.[/]"); }
                    }
                    AnsiConsole.MarkupLine("[yellow]Quota low or billing check failed. Rotating token...[/]");
                    if (!string.IsNullOrEmpty(activeCodespace)) {
                        AnsiConsole.MarkupLine($"[dim]Attempting to delete codespace {activeCodespace} before rotating...[/]");
                        try { await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); } catch {}
                    }
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                    currentToken = TokenManager.SwitchToNextToken(); activeCodespace = null; consecutiveErrors = 0;
                    AnsiConsole.MarkupLine($"[cyan]Switched to token: @{currentToken.Username ?? "unknown"}[/]");
                    await Task.Delay(5000, cancellationToken); continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                AnsiConsole.MarkupLine("Ensuring codespace is healthy...");

                try {
                    activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}", cancellationToken);
                } catch (OperationCanceledException) {
                    AnsiConsole.MarkupLine("\n[yellow]Codespace operation cancelled by user during ensure process.[/]");
                    throw;
                } catch (Exception csEx) {
                    AnsiConsole.MarkupLine($"\n[red]━━━ ERROR ENSURING CODESPACE ━━━[/]");
                    AnsiConsole.WriteException(csEx);
                    consecutiveErrors++;
                    AnsiConsole.MarkupLine($"\n[yellow]Consecutive errors: {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS}[/]");
                    if (!cancellationToken.IsCancellationRequested) {
                        AnsiConsole.MarkupLine("\n[yellow]Press Enter to retry or Ctrl+C to abort...[/]");
                        var readTask = Task.Run(() => Console.ReadLine());
                        var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
                        await Task.WhenAny(readTask, cancelTask);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool isNew = currentState.ActiveCodespaceName != activeCodespace;
                currentState.ActiveCodespaceName = activeCodespace;
                TokenManager.SaveState(currentState);
                if (isNew) { AnsiConsole.MarkupLine($"[green]✓ New/Recreated codespace active: {activeCodespace}[/]"); }
                else { AnsiConsole.MarkupLine($"[green]✓ Reusing existing codespace: {activeCodespace}[/]"); }

                consecutiveErrors = 0;

                AnsiConsole.MarkupLine($"\n[yellow]Sleeping for {KeepAliveInterval.TotalHours:F1} hours...[/]");
                await Task.Delay(KeepAliveInterval, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                currentState = TokenManager.GetState();
                activeCodespace = currentState.ActiveCodespaceName;
                if (string.IsNullOrEmpty(activeCodespace)) {
                    AnsiConsole.MarkupLine("[yellow]No active codespace recorded after sleep. Restarting check cycle.[/]");
                    continue;
                }

                AnsiConsole.MarkupLine("\n[yellow]Performing Keep-Alive check...[/]");
                if (!await CodespaceManager.CheckHealthWithRetry(currentToken, activeCodespace, cancellationToken)) {
                    AnsiConsole.MarkupLine("[red]Keep-alive health check FAILED! Codespace might be broken. Resetting...[/]");
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    continue;
                } else {
                    AnsiConsole.MarkupLine("[green]Health check OK.[/]");
                    try {
                        AnsiConsole.MarkupLine("[dim]Triggering keep-alive script...[/]");
                        await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                        AnsiConsole.MarkupLine("[green]Keep-alive triggered.[/]");
                    } catch (Exception trigEx) {
                        AnsiConsole.MarkupLine($"[yellow]Keep-alive trigger failed: {trigEx.Message.Split('\n').FirstOrDefault()}. Resetting...[/]");
                        currentState.ActiveCodespaceName = null;
                        TokenManager.SaveState(currentState);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { AnsiConsole.MarkupLine("\n[yellow]Orchestrator loop cancelled.[/]"); break; }
            catch (Exception ex) {
                consecutiveErrors++;
                AnsiConsole.MarkupLine("\n[red]━━━ UNEXPECTED LOOP ERROR ━━━[/]");
                AnsiConsole.WriteException(ex);

                if (cancellationToken.IsCancellationRequested) break;

                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                    AnsiConsole.MarkupLine($"\n[red]CRITICAL: Reached {MAX_CONSECUTIVE_ERRORS} consecutive errors![/]");
                    AnsiConsole.MarkupLine("[yellow]Performing emergency recovery: Rotating token + Force deleting codespace...[/]");
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) {
                        try { await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); } catch {}
                    }
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                    currentToken = TokenManager.SwitchToNextToken();
                    consecutiveErrors = 0;
                    AnsiConsole.MarkupLine($"[cyan]Recovery: Switched to token @{currentToken.Username ?? "unknown"}. Waiting 30s...[/]");
                    try { await Task.Delay(30000, cancellationToken); } catch (OperationCanceledException) { break; }
                } else {
                    AnsiConsole.MarkupLine($"[yellow]Retrying loop in {ErrorRetryDelay.TotalMinutes} minutes... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                    try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { break; }
                }
            }
        }
        AnsiConsole.MarkupLine("\n[cyan]Orchestrator Loop Stopped[/]");
    }

     private static void Pause(string message, CancellationToken cancellationToken)
    {
       if (cancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
        Console.WriteLine(); AnsiConsole.Markup($"[dim]{message}[/]");
        try {
            while (!Console.KeyAvailable) {
                if (cancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
                Thread.Sleep(100);
            }
            Console.ReadKey(true);
        }
        catch (InvalidOperationException) {
            AnsiConsole.MarkupLine("[yellow](Non-interactive console detected, auto-continuing...)[/]");
            try { Task.Delay(2000, cancellationToken).Wait(); } catch { }
        }
    }
}
