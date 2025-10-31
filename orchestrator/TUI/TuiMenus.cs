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
                     Program.Pause("Press Enter...", linkedCancellationToken); 
                 } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Setup operation cancelled.[/]"); return; } 
                 catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Program.Pause("Press Enter...", CancellationToken.None); } 
            }
         }

         private static async Task ShowLocalMenuAsync(CancellationToken linkedCancellationToken) {
             while (!linkedCancellationToken.IsCancellationRequested) {
                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Proxy").Color(Color.Green));
                 var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                         .Title("\n[bold white]LOCAL PROXY MGMT[/]").PageSize(10).WrapAround(true)
                         .AddChoices(new[] { "1. Run ProxySync (Interactive)", "0. Back" }));
                 var sel = selection[0].ToString(); if (sel == "0") return;

                 try {
                    if (sel == "1") await ProxyService.DeployProxies(linkedCancellationToken); 
                    Program.Pause("Press Enter...", linkedCancellationToken); 
                 } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]ProxySync operation cancelled.[/]"); return; } 
                 catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Program.Pause("Press Enter...", CancellationToken.None); } 
            }
        }

        private static async Task ShowAttachMenuAsync(CancellationToken linkedCancellationToken) {
            if (linkedCancellationToken.IsCancellationRequested) return; 

            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Attach").Color(Color.Blue));
            var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;
            if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace recorded.[/]"); Program.Pause("Press Enter...", linkedCancellationToken); return; }

            AnsiConsole.MarkupLine($"[dim]Checking active codespace: [blue]{activeCodespace.EscapeMarkup()}[/][/]");
            List<string> sessions;
            try {
                 sessions = await CodeManager.GetTmuxSessions(currentToken, activeCodespace);
                 linkedCancellationToken.ThrowIfCancellationRequested(); 
            } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Fetching sessions cancelled.[/]"); return; }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetching tmux sessions: {ex.Message.EscapeMarkup()}[/]"); Program.Pause("Press Enter...", CancellationToken.None); return; }

            if (!sessions.Any()) { AnsiConsole.MarkupLine("[yellow]No running bot sessions found in tmux.[/]"); Program.Pause("Press Enter...", linkedCancellationToken); return; }
            var backOption = "[ << Back ]"; sessions.Insert(0, backOption);

            var selectedBot = AnsiConsole.Prompt(new SelectionPrompt<string>().Title($"Attach to session (in [green]{activeCodespace.EscapeMarkup()}[/]):").PageSize(15).AddChoices(sessions));

            if (selectedBot == backOption || linkedCancellationToken.IsCancellationRequested) return;

            AnsiConsole.MarkupLine($"\n[cyan]Attaching to tmux window [yellow]{selectedBot.EscapeMarkup()}[/]...[/]");
            AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B, D[/] to detach)[/]");
            AnsiConsole.MarkupLine("[red](Ctrl+C inside attach will detach you)[/]");

            try {
                string tmuxSessionName = "automation_hub_bots"; string escapedBotName = selectedBot.Replace("\"", "\\\"");
                string args = $"codespace ssh --codespace \"{activeCodespace}\" -- tmux attach-session -t {tmuxSessionName} \\; select-window -t \"{escapedBotName}\"";
                
                // === PERBAIKAN DI SINI ===
                // Ganti linkedCtsMenu.Token -> linkedCancellationToken
                await ShellUtil.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCancellationToken, useProxy: false);
                
                AnsiConsole.MarkupLine("\n[yellow]✓ Detached from tmux session.[/]");
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Attach session cancelled (likely Ctrl+C).[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"\n[red]Attach error: {ex.Message.EscapeMarkup()}[/]"); Program.Pause("Press Enter...", CancellationToken.None); }
        }

        private static async Task ShowRemoteShellAsync(CancellationToken linkedCancellationToken)
        {
            if (linkedCancellationToken.IsCancellationRequested) return;
            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Shell").Color(Color.Magenta1));
            var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;

            if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace recorded.[/]"); Program.Pause("Press Enter...", linkedCancellationToken); return; }

            AnsiConsole.MarkupLine($"[cyan]Opening interactive shell in [green]{activeCodespace.EscapeMarkup()}[/]...[/]");
            AnsiConsole.MarkupLine("[dim](Type [bold]exit[/] or [bold]Ctrl+D[/] to close)[/]");
            AnsiConsole.MarkupLine("[red](Ctrl+C inside shell will likely close it)[/]");

            try {
                string args = $"codespace ssh --codespace \"{activeCodespace}\"";
                
                // === PERBAIKAN DI SINI ===
                // Ganti linkedCtsMenu.Token -> linkedCancellationToken
                await ShellUtil.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCancellationToken, useProxy: false);
                
                AnsiConsole.MarkupLine("\n[yellow]✓ Remote shell closed.[/]");
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Remote shell session cancelled (likely Ctrl+C).[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"\n[red]Remote shell error: {ex.Message.EscapeMarkup()}[/]"); Program.Pause("Press Enter...", CancellationToken.None); }
        }
    }
}
