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

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            
            if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[yellow]Ctrl+C detected. Stopping interactive session...[/]");
                _interactiveCts.Cancel();
            }
            else if (!_mainCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[red]Ctrl+C detected. Shutting down...[/]");
                _mainCts.Cancel();
            } 
            else 
            { 
                AnsiConsole.MarkupLine("[yellow]Shutdown already in progress...[/]"); 
            }
        };

        try {
            TokenManager.Initialize();
            if (args.Length > 0 && args[0].ToLower() == "--run") {
                await RunOrchestratorLoopAsync(_mainCts.Token);
            } else {
                await RunInteractiveMenuAsync(_mainCts.Token);
            }
        }
        catch (OperationCanceledException) { 
            AnsiConsole.MarkupLine("\n[yellow]Operation cancelled by user.[/]"); 
        }
        catch (Exception ex) { 
            AnsiConsole.MarkupLine("\n[red]FATAL ERROR in Main:[/]"); 
            AnsiConsole.WriteException(ex);
        }
        finally { 
            AnsiConsole.MarkupLine("\n[dim]Orchestrator shutdown complete.[/]"); 
        }
    }

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
                    .PageSize(10)
                    .WrapAround(true)
                    .AddChoices(new[] {
                        "1. Start/Manage Codespace Runner (Continuous Loop)",
                        "2. Token & Collaborator Management",
                        "3. Proxy Management (Local TUI Proxy)",
                        "4. Attach to Bot Session (Remote)",
                        "5. Deploy Secrets (Manual Upload)",
                        "6. Delete ALL GitHub Secrets (Fix 200KB Error)",
                        "0. Exit"
                    }));

            var choice = selection[0].ToString();
            
            try {
                switch (choice) {
                    case "1": 
                        await RunOrchestratorLoopAsync(cancellationToken); 
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
                    case "5":
                        AnsiConsole.MarkupLine("[yellow]Manual secret upload removed. Use auto-cleanup instead.[/]");
                        Pause("Press Enter to continue...", cancellationToken);
                        break;
                    case "6": 
                        await SecretCleanup.DeleteAllSecrets();
                        Pause("Press Enter to continue...", cancellationToken); 
                        break;
                    case "0": 
                        return;
                }
            }
            catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); 
                Pause("Press Enter to continue...", CancellationToken.None); 
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); 
                AnsiConsole.WriteException(ex); 
                Pause("Press Enter to continue...", CancellationToken.None); 
            }
        }
    }

    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); 
             AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             
             var selection = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                     .Title("\n[bold white]TOKEN & COLLABORATOR SETUP[/]")
                     .PageSize(10)
                     .WrapAround(true)
                     .AddChoices(new[] {
                         "1. Validate Tokens & Get Usernames",
                         "2. Invite Collaborators",
                         "3. Accept Invitations",
                         "4. Show Token/Proxy Status",
                         "0. Back to Main Menu"
                     }));
             
             var sel = selection[0].ToString();
             if (sel == "0") return;

             switch (sel)
             {
                 case "1":
                     await CollaboratorManager.ValidateAllTokens(cancellationToken);
                     break;
                 case "2":
                     await CollaboratorManager.InviteCollaborators(cancellationToken);
                     break;
                 case "3":
                     await CollaboratorManager.AcceptInvitations(cancellationToken);
                     break;
                 case "4":
                     await Task.Run(() => TokenManager.ShowStatus(), cancellationToken);
                     break;
             }
             Pause("Press Enter to continue...", cancellationToken);
        }
     }

    private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); 
             AnsiConsole.Write(new FigletText("Proxy").Centered().Color(Color.Green));
             
             var selection = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                     .Title("\n[bold white]LOCAL PROXY MANAGEMENT[/]")
                     .PageSize(10)
                     .WrapAround(true)
                     .AddChoices(new[] {
                         "1. Run ProxySync (Update TUI's proxy list)",
                         "0. Back to Main Menu"
                     }));
             
             var sel = selection[0].ToString();
             if (sel == "0") return;
             
             if (sel == "1") await ProxyManager.DeployProxies(cancellationToken);
             
             Pause("Press Enter to continue...", cancellationToken);
        }
    }
    
private static async Task ShowAttachMenuAsync(CancellationToken mainCancellationToken)
{
    AnsiConsole.Clear();
    AnsiConsole.Write(new FigletText("Attach").Centered().Color(Color.Blue));
    
    var currentToken = TokenManager.GetCurrentToken();
    var state = TokenManager.GetState();
    var activeCodespace = state.ActiveCodespaceName;

    if (string.IsNullOrEmpty(activeCodespace))
    {
        AnsiConsole.MarkupLine("[red]Error: No active codespace found.[/]");
        AnsiConsole.MarkupLine("[yellow]Please run 'Start/Manage Codespace' (Menu 1) first.[/]");
        Pause("Press Enter to continue...", mainCancellationToken);
        return;
    }
    
    var sessions = await CodespaceManager.GetTmuxSessions(currentToken, activeCodespace);
    if (!sessions.Any())
    {
        AnsiConsole.MarkupLine("[yellow]No running bot sessions found in tmux.[/]");
        AnsiConsole.MarkupLine("[dim]Bots might still be starting up. Check 'auto-start.sh' log.[/]");
        Pause("Press Enter to continue...", mainCancellationToken);
        return;
    }

    var backOption = "[ (Back to Main Menu) ]";
    sessions.Add(backOption);

    var selectedBot = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title($"Select bot to attach (in [green]{activeCodespace}[/]):")
            .PageSize(15)
            .WrapAround(true)
            .AddChoices(sessions)
    );

    if (selectedBot == backOption) return;

    AnsiConsole.MarkupLine($"\n[cyan]Attaching to [yellow]{selectedBot}[/].[/]");
    AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B[/] then [bold]D[/] to detach from session)[/]");
    AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B[/] then [bold]N[/] (next) / [bold]P[/] (prev) to switch bot)[/]");
    AnsiConsole.MarkupLine("[red]⚠ Press Ctrl+C TWICE to force-quit if stuck.[/]");

    _interactiveCts = new CancellationTokenSource();
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, mainCancellationToken);

    try
    {
        string tmuxSessionName = "automation_hub_bots";
        string args = $"codespace ssh --codespace {activeCodespace} -- tmux attach-session -t {tmuxSessionName} -w \"{selectedBot}\"";
        
        await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCts.Token);
    }
    catch (OperationCanceledException)
    {
        if (_interactiveCts.IsCancellationRequested)
            AnsiConsole.MarkupLine("\n[yellow]✓ Detached from bot session.[/]");
        else
            AnsiConsole.MarkupLine("\n[yellow]Main app shutdown requested.[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"\n[red]Attach error: {ex.Message.EscapeMarkup()}[/]");
        Pause("Press Enter to continue...", CancellationToken.None);
    }
    finally
    {
        _interactiveCts?.Dispose();
        _interactiveCts = null;
    }
}

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
                    AnsiConsole.MarkupLine("[yellow]⚠ Quota insufficient. Rotating to next token...[/]");

                    if (!string.IsNullOrEmpty(activeCodespace))
                    {
                        // Coba hapus codespace lama sebelum ganti token
                        try
                        {
                            await CodespaceManager.DeleteCodespace(currentToken, activeCodespace);
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[yellow]Warn: Failed to delete old codespace {activeCodespace}: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                        }
                        currentState.ActiveCodespaceName = null;
                        TokenManager.SaveState(currentState);
                    }

                    currentToken = TokenManager.SwitchToNextToken(); // Rotasi token di sini
                    activeCodespace = null; // Reset active codespace setelah rotasi
                    consecutiveErrors = 0;
                    await Task.Delay(5000, cancellationToken);
                    continue; // Lanjut ke iterasi loop berikutnya dengan token baru
                }

                AnsiConsole.MarkupLine("Ensuring healthy codespace...");
                // === PERBAIKAN DI SINI ===
                // Tambahkan argumen kedua: $"{currentToken.Owner}/{currentToken.Repo}"
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}");
                // === AKHIR PERBAIKAN ===

                bool isNewOrRecreatedCodespace = currentState.ActiveCodespaceName != activeCodespace;

                currentState.ActiveCodespaceName = activeCodespace;
                TokenManager.SaveState(currentState);

                if (isNewOrRecreatedCodespace)
                {
                    AnsiConsole.MarkupLine($"[green]✓ New/Recreated codespace activated: {activeCodespace}[/]");
                    AnsiConsole.MarkupLine("[dim]Bots should be starting automatically via auto-start.sh[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[green]✓ Reusing existing codespace: {activeCodespace}[/]");
                }

                consecutiveErrors = 0; // Reset error count on success

                AnsiConsole.MarkupLine($"\n[yellow]⏱ Keep-Alive:[/] Sleeping for {KeepAliveInterval.TotalHours:F1} hours...");
                AnsiConsole.MarkupLine($"[dim]Next check at: {DateTime.Now.Add(KeepAliveInterval):yyyy-MM-dd HH:mm:ss}[/]");

                await Task.Delay(KeepAliveInterval, cancellationToken);

                // Re-fetch state and active codespace after sleep, maybe it was deleted manually?
                currentState = TokenManager.GetState();
                activeCodespace = currentState.ActiveCodespaceName;

                if (string.IsNullOrEmpty(activeCodespace))
                {
                    AnsiConsole.MarkupLine("[yellow]⚠ No active codespace found after sleep. Will check/recreate next cycle.[/]");
                    continue; // Skip keep-alive check if no codespace is tracked
                }

                AnsiConsole.MarkupLine("\n[yellow]⏱ Keep-Alive Check:[/] Verifying codespace health...");

                // Lakukan health check sederhana dulu sebelum trigger
                if (!await CodespaceManager.CheckHealthWithRetry(currentToken, activeCodespace))
                {
                    AnsiConsole.MarkupLine("[red]✗ Keep-Alive: Health check FAILED![/]");
                    AnsiConsole.MarkupLine("[yellow]Marking codespace for recreation...[/]");
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    // Langsung lanjut ke siklus berikutnya, akan recreate
                    continue;
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]✓ Keep-Alive: Health check OK.[/]");

                    try
                    {
                        AnsiConsole.MarkupLine("[dim]Triggering startup script (git pull & restart bots if needed)...[/]");
                        await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                        AnsiConsole.MarkupLine("[green]✓ Startup script triggered successfully[/]");
                    }
                    catch (Exception ex)
                    {
                        // Jika trigger gagal, mungkin codespace-nya bermasalah
                        AnsiConsole.MarkupLine($"[yellow]⚠ Warning: Keep-alive startup trigger failed: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                        AnsiConsole.MarkupLine("[yellow]Assuming codespace issue, marking for recreation...[/]");
                        currentState.ActiveCodespaceName = null;
                        TokenManager.SaveState(currentState);
                        // Lanjut ke siklus berikutnya, akan recreate
                        continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[yellow]⚠ Loop cancelled by user.[/]");
                break; // Keluar dari loop while
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                AnsiConsole.MarkupLine("\n[red]✗ ERROR in orchestrator loop:[/]");
                AnsiConsole.WriteException(ex); // Tampilkan detail error

                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                {
                    AnsiConsole.MarkupLine($"\n[red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} consecutive errors![/]");
                    AnsiConsole.MarkupLine("[yellow]Attempting full recovery (token rotation + codespace reset)...[/]");

                    // Coba hapus codespace yang bermasalah (jika ada)
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName))
                    {
                        try
                        {
                            await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName);
                        }
                        catch { /* Abaikan error delete saat recovery */ }
                    }

                    currentState.ActiveCodespaceName = null; // Reset nama codespace di state
                    TokenManager.SaveState(currentState);

                    // Rotasi ke token berikutnya
                    currentToken = TokenManager.SwitchToNextToken();
                    consecutiveErrors = 0; // Reset error count setelah rotasi

                    AnsiConsole.MarkupLine("[cyan]Waiting 30 seconds before retry with new token...[/]");
                    await Task.Delay(30000, cancellationToken);
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ Retrying in {ErrorRetryDelay.TotalMinutes} minutes... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                    await Task.Delay(ErrorRetryDelay, cancellationToken);
                }
            }
        } // Akhir loop while

        AnsiConsole.MarkupLine("\n[cyan]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[cyan]   ORCHESTRATOR LOOP STOPPED[/]");
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
    }

    private static void Pause(string message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        
        Console.WriteLine();
        Console.Write(message);
        
        try
        {
            while (!Console.KeyAvailable)
            {
                if (cancellationToken.IsCancellationRequested) return;
                System.Threading.Thread.Sleep(100);
            }
            Console.ReadKey(true);
        }
        catch (InvalidOperationException)
        {
            System.Threading.Thread.Sleep(2000);
        }
    }
}
