using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Services; 
using Orchestrator.Codespace; 
using Orchestrator.Util; 
using Orchestrator.Core; 

namespace Orchestrator.TUI 
{
    internal static class TuiMenus
    {
        // ... (RunInteractiveMenuAsync tidak berubah) ...
        internal static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken) 
        {
            while (!cancellationToken.IsCancellationRequested) {
                AnsiConsole.Clear();
                AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
                AnsiConsole.MarkupLine("[dim]Local Control, Remote Execution[/]");

                var selection = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("\n[bold white]MAIN MENU[/]")
                        .PageSize(8) 
                        .WrapAround(true)
                        .AddChoices(new[] {
                            "1. Start/Manage Codespace Runner (Continuous Loop)",
                            "2. Token & Collaborator Management",
                            "3. Proxy Management (Local TUI Proxy)",
                            "4. Attach to Bot Session (Remote Tmux)",
                            "5. Migrasi Kredensial Lokal (Jalankan 1x)",
                            "6. Open Remote Shell (Codespace)",
                            "0. Exit"
                        }));

                var choice = selection[0].ToString();

                var interactiveCts = new CancellationTokenSource();
                Program.SetInteractiveCts(interactiveCts); 

                using var linkedCtsMenu = CancellationTokenSource.CreateLinkedTokenSource(interactiveCts.Token, cancellationToken);

                try {
                    switch (choice) {
                        case "1":
                            AnsiConsole.MarkupLine(string.Empty); 
                            bool useProxy = AnsiConsole.Confirm("[bold yellow]Gunakan Proxy[/] untuk loop ini? (Disarankan [green]Yes[/])", true);
                            TokenManager.SetProxyUsage(useProxy);

                            await TuiLoop.RunOrchestratorLoopAsync(cancellationToken);
                            if (cancellationToken.IsCancellationRequested) return; 
                            break; 
                        case "2": await ShowSetupMenuAsync(linkedCtsMenu.Token); break; 
                        case "3": await ShowLocalMenuAsync(linkedCtsMenu.Token); break; 
                        case "4": await ShowAttachMenuAsync(linkedCtsMenu.Token); break; 
                        case "5": 
                            await MigrateService.RunMigration(linkedCtsMenu.Token); 
                            Program.Pause("Tekan Enter...", linkedCtsMenu.Token); break; 
                        case "6": await ShowRemoteShellAsync(linkedCtsMenu.Token); break; 
                        case "0":
                            AnsiConsole.MarkupLine("Exiting...");
                            Program.TriggerFullShutdown(); 
                            return; 
                    }
                }
                catch (OperationCanceledException) { 
                    if (cancellationToken.IsCancellationRequested) { 
                        AnsiConsole.MarkupLine("\n[yellow]Main application shutdown requested during menu operation.[/]");
                        return; 
                    } else if (interactiveCts?.IsCancellationRequested == true) { 
                        AnsiConsole.MarkupLine("\n[yellow]Menu operation cancelled by user (Ctrl+C).[/]");
                        Program.Pause("Press Enter to return to menu...", CancellationToken.None); 
                    } else { 
                        AnsiConsole.MarkupLine("\n[yellow]Menu operation cancelled unexpectedly.[/]");
                        Program.Pause("Press Enter...", CancellationToken.None);
                    }
                }
                catch (Exception ex) { 
                    AnsiConsole.MarkupLine($"[red]Error in menu operation: {ex.Message.EscapeMarkup()}[/]");
                    AnsiConsole.WriteException(ex);
                    Program.Pause("Press Enter...", CancellationToken.None);
                }
                finally {
                    interactiveCts?.Dispose();
                    Program.ClearInteractiveCts(); 
                }
            } 
            AnsiConsole.MarkupLine("[yellow]Exiting Menu loop due to main cancellation.[/]");
        } 

        // ... (ShowSetupMenuAsync dan
