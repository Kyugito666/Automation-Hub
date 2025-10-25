using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices; // Pastikan ini ada
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

internal static class Program
{
    private static CancellationTokenSource _mainCts = new CancellationTokenSource();

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task Main(string[] args)
    {
        // ... (Handler Ctrl+C tetap sama) ...
        Console.CancelKeyPress += (sender, e) => { /* ... */ };

        try
        {
            TokenManager.Initialize();
            if (args.Length > 0 && args[0].ToLower() == "--run")
            {
                await RunOrchestratorLoopAsync(_mainCts.Token);
            }
            else
            {
                await RunInteractiveMenuAsync(_mainCts.Token);
            }
        }
        catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled by user.[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"\n[bold red]FATAL ERROR in Main:[/]"); AnsiConsole.WriteException(ex); }
        finally { AnsiConsole.MarkupLine("\n[cyan]Orchestrator shutting down.[/]"); }
    }

    // ... (RunInteractiveMenuAsync, ShowSetupMenuAsync, ShowLocalMenuAsync, ShowDebugMenuAsync, TestLocalBotAsync, GetRunCommandLocal, GetProjectRoot tetap sama) ...
     private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        // ... (Kode menu utama tetap sama) ...
         while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Codespace Orchestrator - Local Control, Remote Execution[/]");

            var choice = AnsiConsole.Prompt(/* ... */);
            var selection = choice.Split('.')[0];

            try
            {
                switch (selection)
                {
                    case "1": await RunOrchestratorLoopAsync(cancellationToken); break;
                    case "2": await ShowSetupMenuAsync(cancellationToken); break;
                    case "3": await ShowLocalMenuAsync(cancellationToken); break;
                    case "4": await ShowDebugMenuAsync(cancellationToken); break;
                    case "5": TokenManager.ReloadAllConfigs(); Pause("...", cancellationToken); break;
                    case "0": return;
                }
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); Pause("...", CancellationToken.None); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]"); AnsiConsole.WriteException(ex); Pause("...", CancellationToken.None); }
        }
    }

    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) { /* ... */ }
    private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) { /* ... */ }
    private static async Task ShowDebugMenuAsync(CancellationToken cancellationToken) { /* ... */ }
    private static async Task TestLocalBotAsync(CancellationToken cancellationToken) { /* ... */ }
    private static (string executor, string args) GetRunCommandLocal(string botPath, string type) { /* ... */ }
    private static string GetProjectRoot() { /* ... */ }


    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator Loop...[/]");
        // ... (Log awal loop) ...

        while (!cancellationToken.IsCancellationRequested)
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken();
            TokenState currentState = TokenManager.GetState();
            string? activeCodespace = currentState.ActiveCodespaceName;

            AnsiConsole.Write(new Rule($"[yellow]Processing Token #{currentState.CurrentIndex + 1} (@{currentToken.Username ?? "???"})[/]").LeftJustified());

            try
            {
                // ... (Cek Billing) ...
                 AnsiConsole.MarkupLine("Checking billing quota...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "???");


                if (!billingInfo.IsQuotaOk)
                {
                    AnsiConsole.MarkupLine("[red]Quota insufficient. Rotating token...[/]");
                    if (!string.IsNullOrEmpty(activeCodespace))
                    {
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace);
                        currentState.ActiveCodespaceName = null;
                        // --- PERBAIKAN DI SINI ---
                        TokenManager.SaveState(currentState); // <--- PASS currentState
                        // --- AKHIR PERBAIKAN ---
                    }
                    TokenManager.SwitchToNextToken();
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }

                // ... (Ensure Codespace) ...
                AnsiConsole.MarkupLine("Ensuring healthy codespace...");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);


                if (currentState.ActiveCodespaceName != activeCodespace)
                {
                    currentState.ActiveCodespaceName = activeCodespace;
                    // --- PERBAIKAN DI SINI ---
                    TokenManager.SaveState(currentState); // <--- PASS currentState
                    // --- AKHIR PERBAIKAN ---
                    AnsiConsole.MarkupLine($"[green]✓ Active codespace set to: {activeCodespace}[/]");

                    // ... (Upload Config & Trigger Startup) ...
                    AnsiConsole.MarkupLine("New/Recreated codespace detected. Uploading configs and triggering startup...");
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace);
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                    AnsiConsole.MarkupLine("[green]✓ Initial startup complete. Entering Keep-Alive mode.[/]");

                }
                else
                {
                    AnsiConsole.MarkupLine("[green]✓ Codespace is healthy and running.[/]");
                }

                // ... (Keep-Alive Delay) ...
                AnsiConsole.MarkupLine($"Sleeping for Keep-Alive interval ({KeepAliveInterval.TotalMinutes} minutes)...");
                await Task.Delay(KeepAliveInterval, cancellationToken);


                // ... (Keep-Alive SSH Check) ...
                AnsiConsole.MarkupLine("Keep-Alive: Checking SSH health...");
                if (string.IsNullOrEmpty(activeCodespace))
                {
                     AnsiConsole.MarkupLine("[yellow]Keep-Alive: No active codespace found in state. Skipping SSH check.[/]");
                     currentState.ActiveCodespaceName = null;
                     // --- PERBAIKAN DI SINI ---
                     TokenManager.SaveState(currentState); // <--- PASS currentState
                     // --- AKHIR PERBAIKAN ---
                     continue;
                }

                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace))
                {
                    AnsiConsole.MarkupLine("[red]Keep-Alive: SSH check failed![/]");
                    currentState.ActiveCodespaceName = null;
                    // --- PERBAIKAN DI SINI ---
                    TokenManager.SaveState(currentState); // <--- PASS currentState
                    // --- AKHIR PERBAIKAN ---
                    AnsiConsole.MarkupLine("[yellow]Will attempt to recreate on next cycle.[/]");
                } else {
                     AnsiConsole.MarkupLine("[green]Keep-Alive: SSH check OK.[/]");
                }

            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Orchestrator loop cancelled.[/]"); break; }
            catch (Exception ex)
            {
                // ... (Error handling loop) ...
                 AnsiConsole.MarkupLine($"[bold red]ERROR in orchestrator loop:[/]");
                 AnsiConsole.WriteException(ex);
                 // ... (Retry delay logic) ...
            }
        }
    }

    // ... (Pause helper) ...
     private static void Pause(string message, CancellationToken cancellationToken) { /* ... */ }

} // Akhir class Program
