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
    
    // CTS khusus untuk bot/attach session
    private static CancellationTokenSource? _interactiveCts;

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            
            // Cek apakah kita sedang menjalankan sesi interaktif (attach/bot)
            if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[yellow]Ctrl+C detected. Requesting interactive session shutdown...[/]");
                _interactiveCts.Cancel();
            }
            else if (!_mainCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[red]Ctrl+C detected. Requesting main shutdown...[/]");
                _mainCts.Cancel();
            } 
            else 
            { 
                AnsiConsole.MarkupLine("[yellow]Shutdown already requested...[/]"); 
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
            AnsiConsole.MarkupLine("\n[dim]Orchestrator shutting down.[/]"); 
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
                    .WrapAround(true) // <- Wrap-around di Menu Utama
                    .AddChoices(new[] {
                        "1. Start/Manage Codespace Runner (Continuous Loop)",
                        "2. Token & Collaborator Management",
                        "3. Proxy Management (Local TUI Proxy)",
                        "4. Attach to Bot Session (Remote)", // <- Menu Baru
                        "5. Refresh All Configs",
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
                        await ShowAttachMenuAsync(cancellationToken); // <- Menu Baru
                        break;
                    case "5": 
                        TokenManager.ReloadAllConfigs(); 
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
                     .WrapAround(true) // <- Wrap-around
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
                     .WrapAround(true) // <- Wrap-around
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
    
    // === MENU BARU: ATTACH TO BOT ===
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
                .WrapAround(true) // <- Wrap-around
                .AddChoices(sessions)
        );

        if (selectedBot == backOption) return;

        AnsiConsole.MarkupLine($"\n[cyan]Attaching to [yellow]{selectedBot}[/].[/]");
        AnsiConsole.MarkupLine("[dim]   (Gunakan [bold]Ctrl+B[/] lalu [bold]D[/] untuk detach dari session)[/]");
        AnsiConsole.MarkupLine("[dim]   (Gunakan [bold]Ctrl+B[/] lalu [bold]N[/] (next) / [bold]P[/] (prev) untuk ganti bot)[/]");
        AnsiConsole.MarkupLine("[yellow]   Press Ctrl+C to force-quit this attach session.[/]");

        _interactiveCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, mainCancellationToken);

        try
        {
            string tmuxSessionName = "automation_hub_bots"; // Sesuai 'deploy_bots.py'
            string args = $"codespace ssh -c {activeCodespace} -- tmux attach-session -t {tmuxSessionName} -w \"{selectedBot}\"";
            
            // Pisahkan 'gh' dari sisa argumennya
            await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (_interactiveCts.IsCancellationRequested)
                AnsiConsole.MarkupLine("\n[yellow]Attach session stopped by user (Ctrl+C).[/]");
            else
                AnsiConsole.MarkupLine("\n[yellow]Main app shutdown requested. Stopping attach...[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]Attach session crashed: {ex.Message.EscapeMarkup()}[/]");
            Pause("Press Enter to continue...", CancellationToken.None);
        }
        finally
        {
            _interactiveCts.Dispose();
            _interactiveCts = null;
        }
    }
    // === AKHIR MENU BARU ===


    // --- Helper GetProjectRoot (dihapus, pindah ke BotConfig.cs) ---
    // --- Helper GetLocalBotPathForTest (dihapus, pindah ke BotConfig.cs) ---
    // --- Helper GetRunCommandLocal (dihapus, 'Test Local Bot' dihapus) ---
    // --- Helper TestLocalBotAsync (dihapus, 'Test Local Bot' dihapus) ---
    
    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) 
    {
        Console.WriteLine("Starting Orchestrator Loop...");
        
        const int MAX_CONSECUTIVE_ERRORS = 3;
        int consecutiveErrors = 0;
        
        while (!cancellationToken.IsCancellationRequested) 
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken(); 
            TokenState currentState = TokenManager.GetState(); 
            string? activeCodespace = currentState.ActiveCodespaceName;
            
            var username = currentToken.Username ?? "unknown";
            Console.WriteLine($"\n========== Token #{currentState.CurrentIndex + 1} (@{username}) ==========");
            
            try 
            { 
                Console.WriteLine("Checking billing..."); 
                var billingInfo = await BillingManager.GetBillingInfo(currentToken); 
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");
                
                if (!billingInfo.IsQuotaOk) 
                { 
                    Console.WriteLine("Quota insufficient. Rotating..."); 
                    
                    if (!string.IsNullOrEmpty(activeCodespace)) 
                    { 
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); 
                        currentState.ActiveCodespaceName = null; 
                        TokenManager.SaveState(currentState); 
                    }
                    
                    currentToken = TokenManager.SwitchToNextToken(); 
                    await Task.Delay(5000, cancellationToken); 
                    consecutiveErrors = 0;
                    continue; 
                }
                
                Console.WriteLine("Ensuring codespace...");
                
                // === LOGIKA BARU: 'EnsureHealthyCodespace' sekarang pinter ===
                // Dia akan 'start' jika 'Stopped', 'create' jika 'null'
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
                
                // Cek apakah TUI perlu meng-upload config (HANYA jika nama CS berubah)
                bool isNewOrRecreatedCodespace = currentState.ActiveCodespaceName != activeCodespace;
                
                if (isNewOrRecreatedCodespace) 
                { 
                    currentState.ActiveCodespaceName = activeCodespace; 
                    TokenManager.SaveState(currentState); 
                    
                    Console.WriteLine($"Active CS: {activeCodespace}");
                    Console.WriteLine("New/Recreated CS detected. Uploading core configs..."); 
                    
                    // Upload file config (bots_config.json, dll)
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace);
                    
                    // Trigger auto-start
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                    Console.WriteLine("Initial startup complete."); 
                } 
                else 
                { 
                    Console.WriteLine("Codespace healthy (reusing existing 'Available' or 'Stopped')."); 
                }
                
                consecutiveErrors = 0;
                
                Console.WriteLine($"Sleeping for Keep-Alive ({KeepAliveInterval.TotalMinutes} min)..."); 
                await Task.Delay(KeepAliveInterval, cancellationToken);
                
                currentState = TokenManager.GetState(); 
                activeCodespace = currentState.ActiveCodespaceName; 
                
                if (string.IsNullOrEmpty(activeCodespace)) 
                { 
                    Console.WriteLine("No active codespace in state. Will recreate next cycle."); 
                    continue; 
                }
                
                Console.WriteLine("Keep-Alive: Checking SSH..."); 
                
                if (!await CodespaceManager.CheckSshHealthWithRetry(currentToken, activeCodespace)) 
                { 
                    Console.WriteLine("Keep-Alive: SSH check FAILED!"); 
                    currentState.ActiveCodespaceName = null; 
                    TokenManager.SaveState(currentState); 
                    Console.WriteLine("Will recreate next cycle."); 
                } 
                else 
                { 
                    Console.WriteLine("Keep-Alive: SSH check OK.");
                    
                    try
                    {
                        Console.WriteLine("Keep-Alive: Re-triggering startup script (git pull & restart bots)...");
                        await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Keep-alive startup trigger failed: {ex.Message}");
                    }
                }
            } 
            catch (OperationCanceledException) 
            { 
                Console.WriteLine("Loop cancelled by user."); 
                break; 
            } 
            catch (Exception ex) 
            { 
                consecutiveErrors++;
                Console.WriteLine("ERROR loop:"); 
                Console.WriteLine(ex.ToString());
                
                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                {
                    AnsiConsole.MarkupLine($"[red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} consecutive errors detected![/]");
                    AnsiConsole.MarkupLine("[yellow]Attempting token rotation and full reset...[/]");
                    
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName))
                    {
                        try
                        {
                            await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName);
                        }
                        catch { }
                    }
                    
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    
                    currentToken = TokenManager.SwitchToNextToken();
                    consecutiveErrors = 0;
                    
                    AnsiConsole.MarkupLine("[cyan]Waiting 30 seconds before retry with new token...[/]");
                    await Task.Delay(30000, cancellationToken);
                }
                else
                {
                    Console.WriteLine($"Retrying in {ErrorRetryDelay.TotalMinutes} minutes... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})");
                    await Task.Delay(ErrorRetryDelay, cancellationToken);
                }
            }
        }
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
