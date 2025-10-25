using Spectre.Console;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

internal static class Program
{
    private static CancellationTokenSource _mainCts = new CancellationTokenSource();
    
    // Interval keep-alive (cek SSH) saat Codespace berjalan
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromHours(3);
    // Delay antar iterasi loop utama jika terjadi error
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5); 

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // Mencegah app langsung terminate
            if (!_mainCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[bold yellow]Ctrl+C detected. Requesting shutdown...[/]");
                _mainCts.Cancel();
            } else {
                 AnsiConsole.MarkupLine("[grey](Shutdown already requested...)[/]");
            }
        };

        try
        {
            TokenManager.Initialize(); // Load token & proxy dulu

            if (args.Length > 0 && args[0].ToLower() == "--run")
            {
                // Mode non-interaktif, langsung jalankan loop orchestrator
                AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator in non-interactive mode...[/]");
                await RunOrchestratorLoopAsync(_mainCts.Token);
            }
            else
            {
                // Mode interaktif (menu)
                await RunInteractiveMenuAsync(_mainCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]Operation cancelled by user.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]FATAL ERROR in Main:[/]");
            AnsiConsole.WriteException(ex);
        }
        finally
        {
             AnsiConsole.MarkupLine("\n[cyan]Orchestrator shutting down.[/]");
        }
    }

    // ==========================================================
    // MENU INTERAKTIF BARU
    // ==========================================================

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Codespace Orchestrator - Local Control, Remote Execution[/]");
             
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MAIN MENU[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. [[REMOTE]] Start/Manage Codespace Runner (Continuous Loop)",
                        "2. [[SETUP]] Token & Collaborator Management",
                        "3. [[LOCAL]] Proxy Management (Run ProxySync)",
                        "4. [[DEBUG]] Test Local Bot (PTY)", // Membutuhkan PTY helper
                        "5. [[SYSTEM]] Refresh All Configs",
                        "0. Exit"
                    }));

            var selection = choice.Split('.')[0];
            
            try
            {
                switch (selection)
                {
                    case "1": 
                        await RunOrchestratorLoopAsync(cancellationToken); 
                        break; // Loop ini akan berjalan sampai di-cancel
                    case "2": 
                        await ShowSetupMenuAsync(cancellationToken); 
                        break;
                    case "3": 
                        await ShowLocalMenuAsync(cancellationToken); 
                        break;
                    case "4": 
                        await ShowDebugMenuAsync(cancellationToken); 
                        break;
                     case "5":
                        TokenManager.ReloadAllConfigs();
                        Pause("Konfigurasi di-refresh. Tekan Enter...", cancellationToken);
                        break;
                    case "0": 
                        return; // Keluar dari menu
                }
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("\n[yellow]Operation cancelled. Returning to main menu.[/]");
                 Pause("Tekan Enter...", CancellationToken.None); // Pause tanpa cancel
            }
            catch (Exception ex) {
                 AnsiConsole.MarkupLine($"[red]Unexpected Error in menu handler: {ex.Message}[/]");
                 AnsiConsole.WriteException(ex);
                 Pause("Error terjadi. Tekan Enter...", CancellationToken.None); // Pause tanpa cancel
            }
        }
    }
    
    // Sub-menu (mirip struktur lama tapi isinya beda)
    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested)
        {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             var choice = AnsiConsole.Prompt(
                  new SelectionPrompt<string>()
                    .Title("\n[bold yellow]TOKEN & COLLABORATOR SETUP[/]")
                    .PageSize(10).WrapAround()
                    .AddChoices(new[] {
                        "1. Validate Tokens & Get Usernames",
                        "2. Invite Collaborators",
                        "3. Accept Invitations",
                        "4. Show Token/Proxy Status",
                        "0. [[Back]]" }));
            var sel = choice.Split('.')[0]; if (sel == "0") return;

            switch (sel) {
                case "1": await CollaboratorManager.ValidateAllTokens(); break;
                case "2": await CollaboratorManager.InviteCollaborators(); break;
                case "3": await CollaboratorManager.AcceptInvitations(); break;
                case "4": TokenManager.ShowStatus(); break;
            }
            Pause("Tekan Enter...", cancellationToken);
        }
     }
    
    private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) { 
        while (!cancellationToken.IsCancellationRequested)
        {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Local").Centered().Color(Color.Green));
             var choice = AnsiConsole.Prompt(
                  new SelectionPrompt<string>()
                    .Title("\n[bold green]LOCAL PROXY MANAGEMENT[/]")
                    .PageSize(10).WrapAround()
                    .AddChoices(new[] {
                        "1. Run ProxySync (Download, Test, Generate proxy.txt)",
                        "0. [[Back]]" }));
            var sel = choice.Split('.')[0]; if (sel == "0") return;
            
            if (sel == "1") await ProxyManager.DeployProxies();
            Pause("Tekan Enter...", cancellationToken);
        }
    }

    private static async Task ShowDebugMenuAsync(CancellationToken cancellationToken) { 
         while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));
             var choice = AnsiConsole.Prompt(
                  new SelectionPrompt<string>()
                    .Title("\n[bold grey]DEBUG & LOCAL TESTING[/]")
                    .PageSize(10).WrapAround()
                    .AddChoices(new[] {
                        "1. Test Local Bot (Run Interactively)",
                        "0. [[Back]]" }));
             var sel = choice.Split('.')[0]; if (sel == "0") return;

            if (sel == "1") await TestLocalBotAsync(cancellationToken);
            // Tidak perlu pause di sini, TestLocalBotAsync sudah menangani
        }
    }
    
    // Placeholder untuk Debug Lokal (membutuhkan PTY dari ShellHelper lama)
    private static async Task TestLocalBotAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[yellow]Fitur Test Local Bot membutuhkan PTY helper (belum diimplementasikan ulang di ShellHelper baru).[/]");
        AnsiConsole.MarkupLine("[dim]Untuk saat ini, jalankan bot secara manual dari terminal.[/]");
        
        // TODO: Jika PTY di ShellHelper ditambahkan lagi, panggil di sini.
        // Contoh:
        // var bot = SelectBot(); // Fungsi helper untuk memilih bot dari config
        // if (bot != null) {
        //    var (exec, args) = BotRunnerLogic.GetRunCommand(bot.Path, bot.Type); // Perlu class baru
        //    await ShellHelper.RunInteractive(exec, args, bot.Path, null, cancellationToken);
        // }
        
        await Task.Delay(1000); // Dummy delay
        Pause("Tekan Enter...", cancellationToken);
    }

    // ==========================================================
    // LOOP ORCHESTRATOR UTAMA (NON-INTERAKTIF)
    // ==========================================================

    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator Loop...[/]");
        AnsiConsole.MarkupLine($"[dim]Keep-Alive check every {KeepAliveInterval.TotalMinutes} minutes.[/]");
        AnsiConsole.MarkupLine($"[dim]Error retry delay: {ErrorRetryDelay.TotalMinutes} minutes.[/]");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken();
            TokenState currentState = TokenManager.GetState();
            string? activeCodespace = currentState.ActiveCodespaceName;
            
            AnsiConsole.Write(new Rule($"[yellow]Processing Token #{currentState.CurrentIndex + 1} (@{currentToken.Username ?? "???"})[/]").LeftJustified());

            try
            {
                // 1. Cek Billing
                AnsiConsole.MarkupLine("Checking billing quota...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "???");

                if (!billingInfo.IsQuotaOk)
                {
                    AnsiConsole.MarkupLine("[red]Quota insufficient. Rotating token...[/]");
                    // Hapus codespace jika ada sebelum rotasi
                    if (!string.IsNullOrEmpty(activeCodespace))
                    {
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace);
                        currentState.ActiveCodespaceName = null;
                        TokenManager.SaveState(currentState);
                    }
                    TokenManager.SwitchToNextToken();
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); // Delay sedikit setelah rotasi
                    continue; // Lanjut ke iterasi berikutnya dengan token baru
                }

                // 2. Pastikan Codespace Sehat
                AnsiConsole.MarkupLine("Ensuring healthy codespace...");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
                
                // Simpan nama codespace yang aktif ke state
                if (currentState.ActiveCodespaceName != activeCodespace)
                {
                    currentState.ActiveCodespaceName = activeCodespace;
                    TokenManager.SaveState(currentState);
                    AnsiConsole.MarkupLine($"[green]✓ Active codespace set to: {activeCodespace}[/]");

                    // Jika codespace baru atau di-recreate, upload config & trigger startup
                    AnsiConsole.MarkupLine("New/Recreated codespace detected. Uploading configs and triggering startup...");
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace);
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                    AnsiConsole.MarkupLine("[green]✓ Initial startup complete. Entering Keep-Alive mode.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]✓ Codespace is healthy and running.[/]");
                }

                // 3. Masuk Mode Keep-Alive (delay lalu cek SSH)
                AnsiConsole.MarkupLine($"Sleeping for Keep-Alive interval ({KeepAliveInterval.TotalMinutes} minutes)...");
                await Task.Delay(KeepAliveInterval, cancellationToken);

                // Cek SSH lagi setelah delay
                AnsiConsole.MarkupLine("Keep-Alive: Checking SSH health...");
                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace))
                {
                    AnsiConsole.MarkupLine("[red]Keep-Alive: SSH check failed! Codespace might be stopped or unresponsive.[/]");
                    // Hapus state codespace aktif agar iterasi berikutnya recreate
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    AnsiConsole.MarkupLine("[yellow]Will attempt to recreate on next cycle.[/]");
                    // Jangan rotasi token, biarkan loop coba lagi dengan token sama
                } else {
                     AnsiConsole.MarkupLine("[green]Keep-Alive: SSH check OK.[/]");
                     // Opsional: Bisa trigger auto-start.sh lagi di sini jika perlu restart periodik
                     // AnsiConsole.MarkupLine("Keep-Alive: Re-triggering startup script...");
                     // await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                }

            }
            catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("\n[yellow]Orchestrator loop cancelled.[/]");
                break; // Keluar dari loop while
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[bold red]ERROR in orchestrator loop:[/]");
                AnsiConsole.WriteException(ex);
                
                // Jika error terkait GH command (misal timeout, auth), rotasi mungkin sudah terjadi di ShellHelper
                // Jika error lain, coba delay sebelum lanjut
                if (!ex.Message.Contains("Triggering token rotation"))
                {
                     AnsiConsole.MarkupLine($"[yellow]Retrying after {ErrorRetryDelay.TotalMinutes} minutes...[/]");
                     try {
                          await Task.Delay(ErrorRetryDelay, cancellationToken);
                     } catch (OperationCanceledException) { break; } // Batal saat delay
                } else {
                     // Jika rotasi sudah terjadi, delay sebentar saja
                     try {
                          await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                     } catch (OperationCanceledException) { break; } 
                }
            }
        }
    }

    // Helper Pause
    private static void Pause(string message, CancellationToken cancellationToken) { 
         AnsiConsole.MarkupLine($"\n[grey]{message} (Ctrl+C to cancel wait)[/]");
        try
        {
            while (!Console.KeyAvailable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task.Delay(50, cancellationToken).Wait(cancellationToken); // Tunggu input atau cancel
            }
            while(Console.KeyAvailable) Console.ReadKey(intercept: true); // Bersihkan buffer key
        }
        catch (OperationCanceledException)
        {
             AnsiConsole.MarkupLine("[yellow]Wait cancelled.[/]");
             throw; // Lemparkan lagi agar loop menu berhenti
        }
    }
}
