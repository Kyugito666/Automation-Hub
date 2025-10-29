using Spectre.Console;
using System;
using System.IO;
using System.Linq;
// using System.Runtime.InteropServices; // Tidak terpakai
// using System.Text.Json; // Tidak terpakai
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator
{
    internal static class Program
    {
        // Token utama aplikasi, dibatalkan saat shutdown
        private static CancellationTokenSource _mainCts = new CancellationTokenSource();
        // Token khusus untuk operasi interaktif (attach, shell, menu)
        private static CancellationTokenSource? _interactiveCts;
        // Flag penanda shutdown
        private static volatile bool _isShuttingDown = false;
        private static volatile bool _forceQuitRequested = false;
        private static readonly object _shutdownLock = new object();

        // Flag untuk mencegah IP Auth rekursif/berbarengan
        private static bool _isAttemptingIpAuth = false;

        // Interval & Delay
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes((3 * 60) + 30); // 3.5 jam
        private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

        public static async Task Main(string[] args)
        {
            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true; // Override default Ctrl+C behavior

                lock (_shutdownLock)
                {
                    AnsiConsole.MarkupLine("\n[yellow]Ctrl+C detected...[/]");

                    if (_isShuttingDown) // Jika shutdown sudah dimulai
                    {
                        if (_forceQuitRequested) {
                            AnsiConsole.MarkupLine("[bold red]FORCE QUIT CONFIRMED. Exiting immediately![/]");
                            Environment.Exit(1); // Keluar paksa
                        }
                        AnsiConsole.MarkupLine("[bold red]Shutdown already in progress. Press Ctrl+C again to force quit.[/]");
                        _forceQuitRequested = true;
                        // Reset flag force quit setelah 3 detik
                        Task.Delay(3000).ContinueWith(_ => {
                             lock(_shutdownLock) { _forceQuitRequested = false; }
                             AnsiConsole.MarkupLine("[dim](Force quit flag reset)[/]");
                        });
                        return; // Jangan lakukan apa-apa lagi
                    }

                    // Jika ada operasi interaktif (attach/shell/sub-menu) berjalan
                    if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
                    {
                        AnsiConsole.MarkupLine("[yellow]Attempting to cancel interactive operation...[/]");
                        try {
                             _interactiveCts.Cancel(); // Kirim sinyal cancel ke operasi interaktif
                             // Jangan trigger full shutdown dulu, biarkan operasi interaktif berhenti
                        } catch (ObjectDisposedException) {
                             AnsiConsole.MarkupLine("[dim]Interactive operation already disposed.[/]");
                             _interactiveCts = null;
                             TriggerFullShutdown(); // Coba shutdown utama jika interactive sudah selesai
                        } catch (Exception ex) {
                             AnsiConsole.MarkupLine($"[red]Error cancelling interactive op: {ex.Message.EscapeMarkup()}[/]");
                             TriggerFullShutdown(); // Coba shutdown utama jika cancel gagal
                        }
                    }
                    else // Jika tidak ada operasi interaktif atau sudah di-cancel
                    {
                         TriggerFullShutdown(); // Langsung trigger shutdown TUI
                    }
                }
            }; // Akhir CancelKeyPress handler

            try {
                TokenManager.Initialize(); // Inisialisasi token, proxy, state, cache
                // Jalankan loop atau menu berdasarkan argumen
                if (args.Length > 0 && args[0].ToLower() == "--run") {
                    await RunOrchestratorLoopAsync(_mainCts.Token); // Pakai token utama
                } else {
                    await RunInteractiveMenuAsync(_mainCts.Token); // Pakai token utama
                }
            }
            catch (OperationCanceledException) when (_mainCts.IsCancellationRequested) { // Tangkap cancel utama
                AnsiConsole.MarkupLine("\n[yellow]Main application loop cancelled.[/]");
            }
            catch (Exception ex) { // Tangkap error tak terduga di level tertinggi
                AnsiConsole.MarkupLine("\n[bold red]FATAL UNHANDLED ERROR in Main:[/]");
                AnsiConsole.WriteException(ex);
            }
            finally {
                AnsiConsole.MarkupLine("\n[dim]Application shutdown sequence complete.[/]");
                await Task.Delay(500); // Jeda singkat sebelum console window tertutup
            }
        } // Akhir Main

        // Fungsi helper untuk memicu shutdown TUI secara graceful
        private static void TriggerFullShutdown() {
             lock(_shutdownLock) {
                if (_isShuttingDown) return; // Hindari trigger ganda

                AnsiConsole.MarkupLine("[bold red]SHUTDOWN TRIGGERED! Attempting graceful exit...[/]");
                AnsiConsole.MarkupLine("[dim](Attempting to stop active codespace if running...)[/]");
                AnsiConsole.MarkupLine("[dim](Press Ctrl+C again to force quit immediately.)[/]");

                _isShuttingDown = true; // Tandai shutdown sedang berjalan
                _forceQuitRequested = false; // Reset flag force quit

                // Jalankan proses stop codespace di background (jangan ditunggu di sini)
                Task.Run(async () => {
                    await PerformGracefulShutdownAsync(); // Coba stop codespace
                    // Setelah mencoba stop, baru batalkan token utama
                    if (!_mainCts.IsCancellationRequested) {
                        AnsiConsole.MarkupLine("[yellow]Signalling main loop/menu to exit...[/]");
                        try { _mainCts.Cancel(); } catch (ObjectDisposedException) {} // Handle jika sudah di-dispose
                    }
                });
             }
        }

        // Fungsi untuk mencoba stop codespace saat shutdown
        private static async Task PerformGracefulShutdownAsync()
        {
            AnsiConsole.MarkupLine("[dim]Executing graceful shutdown: Attempting to stop codespace...[/]");
            try {
                // Ambil token dan state saat ini
                var token = TokenManager.GetCurrentToken();
                var state = TokenManager.GetState();
                var activeCodespace = state.ActiveCodespaceName;

                if (!string.IsNullOrEmpty(activeCodespace)) {
                    AnsiConsole.MarkupLine($"[yellow]Sending STOP command to codespace '{activeCodespace.EscapeMarkup()}' via gh cli...[/]");
                    // Panggil fungsi stop dari CodespaceManager (tidak pakai cancel token)
                    await CodespaceManager.StopCodespace(token, activeCodespace);
                    // Log dari StopCodespace akan muncul
                    AnsiConsole.MarkupLine($"[green]✓ Stop command attempt finished for '{activeCodespace.EscapeMarkup()}'.[/]");
                } else {
                    AnsiConsole.MarkupLine("[yellow]No active codespace recorded to stop.[/]");
                }
            } catch (Exception ex) {
                // Tangkap error jika terjadi (misal token invalid saat shutdown)
                AnsiConsole.MarkupLine($"[red]Exception during codespace stop attempt: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
            AnsiConsole.MarkupLine("[dim]Graceful shutdown steps finished.[/]");
        }

        // --- Menu Interaktif Utama ---
        private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken) // Token utama (_mainCts)
        {
            // Loop menu hanya berhenti jika token utama di-cancel
            while (!cancellationToken.IsCancellationRequested) {
                AnsiConsole.Clear();
                AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
                AnsiConsole.MarkupLine("[dim]Local Control, Remote Execution[/]");

                var selection = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("\n[bold white]MAIN MENU[/]")
                        .PageSize(9) // Jumlah item menu + 1
                        .WrapAround(true)
                        .AddChoices(new[] {
                            "1. Start/Manage Codespace Runner (Continuous Loop)",
                            "2. Token & Collaborator Management",
                            "3. Proxy Management (Local TUI Proxy)",
                            "4. Attach to Bot Session (Remote Tmux)",
                            "5. Migrasi Kredensial Lokal (Jalankan 1x)",
                            "6. Open Remote Shell (Codespace)",
                            "7. Delete All GitHub Secrets (Fix 200KB Error)", // Tambah menu delete secrets
                            "0. Exit"
                        }));

                var choice = selection[0].ToString();

                // Buat CancellationTokenSource BARU khusus untuk operasi menu ini
                // Ini yang akan di-cancel oleh Ctrl+C pertama kali saat di dalam menu
                _interactiveCts = new CancellationTokenSource();
                // Gabungkan token interaktif dengan token utama
                using var linkedCtsMenu = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, cancellationToken);

                try {
                    switch (choice) {
                        case "1":
                            // Loop orkestrasi HARUS pakai token utama, bukan interaktif
                            await RunOrchestratorLoopAsync(cancellationToken);
                            // Jika loop selesai (misal karena cancel dari dalam loop), cek token utama
                            if (cancellationToken.IsCancellationRequested) return; // Keluar dari menu jika TUI shutdown
                            break; // Kembali ke menu jika loop selesai normal (jarang terjadi)
                        case "2": await ShowSetupMenuAsync(linkedCtsMenu.Token); break; // Pakai linked token
                        case "3": await ShowLocalMenuAsync(linkedCtsMenu.Token); break; // Pakai linked token
                        case "4": await ShowAttachMenuAsync(linkedCtsMenu.Token); break; // Pakai linked token
                        case "5": await CredentialMigrator.RunMigration(linkedCtsMenu.Token); Pause("Tekan Enter...", linkedCtsMenu.Token); break; // Pakai linked token
                        case "6": await ShowRemoteShellAsync(linkedCtsMenu.Token); break; // Pakai linked token
                        case "7":
                            // Panggil delete secrets (jika async, teruskan linkedCtsMenu.Token)
                            await SecretCleanup.DeleteAllSecrets(/* linkedCtsMenu.Token */);
                            Pause("Tekan Enter...", linkedCtsMenu.Token); // Pause dengan linked token
                            break;
                        case "0":
                            AnsiConsole.MarkupLine("Exiting...");
                            TriggerFullShutdown(); // Panggil shutdown TUI
                            return; // Keluar loop menu
                    }
                }
                catch (OperationCanceledException) { // Tangkap cancel dari linkedCtsMenu
                    if (cancellationToken.IsCancellationRequested) { // Jika cancel dari token utama
                        AnsiConsole.MarkupLine("\n[yellow]Main application shutdown requested during menu operation.[/]");
                        return; // Keluar menu
                    } else if (_interactiveCts?.IsCancellationRequested == true) { // Jika cancel dari Ctrl+C
                        AnsiConsole.MarkupLine("\n[yellow]Menu operation cancelled by user (Ctrl+C).[/]");
                        Pause("Press Enter to return to menu...", CancellationToken.None); // Pause tanpa cancel token
                    } else { // Kasus lain (jarang terjadi)
                        AnsiConsole.MarkupLine("\n[yellow]Menu operation cancelled unexpectedly.[/]");
                        Pause("Press Enter...", CancellationToken.None);
                    }
                }
                catch (Exception ex) { // Tangkap error lain di operasi menu
                    AnsiConsole.MarkupLine($"[red]Error in menu operation: {ex.Message.EscapeMarkup()}[/]");
                    AnsiConsole.WriteException(ex);
                    Pause("Press Enter...", CancellationToken.None);
                }
                finally {
                    // Dispose _interactiveCts setelah operasi menu selesai atau di-cancel
                    // Ini penting agar Ctrl+C berikutnya bisa trigger full shutdown
                    _interactiveCts?.Dispose();
                    _interactiveCts = null;
                }
            } // End while menu loop
            AnsiConsole.MarkupLine("[yellow]Exiting Menu loop due to main cancellation.[/]");
        } // Akhir RunInteractiveMenuAsync


        // --- Sub-menu (ShowSetupMenuAsync, ShowLocalMenuAsync, ShowAttachMenuAsync, ShowRemoteShellAsync) ---
        // Semua sub-menu menerima CancellationToken (linked token dari menu utama)
        // dan harus meneruskannya ke fungsi async yang dipanggil & cek secara periodik.

        private static async Task ShowSetupMenuAsync(CancellationToken linkedCancellationToken) {
            // Loop sub-menu berhenti jika linked token di-cancel
            while (!linkedCancellationToken.IsCancellationRequested) {
                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Color(Color.Yellow));
                 var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                         .Title("\n[bold white]TOKEN & COLLABORATOR[/]").PageSize(10).WrapAround(true)
                         .AddChoices(new[] { "1. Validate Tokens", "2. Invite Collaborators", "3. Accept Invitations", "4. Show Status", "0. Back" }));
                 var sel = selection[0].ToString(); if (sel == "0") return; // Kembali ke menu utama

                 try {
                     switch (sel) {
                         // Teruskan linked token ke fungsi-fungsi ini
                         case "1": await CollaboratorManager.ValidateAllTokens(linkedCancellationToken); break;
                         case "2": await CollaboratorManager.InviteCollaborators(linkedCancellationToken); break;
                         case "3": await CollaboratorManager.AcceptInvitations(linkedCancellationToken); break;
                         case "4": await Task.Run(() => TokenManager.ShowStatus(), linkedCancellationToken); break; // Jalankan di thread pool tapi tetap bisa di-cancel
                     }
                     Pause("Press Enter...", linkedCancellationToken); // Pause dengan linked token
                 } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Setup operation cancelled.[/]"); return; } // Kembali ke menu utama jika cancel
                 catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); } // Pause biasa jika error
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
                    if (sel == "1") await ProxyManager.DeployProxies(linkedCancellationToken); // Teruskan linked token
                    Pause("Press Enter...", linkedCancellationToken); // Pause dengan linked token
                 } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]ProxySync operation cancelled.[/]"); return; } // Kembali jika cancel
                 catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); } // Pause biasa jika error
            }
        }

        private static async Task ShowAttachMenuAsync(CancellationToken linkedCancellationToken) {
            if (linkedCancellationToken.IsCancellationRequested) return; // Cek cancel di awal

            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Attach").Color(Color.Blue));
            var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;
            if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace recorded.[/]"); Pause("Press Enter...", linkedCancellationToken); return; }

            AnsiConsole.MarkupLine($"[dim]Checking active codespace: [blue]{activeCodespace.EscapeMarkup()}[/][/]");
            List<string> sessions;
            try {
                 // GetTmuxSessions tidak butuh CancellationToken karena RunGhCommand handle timeout & cancel
                 sessions = await CodespaceManager.GetTmuxSessions(currentToken, activeCodespace);
                 linkedCancellationToken.ThrowIfCancellationRequested(); // Cek cancel setelah operasi
            } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Fetching sessions cancelled.[/]"); return; }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetching tmux sessions: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); return; }

            if (!sessions.Any()) { AnsiConsole.MarkupLine("[yellow]No running bot sessions found in tmux.[/]"); Pause("Press Enter...", linkedCancellationToken); return; }
            var backOption = "[ << Back ]"; sessions.Insert(0, backOption);

            var selectedBot = AnsiConsole.Prompt(new SelectionPrompt<string>().Title($"Attach to session (in [green]{activeCodespace.EscapeMarkup()}[/]):").PageSize(15).AddChoices(sessions));

            // Cek cancel setelah prompt
            if (selectedBot == backOption || linkedCancellationToken.IsCancellationRequested) return;

            AnsiConsole.MarkupLine($"\n[cyan]Attaching to tmux window [yellow]{selectedBot.EscapeMarkup()}[/]...[/]");
            AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B, D[/] to detach)[/]");
            AnsiConsole.MarkupLine("[red](Ctrl+C inside attach will detach you)[/]");

            try {
                string tmuxSessionName = "automation_hub_bots"; string escapedBotName = selectedBot.Replace("\"", "\\\"");
                // Command: Masuk ke codespace -> attach/select window
                string args = $"codespace ssh --codespace \"{activeCodespace}\" -- tmux attach-session -t {tmuxSessionName} \\; select-window -t \"{escapedBotName}\"";
                // Jalankan interaktif, teruskan linked token
                await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCancellationToken);
                // Jika selesai tanpa cancel -> detach manual
                AnsiConsole.MarkupLine("\n[yellow]✓ Detached from tmux session.[/]");
            }
            // Tangkap cancel spesifik dari RunInteractiveWithFullInput (yang dilempar saat linked token cancel)
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Attach session cancelled (likely Ctrl+C).[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"\n[red]Attach error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
            // Finally block tidak dibutuhkan karena _interactiveCts di-handle di RunInteractiveMenuAsync
        }

        private static async Task ShowRemoteShellAsync(CancellationToken linkedCancellationToken)
        {
            if (linkedCancellationToken.IsCancellationRequested) return;
            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Shell").Color(Color.Magenta1));
            var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;

            if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace recorded.[/]"); Pause("Press Enter...", linkedCancellationToken); return; }

            AnsiConsole.MarkupLine($"[cyan]Opening interactive shell in [green]{activeCodespace.EscapeMarkup()}[/]...[/]");
            AnsiConsole.MarkupLine("[dim](Type [bold]exit[/] or [bold]Ctrl+D[/] to close)[/]");
            AnsiConsole.MarkupLine("[red](Ctrl+C inside shell will likely close it)[/]");

            try {
                string args = $"codespace ssh --codespace \"{activeCodespace}\"";
                // Jalankan interaktif, teruskan linked token
                await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCancellationToken);
                // Jika selesai tanpa cancel -> exit manual
                AnsiConsole.MarkupLine("\n[yellow]✓ Remote shell closed.[/]");
            }
            // Tangkap cancel spesifik dari RunInteractiveWithFullInput
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Remote shell session cancelled (likely Ctrl+C).[/]"); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"\n[red]Remote shell error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
             // Finally block tidak dibutuhkan
        }


        // --- Loop Orkestrasi Utama ---
        private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) // Ini adalah _mainCts.Token
        {
            AnsiConsole.Clear(); AnsiConsole.MarkupLine("[cyan]Starting Orchestrator Loop...[/]"); AnsiConsole.MarkupLine("[dim](Press Ctrl+C ONCE for graceful shutdown)[/]");
            const int MAX_CONSECUTIVE_ERRORS = 3; int consecutiveErrors = 0;

            while (!cancellationToken.IsCancellationRequested) { // Loop berdasarkan token utama
                TokenEntry currentToken = TokenManager.GetCurrentToken(); TokenState currentState = TokenManager.GetState(); string? activeCodespace = currentState.ActiveCodespaceName;
                var username = currentToken.Username ?? "unknown";
                AnsiConsole.MarkupLine($"\n[cyan]Token #{currentState.CurrentIndex + 1}: @{username.EscapeMarkup()}[/]");
                try {
                    // Cek Billing (pakai token utama untuk cancel check)
                    cancellationToken.ThrowIfCancellationRequested();
                    AnsiConsole.MarkupLine("Checking billing...");
                    var billingInfo = await BillingManager.GetBillingInfo(currentToken); // GetBillingInfo tidak perlu CancellationToken eksternal
                    cancellationToken.ThrowIfCancellationRequested();
                    BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");

                    // Handle Quota/Billing Error (pakai token utama untuk cancel check)
                    if (!billingInfo.IsQuotaOk) {
                        if (billingInfo.Error == BillingManager.PersistentProxyError && !_isAttemptingIpAuth) {
                            AnsiConsole.MarkupLine("[magenta]Proxy error detected. Attempting recovery...[/]");
                            _isAttemptingIpAuth = true;
                            bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(cancellationToken); // Pakai token utama
                            _isAttemptingIpAuth = false; cancellationToken.ThrowIfCancellationRequested();
                            if (ipAuthSuccess) {
                                AnsiConsole.MarkupLine("[green]IP Auth OK. Testing & Reloading...[/]");
                                await ProxyManager.RunProxyTestAndSaveAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); // Pakai token utama
                                TokenManager.ReloadProxyListAndReassign(); currentToken = TokenManager.GetCurrentToken();
                                AnsiConsole.MarkupLine("[yellow]Retrying billing check...[/]"); await Task.Delay(5000, cancellationToken); continue;
                            } else AnsiConsole.MarkupLine("[red]IP Auth failed.[/]");
                        } else if (_isAttemptingIpAuth) AnsiConsole.MarkupLine("[yellow]IP Auth in progress, skipping redundant attempt.[/]");

                        // Rotasi Token
                        AnsiConsole.MarkupLine("[yellow]Quota low/billing failed/recovery failed. Rotating token...[/]");
                        if (!string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine($"[dim]Deleting {activeCodespace.EscapeMarkup()}...[/]"); try { await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); } catch {} } // Delete tidak pakai cancel token
                        currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                        currentToken = TokenManager.SwitchToNextToken(); activeCodespace = null; consecutiveErrors = 0;
                        AnsiConsole.MarkupLine($"[cyan]Switched to token: @{(currentToken.Username ?? "unknown").EscapeMarkup()}[/]");
                        await Task.Delay(5000, cancellationToken); continue; // Ulang loop
                    }

                    // Ensure Codespace (pakai token utama)
                    cancellationToken.ThrowIfCancellationRequested(); AnsiConsole.MarkupLine("Ensuring codespace...");
                    string ensuredCodespaceName;
                    try { ensuredCodespaceName = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}", cancellationToken); } // Teruskan token utama
                    catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Codespace ensure cancelled.[/]"); throw; } // Lempar jika cancel
                    catch (Exception csEx) { AnsiConsole.MarkupLine($"\n[red]ERROR ENSURING CODESPACE[/]"); AnsiConsole.WriteException(csEx); consecutiveErrors++; AnsiConsole.MarkupLine($"\n[yellow]Errors: {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS}. Retrying after delay...[/]"); try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { throw; } continue; } // Retry loop

                    // Update State (pakai token utama untuk cancel check)
                    cancellationToken.ThrowIfCancellationRequested();
                    bool isNewOrRecreated = currentState.ActiveCodespaceName != ensuredCodespaceName;
                    currentState.ActiveCodespaceName = ensuredCodespaceName; TokenManager.SaveState(currentState); activeCodespace = ensuredCodespaceName;
                    if (isNewOrRecreated) AnsiConsole.MarkupLine($"[green]✓ New/Recreated: {activeCodespace.EscapeMarkup()}[/]"); else AnsiConsole.MarkupLine($"[green]✓ Reusing: {activeCodespace.EscapeMarkup()}[/]");
                    consecutiveErrors = 0;

                    // Sleep (bisa di-cancel token utama)
                    AnsiConsole.MarkupLine($"\n[yellow]Sleeping for {KeepAliveInterval.TotalHours:F1} hours...[/]");
                    try { await Task.Delay(KeepAliveInterval, cancellationToken); } catch (OperationCanceledException) { throw; }

                    // Keep-Alive Check (pakai token utama untuk cancel check)
                    cancellationToken.ThrowIfCancellationRequested(); currentState = TokenManager.GetState(); activeCodespace = currentState.ActiveCodespaceName;
                    if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[yellow]No active codespace after sleep. Restarting cycle.[/]"); continue; }
                    AnsiConsole.MarkupLine("\n[yellow]Performing Keep-Alive check...[/]");
                    if (!await CodespaceManager.CheckHealthWithRetry(currentToken, activeCodespace, cancellationToken)) { // Pakai token utama
                        AnsiConsole.MarkupLine("[red]Keep-alive FAILED! Resetting state...[/]"); currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); continue;
                    } else {
                        AnsiConsole.MarkupLine("[green]Health OK.[/]");
                        try { AnsiConsole.MarkupLine("[dim]Triggering keep-alive script...[/]"); await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace); AnsiConsole.MarkupLine("[green]Keep-alive triggered.[/]"); } // Tidak pakai cancel token
                        catch (Exception trigEx) { AnsiConsole.MarkupLine($"[yellow]Keep-alive trigger failed: {trigEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}. Resetting state...[/]"); currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); continue; }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { AnsiConsole.MarkupLine("\n[yellow]Orchestrator loop cancellation requested.[/]"); break; } // Keluar while
                catch (Exception ex) { // Error tak terduga
                    consecutiveErrors++; AnsiConsole.MarkupLine("\n[bold red]UNEXPECTED LOOP ERROR[/]"); AnsiConsole.WriteException(ex);
                    if (cancellationToken.IsCancellationRequested) break; // Keluar jika sudah di-cancel
                    if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                        AnsiConsole.MarkupLine($"\n[bold red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} errors! Emergency recovery...[/]");
                        if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) { try { await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); } catch {} } // Tidak pakai cancel
                        currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); currentToken = TokenManager.SwitchToNextToken(); consecutiveErrors = 0;
                        AnsiConsole.MarkupLine($"[cyan]Recovery: Switched token. Waiting 30s...[/]");
                        try { await Task.Delay(30000, cancellationToken); } catch (OperationCanceledException) { break; } // Bisa di-cancel saat delay
                    } else {
                        AnsiConsole.MarkupLine($"[yellow]Retrying loop in {ErrorRetryDelay.TotalMinutes} min... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                        try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { break; } // Bisa di-cancel saat delay
                    }
                }
            } // End while loop
            AnsiConsole.MarkupLine("\n[cyan]Orchestrator Loop Stopped.[/]");
        } // Akhir RunOrchestratorLoopAsync

        // Fungsi Pause (harus cek kedua token)
        private static void Pause(string message, CancellationToken linkedCancellationToken) // Terima linked token
        {
           // Cek token utama DAN linked token
           if (linkedCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
            Console.WriteLine(); AnsiConsole.Markup($"[dim]{message.EscapeMarkup()}[/]");
            try {
                // Loop cek key sambil cek kedua token
                while (!Console.KeyAvailable) {
                    if (linkedCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
                    Thread.Sleep(100);
                }
                Console.ReadKey(true); // Baca key jika ada
            }
            catch (InvalidOperationException) { // Handle jika console tidak interaktif
                AnsiConsole.MarkupLine("[yellow](Non-interactive, auto-continuing...)[/]");
                // Tunggu sebentar saja, bisa di-cancel oleh salah satu token
                try {
                    // Buat token gabungan untuk delay
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCancellationToken, _mainCts.Token);
                    Task.Delay(2000, combinedCts.Token).Wait(); // Tunggu pakai token gabungan
                 } catch { /* Ignored */ }
            }
        }

        // Fungsi GetMainCancellationToken (tidak berubah)
        public static CancellationToken GetMainCancellationToken() => _mainCts.Token;

    } // Akhir class Program
} // Akhir namespace Orchestrator
