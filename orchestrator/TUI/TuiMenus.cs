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
                            "4. Attach/Setup Bots (Monitor & Record UI)",
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
                        Program.Pause("Press Enter untuk kembali ke menu...", CancellationToken.None); 
                    } else { 
                        AnsiConsole.MarkupLine("\n[yellow]Menu operation cancelled unexpectedly.[/]");
                        Program.Pause("Press Enter...", CancellationToken.None);
                    }
                }
                catch (Exception ex) { 
                    var msg = ex.Message ?? ex.ToString();
                    AnsiConsole.MarkupLine($"[red]Error in menu operation: {msg.EscapeMarkup()}[/]");
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
                 catch (Exception ex) { 
                     var msg = ex.Message ?? ex.ToString();
                     AnsiConsole.MarkupLine($"[red]Error: {msg.EscapeMarkup()}[/]"); 
                     Program.Pause("Press Enter...", CancellationToken.None); 
                 } 
            }
         }

         private static async Task ShowLocalProxyMenuAsync(CancellationToken linkedCancellationToken) {
             while (!linkedCancellationToken.IsCancellationRequested) {
                 await Task.Yield();
                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Proxy").Color(Color.Green));
                 var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                         .Title("\n[bold white]LOCAL PROXY MGMT[/]").PageSize(10).WrapAround(true)
                         .AddChoices(new[] { "1. Run ProxySync (Interactive)", "0. Back" }));
                 var sel = selection[0].ToString(); if (sel == "0") return;

                 try {
                    if (sel == "1") await ProxyService.DeployProxies(linkedCancellationToken); 
                    Program.Pause("Tekan Enter...", linkedCancellationToken); 
                 } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]ProxySync operation cancelled.[/]"); return; } 
                 catch (Exception ex) { 
                     var msg = ex.Message ?? ex.ToString();
                     AnsiConsole.MarkupLine($"[red]Error: {msg.EscapeMarkup()}[/]"); 
                     Program.Pause("Press Enter...", CancellationToken.None); 
                 } 
            }
         }

        private static async Task ShowRecordMenuAsync(CancellationToken linkedCancellationToken)
        {
             var config = Core.BotConfig.Load();
             if (config == null || !config.BotsAndTools.Any()) {
                 AnsiConsole.MarkupLine("[red]✗ Gagal memuat bots_config.json.[/]");
                 Program.Pause("Press Enter...", linkedCancellationToken); return;
             }
             
             var targetBots = config.BotsAndTools
                 .Where(b => b.Enabled && b.IsBot)
                 .Select(b => new { 
                     Name = b.Name, 
                     Path = b.Path,
                     Recorded = ExpectManager.CheckExpectScriptExists(b.Path)
                 })
                 .ToList();
             
             if (!targetBots.Any()) { AnsiConsole.MarkupLine("[yellow]Tidak ada bot aktif yang terdaftar untuk di-setup.[/]"); Program.Pause("Press Enter...", linkedCancellationToken); return; }

             while (!linkedCancellationToken.IsCancellationRequested)
             {
                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup UI").Color(Color.Fuchsia));
                 static string TruncateString(string value, int maxLength)
                 {
                     if (string.IsNullOrEmpty(value)) return value;
                     return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
                 }
                 var choices = targetBots
                     .Select(b => $"{b.Name.EscapeMarkup()} (Status: [{(b.Recorded ? "green" : "red")}]{(b.Recorded ? "Recorded" : "NONE")}[/])").ToList();
                 choices.Insert(0, "<< Back");
                 choices.Insert(1, "--- MANUAL ACTIONS ---");
                 choices.Insert(2, "[yellow]Delete Setup Script[/]");

                 var selectedChoice = AnsiConsole.Prompt(
                     new SelectionPrompt<string>()
                         .Title("\n[bold white]SETUP INTERAKTIF (RECORD) (Manual Input)[/]")
                         .PageSize(15)
                         .AddChoices(choices));
                 
                 if (selectedChoice == "<< Back") return;
                 if (selectedChoice == "--- MANUAL ACTIONS ---") continue;

                 if (selectedChoice == "[yellow]Delete Setup Script[/]")
                 {
                      ShowDeleteExpectScriptMenuAsync(targetBots.Select(b => b.Path).ToList(), linkedCancellationToken);
                      targetBots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).Select(b => new { Name = b.Name, Path = b.Path, Recorded = ExpectManager.CheckExpectScriptExists(b.Path) }).ToList();
                      continue;
                 }

                 var selectedBot = targetBots.FirstOrDefault(b => selectedChoice.StartsWith(b.Name.EscapeMarkup()));
                 if (selectedBot == null)
                 {
                     AnsiConsole.MarkupLine("[red]Error: Bot yang dipilih tidak ditemukan (post-escape).[/]");
                     Program.Pause("Tekan Enter...", linkedCancellationToken);
                     continue;
                 }

                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Record").Color(Color.Fuchsia));
                 AnsiConsole.MarkupLine($"\n[cyan]SETUP SCRIPT UNTUK {selectedBot.Name.EscapeMarkup()}[/]");

                 var existingScript = ExpectManager.LoadExpectScript(selectedBot.Path);
                 if (existingScript != null)
                 {
                      AnsiConsole.MarkupLine("[yellow]Script lama ditemukan. Masukkan input baru untuk menimpanya.[/]");
                      DisplayExpectScript(existingScript, TruncateString);
                 }
                 
                 AnsiConsole.MarkupLine("\n[bold]Mode Perekaman Dimulai (Expect Script).[/bold]");
                 AnsiConsole.MarkupLine("[dim]Masukkan [bold]PROMPT[/] yang diharapkan (Output bot) dan [bold]SEND[/] (Input user).[/dim]");
                 AnsiConsole.MarkupLine("[red]Tekan Enter (kosong) di PROMPT untuk mengakhiri rekaman.[/red]");

                 var newScript = new List<ExpectStep>();
                 int step = 1;
                 
                 while (true)
                 {
                      AnsiConsole.MarkupLine($"\n[cyan]-- STEP {step} --[/]");
                      string expectPrompt = AnsiConsole.Ask<string>($"[bold]EXPECT (Prompt Bot)[/]:").Trim();
                      if (string.IsNullOrEmpty(expectPrompt)) break;
                      string sendInput = AnsiConsole.Ask<string>($"[bold]SEND (Input User)[/]:").Trim();
                      newScript.Add(new ExpectStep { Expect = expectPrompt, Send = sendInput });
                      step++;
                 }
                 
                 if (newScript.Any())
                 {
                     ExpectManager.SaveExpectScript(selectedBot.Path, newScript);
                 }
                 else
                 {
                     AnsiConsole.MarkupLine("[yellow]Perekaman dibatalkan atau kosong.[/]");
                 }
                 
                 Program.Pause("Tekan Enter...", linkedCancellationToken); 
             }
        }
        
        private static void DisplayExpectScript(List<ExpectStep> script, Func<string, int, string> truncator)
        {
             var table = new Table().Title("[bold yellow]Existing Setup Script[/]").Expand();
             table.AddColumn("[cyan]Step[/]");
             table.AddColumn("[cyan]EXPECT (Prompt)[/]");
             table.AddColumn("[cyan]SEND (Input)[/]");

             for(int i = 0; i < script.Count; i++)
             {
                 table.AddRow(
                     (i+1).ToString(),
                     truncator(script[i].Expect.EscapeMarkup(), 50),
                     truncator(script[i].Send.EscapeMarkup(), 50)
                 );
             }
             AnsiConsole.Write(table);
        }
        
        private static void ShowDeleteExpectScriptMenuAsync(List<string> botPaths, CancellationToken linkedCancellationToken)
        {
             var choices = botPaths
                .Where(p => ExpectManager.CheckExpectScriptExists(p))
                .Select(p => {
                    var name = p.Split(new char[] { '/', '\\' }).Last();
                    return $"{name.EscapeMarkup()} ({p.EscapeMarkup()})";
                }).ToList();
             
             if (!choices.Any()) { AnsiConsole.MarkupLine("[yellow]Tidak ada script expect yang bisa dihapus.[/]"); Program.Pause("Press Enter...", linkedCancellationToken); return; }
             
             choices.Insert(0, "<< Back");

             var selectedChoice = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                     .Title("\n[bold red]Pilih script untuk dihapus[/]")
                     .PageSize(15)
                     .AddChoices(choices));

             if (selectedChoice == "<< Back") return;
             
             var pathStart = selectedChoice.LastIndexOf('(');
             var pathEnd = selectedChoice.LastIndexOf(')');
             
             if (pathStart == -1 || pathEnd == -1 || pathEnd < pathStart)
             {
                 AnsiConsole.MarkupLine("[red]Error parsing pilihan.[/]");
                 Program.Pause("Tekan Enter...", linkedCancellationToken);
                 return;
             }
             var botPath = selectedChoice.Substring(pathStart + 1, pathEnd - pathStart - 1);
             var displayName = selectedChoice.Substring(0, pathStart).Trim();

             if (AnsiConsole.Confirm($"[red]Anda yakin ingin menghapus script setup untuk {displayName.EscapeMarkup()} ({botPath.EscapeMarkup()})?[/]", false))
             {
                 ExpectManager.DeleteExpectScript(botPath);
             }
             Program.Pause("Tekan Enter...", linkedCancellationToken);
        }

        private static async Task ShowAttachMenuAsync(CancellationToken linkedCancellationToken) {
            if (linkedCancellationToken.IsCancellationRequested) return; 
            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Monitor").Color(Color.Blue));
            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold white]MONITORING & SETUP[/]")
                    .PageSize(7)
                    .AddChoices(new[] {
                        "[green]1. Monitor Running Bots (Attach Tmux)[/]",
                        "[yellow]2. Setup Interaktif (Record/Edit Replay Script)[/]",
                        "[dim]3. Open Remote Shell (Debug)[/]",
                        "<< Back"
                    }));
            switch(selection)
            {
                 case "[yellow]2. Setup Interaktif (Record/Edit Replay Script)[/]":
                    await ShowRecordMenuAsync(linkedCancellationToken);
                    return;
                 case "[dim]3. Open Remote Shell (Debug)[/]":
                    await ShowRemoteShellAsync(linkedCancellationToken);
                    return;
                 case "<< Back":
                    return;
            }
            var currentToken = TokenManager.GetCurrentToken(); 
            var state = TokenManager.GetState(); 
            var activeCodespace = state.ActiveCodespaceName;
            if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace recorded.[/]"); Program.Pause("Press Enter...", linkedCancellationToken); return; }
            AnsiConsole.MarkupLine($"[dim]Checking active codespace: [blue]{activeCodespace.EscapeMarkup()}[/][/]");
            List<string> sessions;
            try {
                 sessions = await CodeManager.GetTmuxSessions(currentToken, activeCodespace);
                 linkedCancellationToken.ThrowIfCancellationRequested(); 
            } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Fetching sessions cancelled.[/]"); return; }
            catch (Exception ex) { 
                var msg = ex.Message ?? ex.ToString();
                AnsiConsole.MarkupLine($"[red]Error fetching tmux sessions: {msg.EscapeMarkup()}[/]"); 
                Program.Pause("Press Enter...", CancellationToken.None); 
                return; 
            }
            if (!sessions.Any()) { AnsiConsole.MarkupLine("[yellow]No running bot sessions found in tmux.[/]"); Program.Pause("Press Enter...", linkedCancellationToken); return; }
            var backOption = "<< Back"; 
            var choices = new List<string> { backOption };
            choices.AddRange(sessions); 
            var selectedBot = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Attach to session (in [green]{activeCodespace.EscapeMarkup()}[/]):")
                    .PageSize(15)
                    .AddChoices(choices) 
                    .UseConverter(s => {
                        if (s == backOption) return $"[green]{s.EscapeMarkup()}[/]";
                        return s.EscapeMarkup();
                    })
                );
            if (selectedBot == backOption || linkedCancellationToken.IsCancellationRequested) return;
            string originalBotName = selectedBot;
            AnsiConsole.MarkupLine($"\n[cyan]Attaching to tmux window [yellow]{originalBotName.EscapeMarkup()}[/]...[/]");
            AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B, D[/] to detach)[/]");
            AnsiConsole.MarkupLine("[red](Ctrl+C inside attach will detach you)[/]");
            try {
                string tmuxSessionName = "automation_hub_bots"; 
                string escapedBotNameForTmux = originalBotName.Replace("\"", "\\\"");
                string args = $"codespace ssh --codespace \"{activeCodespace}\" -- tmux attach-session -t {tmuxSessionName} \\; select-window -t \"{escapedBotNameForTmux}\"";
                await ShellUtil.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCancellationToken, useProxy: false);
                AnsiConsole.MarkupLine("\n[yellow]✓ Detached from tmux session.[/]");
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Attach session cancelled (likely Ctrl+C/Exit).[/]"); }
            catch (Exception ex) { 
                var msg = ex.Message ?? ex.ToString();
                AnsiConsole.MarkupLine($"\n[red]Attach error: {msg.EscapeMarkup()}[/]"); 
                Program.Pause("Press Enter...", CancellationToken.None); 
            }
        }

        private static async Task ShowRemoteShellAsync(CancellationToken linkedCancellationToken)
        {
            if (linkedCancellationToken.IsCancellationRequested) return;
            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Shell").Color(Color.Magenta1));
            var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;
            if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace recorded.[/]"); Program.Pause("Press Enter...", linkedCancellationToken); return; }
            AnsiConsole.MarkupLine($"[cyan]Opening interactive shell in [green]{activeCodespace.EscapeMarkup()}[/]...[/]");
            AnsiConsole.MarkupLine("[dim](Type [bold]exit[/] atau [bold]Ctrl+D[/] to close)[/]");
            AnsiConsole.MarkupLine("[red](Ctrl+C inside shell will likely close it)[/]");
            try {
                string args = $"codespace ssh --codespace \"{activeCodespace}\"";
                await ShellUtil.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCancellationToken, useProxy: false);
                AnsiConsole.MarkupLine("\n[yellow]✓ Remote shell closed.[/]");
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Remote shell session cancelled (likely Ctrl+C).[/]"); }
            catch (Exception ex) { 
                var msg = ex.Message ?? ex.ToString();
                AnsiConsole.MarkupLine($"\n[red]Remote shell error: {msg.EscapeMarkup()}[/]"); 
                Program.Pause("Press Enter...", CancellationToken.None); 
            }
        }
    }
}
