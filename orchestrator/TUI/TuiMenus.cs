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
        internal static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken) 
        {
            while (!cancellationToken.IsCancellationRequested) {
                AnsiConsole.Clear();
                AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
                AnsiConsole.MarkupLine("[dim]Local Control, Remote Execution[/]");

                var selection = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("\n[bold white]MAIN MENU[/]")
                        .PageSize(5) 
                        .WrapAround(true)
                        .AddChoices(new[] {
                            "1. Start/Manage Codespace Runner (Full Auto)",
                            "2. Token & Collaborator Management",
                            "3. Proxy Management (Local TUI Proxy)",
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
                            
                            await TuiLoop.RunOrchestratorLoopAsync(linkedCtsMenu.Token);
                            
                            if (linkedCtsMenu.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                            {
                                AnsiConsole.MarkupLine("\n[yellow]Loop dihentikan (Ctrl+C). Codespace dibiarkan berjalan.[/]");
                                Program.Pause("Tekan Enter untuk kembali ke menu...", CancellationToken.None); 
                            }
                            
                            if (cancellationToken.IsCancellationRequested) return; 
                            break; 
                        case "2": await ShowSetupMenuAsync(linkedCtsMenu.Token); break; 
                        case "3": await ShowLocalProxyMenuAsync(linkedCtsMenu.Token); break; 
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
                        Program.Pause("Press Enter untuk kembali ke menu...", CancellationToken.None); 
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

         private static async Task ShowSetupMenuAsync(CancellationToken linkedCancellationToken) {
            while (!linkedCancellationToken.IsCancellationRequested) {
                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Color(Color.Yellow));
                 var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                         .Title("\n[bold white]TOKEN & COLLABORATOR[/]").PageSize(10).WrapAround(true)
                         .AddChoices(new[] { "1. Validate Tokens", "2. Invite Collaborators", "3. Accept Invitations", "4. Show Status", "0. Back" }));
                 var sel = selection[0].ToString(); if (sel == "0") return; 

                 try {
                     switch (sel) {
                         case "1": await CollabService.ValidateAllTokens(linkedCancellationToken); break;
                         case "2": await CollabService.InviteCollaborators(linkedCancellationToken); break;
                         case "3": await CollabService.AcceptInvitations(linkedCancellationToken); break;
                         case "4": await Task.Run(() => TokenManager.ShowStatus(), linkedCancellationToken); break; 
                     }
                     Program.Pause("Tekan Enter...", linkedCancellationToken); 
                 } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Setup operation cancelled.[/]"); return; } 
                 catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Program.Pause("Press Enter...", CancellationToken.None); } 
            }
         }

         private static async Task ShowLocalProxyMenuAsync(CancellationToken linkedCancellationToken) {
             while (!linkedCancellationToken.IsCancellationRequested) {
                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Proxy").Color(Color.Green));
                 var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                         .Title("\n[bold white]LOCAL PROXY MGMT[/]").PageSize(10).WrapAround(true)
                         .AddChoices(new[] { "1. Run ProxySync (Interactive)", "0. Back" }));
                 var sel = selection[0].ToString(); if (sel == "0") return;

                 try {
                    if (sel == "1") await ProxyService.DeployProxies(linkedCancellationToken); 
                    Program.Pause("Tekan Enter...", linkedCancellationToken); 
                 } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]ProxySync operation cancelled.[/]"); return; } 
                 catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Program.Pause("Press Enter...", CancellationToken.None); } 
            }
         }
    }
}
