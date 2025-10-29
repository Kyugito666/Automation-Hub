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
    // _interactiveCts DIPERLUKAN untuk cancel Menu Attach
    private static CancellationTokenSource? _interactiveCts;

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes((3 * 60) + 30); // 3 jam 30 menit
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    // === KEMBALIKAN FUNGSI MAIN YANG HILANG ===
    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true; // Jangan biarkan Ctrl+C langsung matiin program

            // Jika sedang di menu attach (interactive), cancel itu dulu
            if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[yellow]Ctrl+C detected. Stopping interactive session (attach)...[/]");
                _interactiveCts.Cancel();
            }
            // Jika tidak sedang attach, cancel loop utama
            else if (!_mainCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[red]Ctrl+C detected. Shutting down main loop...[/]");
                _mainCts.Cancel();
            }
            else // Jika sudah proses shutdown
            {
                AnsiConsole.MarkupLine("[yellow]Shutdown already in progress...[/]");
            }
        };

        try
        {
            TokenManager.Initialize(); // Load token, proxy, state awal
            // Cek argumen command line
            if (args.Length > 0 && args[0].ToLower() == "--run")
            {
                // Mode non-interaktif (loop langsung)
                await RunOrchestratorLoopAsync(_mainCts.Token);
            }
            else
            {
                // Mode interaktif (tampilkan menu)
                await RunInteractiveMenuAsync(_mainCts.Token);
            }
        }
        catch (OperationCanceledException) when (_mainCts.IsCancellationRequested)
        {
            // Tangkap cancel dari loop utama/menu
            AnsiConsole.MarkupLine("\n[yellow]Operation cancelled by user (main loop).[/]");
        }
        catch (Exception ex)
        {
            // Tangkap error fatal lainnya
            AnsiConsole.MarkupLine("\n[red]FATAL ERROR in Main execution:[/]");
            AnsiConsole.WriteException(ex);
        }
        finally
        {
            AnsiConsole.MarkupLine("\n[dim]Orchestrator shutdown complete.[/]");
        }
    }
    // === AKHIR FUNGSI MAIN ===

    // Fungsi RunInteractiveMenuAsync tetap sama (dengan menu 5&6 dihapus)
    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Codespace Orchestrator - Local Control, Remote Execution[/]");

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold white]MAIN MENU[/]")
                    .PageSize(7)
                    .WrapAround(true)
                    .AddChoices(new[] {
                        "1. Start/Manage Codespace Runner (Continuous Loop)",
                        "2. Token & Collaborator Management",
                        "3. Proxy Management (Local TUI Proxy)",
                        "4. Attach to Bot Session (Remote)",
                        "0. Exit"
                    }));

            var choice = selection[0].ToString();

            try {
                switch (choice) {
                    case "1":
                        // Jalankan loop utama, akan blok sampai loop selesai atau di-cancel
                        await RunOrchestratorLoopAsync(cancellationToken);
                        // Jika loop selesai (misal karena cancel), keluar dari menu juga
                        if (cancellationToken.IsCancellationRequested) return;
                        break;
                    case "2":
                        await ShowSetupMenuAsync(cancellationToken);
                        break;
                    case "3":
                        await ShowLocalMenuAsync(cancellationToken);
                        break;
                    case "4":
                        await ShowAttachMenuAsync(cancellationToken);
                        break;
                    case "0":
                        AnsiConsole.MarkupLine("Exiting...");
                        _mainCts.Cancel(); // Pastikan loop utama berhenti jika keluar dari menu
                        return; // Keluar dari fungsi menu
                }
            }
            // Tangkap cancel dari submenu (misal Ctrl+C pas setup)
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                AnsiConsole.MarkupLine("\n[yellow]Sub-menu operation cancelled.[/]");
                Pause("Press Enter to return to main menu...", CancellationToken.None);
            }
            // Tangkap cancel dari loop utama (jika ditekan pas di menu)
             catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                 AnsiConsole.MarkupLine("\n[yellow]Main loop cancellation requested.[/]");
                 return; // Keluar dari menu
             }
            catch (Exception ex) { // Error lain di menu
                AnsiConsole.MarkupLine($"[red]Error in Menu: {ex.Message.EscapeMarkup()}[/]");
                AnsiConsole.WriteException(ex);
                Pause("Press Enter to continue...", CancellationToken.None);
            }
        }
    }


    // Fungsi ShowSetupMenuAsync tetap sama
     private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear();
             AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             var selection = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                     .Title("\n[bold white]TOKEN & COLLABORATOR SETUP[/]")
                     .PageSize(10).WrapAround(true)
                     .AddChoices(new[] {
                         "1. Validate Tokens & Get Usernames",
                         "2. Invite Collaborators",
                         "3. Accept Invitations",
                         "4. Show Token/Proxy Status",
                         "0. Back to Main Menu"
                     }));
             var sel = selection[0].ToString();
             if (sel == "0") return;
             try {
                 switch (sel) {
                     case "1": await CollaboratorManager.ValidateAllTokens(cancellationToken); break;
                     case "2": await CollaboratorManager.InviteCollaborators(cancellationToken); break;
                     case "3": await CollaboratorManager.AcceptInvitations(cancellationToken); break;
                     case "4": await Task.Run(() => TokenManager.ShowStatus(), cancellationToken); break;
                 }
                 Pause("Press Enter to continue...", cancellationToken);
             } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); return; // Kembali ke menu utama jika cancel
             } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); AnsiConsole.WriteException(ex); Pause("Press Enter...", CancellationToken.None); }
        }
     }

    // Fungsi ShowLocalMenuAsync tetap sama
     private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear();
             AnsiConsole.Write(new FigletText("Proxy").Centered().Color(Color.Green));
             var selection = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                     .Title("\n[bold white]LOCAL PROXY MANAGEMENT[/]")
                     .PageSize(10).WrapAround(true)
                     .AddChoices(new[] { "1. Run ProxySync (Update TUI's proxy list)", "0. Back to Main Menu" }));
             var sel = selection[0].ToString();
             if (sel == "0") return;
             try {
                if (sel == "1") await ProxyManager.DeployProxies(cancellationToken);
                Pause("Press Enter to continue...", cancellationToken);
             } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); return;
             } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); AnsiConsole.WriteException(ex); Pause("Press Enter...", CancellationToken.None); }
        }
    }

    // Fungsi ShowAttachMenuAsync tetap sama
    private static async Task ShowAttachMenuAsync(CancellationToken mainCancellationToken)
    {
        // Pastikan kita tidak menjalankan ini jika main loop di-cancel
        if (mainCancellationToken.IsCancellationRequested) return;

        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("Attach").Centered().Color(Color.Blue));
        var currentToken = TokenManager.GetCurrentToken();
        var state = TokenManager.GetState();
        var activeCodespace = state.ActiveCodespaceName;

        if (string.IsNullOrEmpty(activeCodespace)) {
            AnsiConsole.MarkupLine("[red]Error: No active codespace found.[/]");
            AnsiConsole.MarkupLine("[yellow]Please run 'Start/Manage Codespace' (Menu 1) first.[/]");
            Pause("Press Enter to continue...", mainCancellationToken); // Pakai main token di sini
            return;
        }

        List<string> sessions;
        try { sessions = await CodespaceManager.GetTmuxSessions(currentToken, activeCodespace); }
        catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Cancelled fetching sessions.[/]"); return; }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetching sessions: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); return; }

        if (!sessions.Any()) {
            AnsiConsole.MarkupLine("[yellow]No running bot sessions found in tmux.[/]");
            AnsiConsole.MarkupLine("[dim]Bots might still be starting up. Check remote logs if needed.[/]");
            Pause("Press Enter to continue...", mainCancellationToken);
            return;
        }

        var backOption = "[ (Back to Main Menu) ]";
        sessions.Add(backOption);
        var selectedBot = AnsiConsole.Prompt( new SelectionPrompt<string>()
                .Title($"Select bot to attach (in [green]{activeCodespace}[/]):")
                .PageSize(15).WrapAround(true).AddChoices(sessions) );
        if (selectedBot == backOption) return;

        AnsiConsole.MarkupLine($"\n[cyan]Attaching to [yellow]{selectedBot}[/].[/]");
        AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B[/] then [bold]D[/] to detach)[/]");
        AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B[/] then [bold]N/P[/] to switch window)[/]");
        AnsiConsole.MarkupLine("[red]⚠ Press Ctrl+C TWICE to force-quit if stuck.[/]");

        // Buat CancellationTokenSource BARU khusus untuk sesi attach ini
        _interactiveCts = new CancellationTokenSource();
        // Gabungkan dengan main CancellationToken
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, mainCancellationToken);

        try {
            string tmuxSessionName = "automation_hub_bots";
            string escapedBotName = selectedBot.Replace("\"", "\\\"");
            string args = $"codespace ssh --codespace {activeCodespace} -- tmux attach-session -t {tmuxSessionName} \\; select-window -t \"{escapedBotName}\"";
            // Jalankan attach, gunakan linked CancellationToken
            await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCts.Token);
        }
        catch (OperationCanceledException) {
            // Cek apakah cancel dari _interactiveCts (Ctrl+C di attach) atau mainCancellationToken
            if (_interactiveCts?.IsCancellationRequested == true) AnsiConsole.MarkupLine("\n[yellow]✓ Detached from bot session.[/]");
            else if (mainCancellationToken.IsCancellationRequested) AnsiConsole.MarkupLine("\n[yellow]Main cancellation requested during attach.[/]");
            else AnsiConsole.MarkupLine("\n[yellow]Attach cancelled.[/]");
            // Tidak perlu throw lagi di sini, biarkan kembali ke menu
        }
        catch (Exception ex) {
            AnsiConsole.MarkupLine($"\n[red]Attach error: {ex.Message.EscapeMarkup()}[/]");
            Pause("Press Enter to continue...", CancellationToken.None); // Jangan pakai main token di sini
        }
        finally {
            _interactiveCts?.Dispose(); // Dispose CancellationTokenSource attach
            _interactiveCts = null; // Set null lagi
        }
    }


    // Fungsi RunOrchestratorLoopAsync tetap sama (versi terakhir dengan fix error proxy)
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

                if (!billingInfo.IsQuotaOk)
                {
                    if (billingInfo.Error == BillingManager.PersistentProxyError)
                    {
                        AnsiConsole.MarkupLine("[magenta]Persistent proxy error detected during billing check.[/]");
                        AnsiConsole.MarkupLine("[magenta]Attempting automatic IP Authorization...[/]");
                        bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(cancellationToken);
                        if (ipAuthSuccess) {
                            AnsiConsole.MarkupLine("[green]IP Authorization finished successfully.[/]");
                            AnsiConsole.MarkupLine("[magenta]Attempting automatic Proxy Test & Save...[/]");
                            bool testSuccess = await ProxyManager.RunProxyTestAndSaveAsync(cancellationToken);
                            if(testSuccess) {
                                AnsiConsole.MarkupLine("[green]Proxy Test & Save finished. Reloading configurations...[/]");
                                TokenManager.ReloadAllConfigs();
                                currentToken = TokenManager.GetCurrentToken(); // Re-get token
                                AnsiConsole.MarkupLine("[yellow]Retrying operation with the same token and refreshed proxies...[/]");
                                await Task.Delay(5000, cancellationToken);
                                continue; // Coba lagi billing check
                            } else {
                                AnsiConsole.MarkupLine("[red]Automatic Proxy Test & Save failed.[/]");
                                AnsiConsole.MarkupLine("[yellow]Proceeding with token rotation as fallback...[/]");
                            }
                        } else {
                            AnsiConsole.MarkupLine("[red]Automatic IP Authorization failed.[/]");
                            AnsiConsole.MarkupLine("[yellow]Proceeding with token rotation as fallback...[/]");
                        }
                    }

                    // Jika bukan error proxy ATAU jika IP Auth/Test gagal, rotasi token
                    AnsiConsole.MarkupLine("[yellow]⚠ Rotating to next token (low quota or unrecoverable error)...[/]");
                    if (!string.IsNullOrEmpty(activeCodespace)) {
                         try { await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); }
                         catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]Warn: Failed delete old codespace {activeCodespace}: {ex.Message.Split('\n').FirstOrDefault()}[/]"); }
                         currentState.ActiveCodespaceName = null;
                         TokenManager.SaveState(currentState);
                    }
                    currentToken = TokenManager.SwitchToNextToken();
                    activeCodespace = null;
                    consecutiveErrors = 0;
                    await Task.Delay(5000, cancellationToken);
                    continue;
                }

                AnsiConsole.MarkupLine("Ensuring healthy codespace...");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}");

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
                        AnsiConsole.MarkupLine($"[yellow]⚠ Warning: Keep-alive trigger failed: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                        AnsiConsole.MarkupLine("[yellow]Assuming codespace issue, marking for recreation...[/]");
                        currentState.ActiveCodespaceName = null;
                        TokenManager.SaveState(currentState);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("\n[yellow]⚠ Loop cancelled by user.[/]");
                break;
            }
            catch (Exception ex) {
                consecutiveErrors++;
                AnsiConsole.MarkupLine("\n[red]✗ ERROR in orchestrator loop:[/]");
                AnsiConsole.WriteException(ex);
                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                    AnsiConsole.MarkupLine($"\n[red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} errors![/]");
                    AnsiConsole.MarkupLine("[yellow]Attempting recovery (token rotation + reset)...[/]");
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) {
                        try { await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); } catch {}
                    }
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    currentToken = TokenManager.SwitchToNextToken();
                    consecutiveErrors = 0;
                    AnsiConsole.MarkupLine("[cyan]Waiting 30s before retry with new token...[/]");
                    await Task.Delay(30000, cancellationToken);
                } else {
                    AnsiConsole.MarkupLine($"[yellow]⚠ Retrying in {ErrorRetryDelay.TotalMinutes} min... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                    await Task.Delay(ErrorRetryDelay, cancellationToken);
                }
            }
        } // End while

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

} // End class Program
