using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices; // Tidak terpakai, bisa dihapus
using System.Text.Json; // Tidak terpakai, bisa dihapus
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator
{
    internal static class Program
    {
        private static CancellationTokenSource _mainCts = new CancellationTokenSource();
        // CancellationTokenSource untuk operasi interaktif spesifik (attach/shell/menu)
        private static CancellationTokenSource? _interactiveCts;
        private static volatile bool _isShuttingDown = false;
        private static volatile bool _forceQuitRequested = false; // Flag untuk force quit
        private static readonly object _shutdownLock = new object(); // Lock untuk sinkronisasi shutdown

        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes((3 * 60) + 30);
        private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

        public static async Task Main(string[] args)
        {
            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true; // Override default Ctrl+C behavior (terminate process)

                lock (_shutdownLock)
                {
                    AnsiConsole.MarkupLine("\n[yellow]Ctrl+C detected...[/]"); // Logging

                    // Jika shutdown sudah dimulai
                    if (_isShuttingDown)
                    {
                        if (_forceQuitRequested) {
                            AnsiConsole.MarkupLine("[bold red]FORCE QUIT CONFIRMED. Exiting immediately![/]");
                            Environment.Exit(1); // Keluar paksa
                        }
                        AnsiConsole.MarkupLine("[bold red]Shutdown already in progress. Press Ctrl+C again to force quit.[/]");
                        _forceQuitRequested = true;
                        // Reset flag setelah beberapa detik agar tidak langsung force quit di Ctrl+C berikutnya
                        Task.Delay(3000).ContinueWith(_ => {
                             lock(_shutdownLock) { _forceQuitRequested = false; }
                             AnsiConsole.MarkupLine("[dim](Force quit flag reset)[/]");
                        });
                        return;
                    }

                    // Jika ada operasi interaktif (attach/shell/proxy menu) sedang berjalan
                    if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
                    {
                        AnsiConsole.MarkupLine("[yellow]Attempting to cancel interactive operation (like attach/shell)...[/]");
                        try {
                             _interactiveCts.Cancel(); // Kirim sinyal cancel ke operasi interaktif
                        } catch (ObjectDisposedException) {
                             AnsiConsole.MarkupLine("[dim]Interactive operation already finished or disposed.[/]");
                             _interactiveCts = null; // Bersihkan jika sudah disposed
                             // Jika interactiveCts null, coba trigger shutdown utama
                             TriggerFullShutdown();
                        } catch (Exception ex) {
                             AnsiConsole.MarkupLine($"[red]Error cancelling interactive op: {ex.Message}[/]");
                             // Jika cancel gagal, mungkin perlu shutdown utama
                             TriggerFullShutdown();
                        }
                    }
                    // Jika tidak ada operasi interaktif atau _interactiveCts null/sudah cancel
                    else
                    {
                         TriggerFullShutdown();
                    }
                }
            };

            try {
                TokenManager.Initialize();
                // Jalankan loop atau menu berdasarkan argumen
                if (args.Length > 0 && args[0].ToLower() == "--run") {
                    await RunOrchestratorLoopAsync(_mainCts.Token);
                } else {
                    await RunInteractiveMenuAsync(_mainCts.Token);
                }
            }
            // Tangkap cancellation HANYA dari token utama
            catch (OperationCanceledException) when (_mainCts.IsCancellationRequested) {
                AnsiConsole.MarkupLine("\n[yellow]Main application loop cancelled.[/]");
            }
            catch (Exception ex) {
                AnsiConsole.MarkupLine("\n[bold red]FATAL UNHANDLED ERROR in Main:[/]");
                AnsiConsole.WriteException(ex);
            }
            finally {
                AnsiConsole.MarkupLine("\n[dim]Application shutdown sequence complete.[/]");
                // Beri waktu sedikit untuk log terakhir tampil sebelum console window nutup
                await Task.Delay(500);
            }
        }

        // Fungsi helper untuk memicu shutdown TUI
        private static void TriggerFullShutdown() {
             lock(_shutdownLock) {
                if (_isShuttingDown) return; // Hindari trigger ganda

                AnsiConsole.MarkupLine("[bold red]SHUTDOWN TRIGGERED! Attempting graceful exit...[/]");
                AnsiConsole.MarkupLine("[dim](Attempting to stop active codespace if running. This might take up to 2 minutes.)[/]");
                AnsiConsole.MarkupLine("[dim](Press Ctrl+C again to force quit immediately.)[/]");

                _isShuttingDown = true; // Tandai shutdown sedang berlangsung
                _forceQuitRequested = false; // Reset flag force quit saat memulai shutdown baru

                // Jalankan shutdown di background agar handler Ctrl+C bisa selesai cepat
                Task.Run(async () => {
                    await PerformGracefulShutdownAsync(); // Coba stop codespace
                    // Setelah shutdown selesai (atau gagal), cancel token utama untuk menghentikan loop/menu
                    if (!_mainCts.IsCancellationRequested) {
                        AnsiConsole.MarkupLine("[yellow]Signalling main loop/menu to exit...[/]");
                        try { _mainCts.Cancel(); } catch (ObjectDisposedException) {}
                    }
                });
             }
        }


        // Fungsi shutdown (coba stop codespace)
        private static async Task PerformGracefulShutdownAsync()
        {
            AnsiConsole.MarkupLine("[dim]Executing graceful shutdown: Attempting to stop codespace...[/]");
            try {
                // Ambil info token dan codespace terakhir yang diketahui
                var token = TokenManager.GetCurrentToken(); // Ambil token saat ini
                var state = TokenManager.GetState();
                var activeCodespace = state.ActiveCodespaceName;

                if (!string.IsNullOrEmpty(activeCodespace)) {
                    AnsiConsole.MarkupLine($"[yellow]Sending STOP command to codespace '{activeCodespace}' via gh cli...[/]");
                    // Panggil CodespaceManager.StopCodespace (yang menjalankan 'gh codespace stop')
                    // Fungsi ini punya timeout sendiri dan logging internal
                    await CodespaceManager.StopCodespace(token, activeCodespace);
                    AnsiConsole.MarkupLine($"[green]✓ Stop command attempt finished for '{activeCodespace}'.[/]");
                } else {
                    AnsiConsole.MarkupLine("[yellow]No active codespace recorded in state file to stop.[/]");
                }
            } catch (Exception ex) {
                // Tangkap error jika gagal stop (misal token invalid, network error)
                AnsiConsole.MarkupLine($"[red]Exception during codespace stop attempt: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
            AnsiConsole.MarkupLine("[dim]Graceful shutdown steps finished.[/]");
        }

        // --- RunInteractiveMenuAsync dan sub-menu lainnya ---
        // Perlu penyesuaian di catch block OperationCanceledException

        private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested) {
                // --- Tampilan Menu (Tidak Berubah) ---
                AnsiConsole.Clear();
                AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
                AnsiConsole.MarkupLine("[dim]Local Control, Remote Execution[/]");

                var selection = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("\n[bold white]MAIN MENU[/]")
                        .PageSize(9) // Tambah satu pilihan
                        .WrapAround(true)
                        .AddChoices(new[] {
                            "1. Start/Manage Codespace Runner (Continuous Loop)",
                            "2. Token & Collaborator Management",
                            "3. Proxy Management (Local TUI Proxy)",
                            "4. Attach to Bot Session (Remote Tmux)",
                            "5. Migrasi Kredensial Lokal (Jalankan 1x)",
                            "6. Open Remote Shell (Codespace)",
                            "7. Delete All GitHub Secrets (Fix 200KB Error)", // Opsi baru
                            "0. Exit"
                        }));

                var choice = selection[0].ToString();
                _interactiveCts = new CancellationTokenSource(); // Buat CTS baru untuk operasi menu ini
                using var linkedCtsMenu = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, cancellationToken); // Gabungkan

                try {
                    switch (choice) {
                        case "1":
                            // Loop utama butuh token utama, bukan interactive
                            await RunOrchestratorLoopAsync(cancellationToken);
                            // Jika loop di-cancel (karena shutdown TUI), keluar dari menu
                            if (cancellationToken.IsCancellationRequested) return;
                            break; // Kembali ke menu jika loop selesai normal (jarang terjadi)
                        case "2": await ShowSetupMenuAsync(linkedCtsMenu.Token); break;
                        case "3": await ShowLocalMenuAsync(linkedCtsMenu.Token); break;
                        case "4": await ShowAttachMenuAsync(linkedCtsMenu.Token); break;
                        case "5": await CredentialMigrator.RunMigration(linkedCtsMenu.Token); Pause("Tekan Enter...", linkedCtsMenu.Token); break;
                        case "6": await ShowRemoteShellAsync(linkedCtsMenu.Token); break;
                        case "7": // Panggil fungsi Delete All Secrets
                            await SecretCleanup.DeleteAllSecrets(); // Tidak perlu CancellationToken? (Fungsi ini sync?) Jika async, pakai linkedCtsMenu.Token
                            Pause("Tekan Enter...", linkedCtsMenu.Token);
                            break;
                        case "0":
                            AnsiConsole.MarkupLine("Exiting...");
                            TriggerFullShutdown(); // Panggil shutdown TUI saat milih exit
                            return; // Keluar dari loop menu
                    }
                }
                // === PERBAIKAN CATCH BLOCK ===
                catch (OperationCanceledException) {
                    // Cek token mana yang di-cancel
                    if (cancellationToken.IsCancellationRequested) {
                        AnsiConsole.MarkupLine("\n[yellow]Main application shutdown requested during menu operation.[/]");
                        return; // Keluar dari loop menu jika TUI shutdown
                    } else if (_interactiveCts?.IsCancellationRequested == true) {
                        AnsiConsole.MarkupLine("\n[yellow]Menu operation cancelled by user (Ctrl+C).[/]");
                        Pause("Press Enter to return to menu...", CancellationToken.None); // Pause tanpa cancel token
                    } else {
                        AnsiConsole.MarkupLine("\n[yellow]Menu operation cancelled unexpectedly.[/]");
                        Pause("Press Enter...", CancellationToken.None);
                    }
                }
                // === AKHIR PERBAIKAN CATCH BLOCK ===
                catch (Exception ex) {
                    AnsiConsole.MarkupLine($"[red]Error in menu operation: {ex.Message.EscapeMarkup()}[/]");
                    AnsiConsole.WriteException(ex);
                    Pause("Press Enter...", CancellationToken.None);
                }
                finally {
                    _interactiveCts?.Dispose(); // Dispose CTS setelah operasi selesai/cancel
                    _interactiveCts = null;
                }
            } // End while menu
        }

        // --- Sub-menu (ShowSetupMenuAsync, ShowLocalMenuAsync) ---
        // Menggunakan linkedCts yang diteruskan dari RunInteractiveMenuAsync

         private static async Task ShowSetupMenuAsync(CancellationToken linkedCancellationToken) {
             // Parameter sekarang adalah linkedCancellationToken
            while (!linkedCancellationToken.IsCancellationRequested) { // Gunakan linked token
                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Color(Color.Yellow));
                 var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                         .Title("\n[bold white]TOKEN & COLLABORATOR[/]").PageSize(10).WrapAround(true)
                         .AddChoices(new[] { "1. Validate Tokens", "2. Invite Collaborators", "3. Accept Invitations", "4. Show Status", "0. Back" }));
                 var sel = selection[0].ToString(); if (sel == "0") return; // Kembali ke menu utama

                 // Tidak perlu buat linked CTS lagi di sini
                 try {
                     switch (sel) {
                         // Teruskan linkedCancellationToken ke fungsi-fungsi ini
                         case "1": await CollaboratorManager.ValidateAllTokens(linkedCancellationToken); break;
                         case "2": await CollaboratorManager.InviteCollaborators(linkedCancellationToken); break;
                         case "3": await CollaboratorManager.AcceptInvitations(linkedCancellationToken); break;
                         case "4": await Task.Run(() => TokenManager.ShowStatus(), linkedCancellationToken); break;
                     }
                     Pause("Press Enter...", linkedCancellationToken); // Pause dengan linked token
                 } catch (OperationCanceledException) {
                     // Jika linked token di-cancel (dari Ctrl+C di menu utama atau operasi ini)
                     AnsiConsole.MarkupLine("\n[yellow]Setup operation cancelled.[/]");
                     // Jangan pause, langsung kembali ke loop menu (yang akan cek token)
                     return;
                 } catch (Exception ex) {
                      AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
                      Pause("Press Enter...", CancellationToken.None); // Pause tanpa cancel token
                 }
            }
         }

         private static async Task ShowLocalMenuAsync(CancellationToken linkedCancellationToken) {
             // Parameter sekarang adalah linkedCancellationToken
             while (!linkedCancellationToken.IsCancellationRequested) { // Gunakan linked token
                 AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Proxy").Color(Color.Green));
                 var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                         .Title("\n[bold white]LOCAL PROXY MGMT[/]").PageSize(10).WrapAround(true)
                         .AddChoices(new[] { "1. Run ProxySync (Interactive)", "0. Back" }));
                 var sel = selection[0].ToString(); if (sel == "0") return; // Kembali ke menu utama

                 // Tidak perlu buat linked CTS lagi
                 try {
                    // Teruskan linkedCancellationToken
                    if (sel == "1") await ProxyManager.DeployProxies(linkedCancellationToken);
                    Pause("Press Enter...", linkedCancellationToken); // Pause dengan linked token
                 } catch (OperationCanceledException) {
                     AnsiConsole.MarkupLine("\n[yellow]ProxySync operation cancelled.[/]");
                     // Jangan pause, langsung kembali
                     return;
                 } catch (Exception ex) {
                     AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
                     Pause("Press Enter...", CancellationToken.None);
                 }
            }
        }

        // --- Attach & Shell Menu ---
        // Menggunakan linkedCts dari RunInteractiveMenuAsync

         private static async Task ShowAttachMenuAsync(CancellationToken linkedCancellationToken) {
            // Parameter adalah linkedCancellationToken dari menu utama
            if (linkedCancellationToken.IsCancellationRequested) return; // Cek di awal

            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Attach").Color(Color.Blue));
            var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;
            if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace recorded in state.[/]"); Pause("Press Enter...", linkedCancellationToken); return; }

            AnsiConsole.MarkupLine($"[dim]Checking active codespace: [blue]{activeCodespace.EscapeMarkup()}[/][/]");
            List<string> sessions;
            try {
                 // Gunakan linkedCancellationToken untuk fetch sessions
                 sessions = await CodespaceManager.GetTmuxSessions(currentToken, activeCodespace);
                 // Cek cancel *setelah* await selesai
                 linkedCancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Fetching sessions cancelled.[/]"); return; }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetching tmux sessions: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); return; }

            if (!sessions.Any()) { AnsiConsole.MarkupLine("[yellow]No running bot sessions found in tmux.[/]"); Pause("Press Enter...", linkedCancellationToken); return; }
            var backOption = "[ << Back ]";
            sessions.Insert(0, backOption);

            // Prompt TIDAK bisa di-cancel langsung dengan CancellationToken,
            // tapi Ctrl+C di sini akan trigger CancelKeyPress handler utama
            var selectedBot = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Attach to bot session (in [green]{activeCodespace.EscapeMarkup()}[/]):")
                    .PageSize(15)
                    .WrapAround(true)
                    .AddChoices(sessions)
            );

            // Cek cancel *setelah* Prompt selesai
            if (selectedBot == backOption || linkedCancellationToken.IsCancellationRequested) return;

            AnsiConsole.MarkupLine($"\n[cyan]Attempting to attach to tmux window [yellow]{selectedBot.EscapeMarkup()}[/].[/]");
            AnsiConsole.MarkupLine("[dim](Use [bold]Ctrl+B[/] then [bold]D[/] to detach from tmux window)[/]");
            AnsiConsole.MarkupLine("[red](Use Ctrl+C carefully inside attach. It will likely detach you.)[/]"); // Update pesan

            // TIDAK perlu buat _interactiveCts baru di sini, kita gunakan yang dari RunInteractiveMenuAsync
            // Cukup teruskan linkedCancellationToken yang sudah ada
            try {
                string tmuxSessionName = "automation_hub_bots";
                string escapedBotName = selectedBot.Replace("\"", "\\\""); // Escape quotes
                // Argumen untuk attach ke sesi dan langsung select window
                string args = $"codespace ssh --codespace \"{activeCodespace}\" -- tmux attach-session -t {tmuxSessionName} \\; select-window -t \"{escapedBotName}\"";

                // Panggil RunInteractiveWithFullInput dengan linkedCancellationToken
                await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCancellationToken);
                // Jika selesai tanpa cancel, berarti user detach manual (Ctrl+B D) atau bot selesai
                AnsiConsole.MarkupLine("\n[yellow]✓ Detached from tmux session.[/]");

            } catch (OperationCanceledException) {
                // Tangkap cancel dari linkedCancellationToken (yang berasal dari _interactiveCts atau _mainCts)
                AnsiConsole.MarkupLine("\n[yellow]Attach session cancelled (Ctrl+C detected).[/]");
                // Tidak perlu throw lagi, kembali ke menu
            } catch (Exception ex) {
                 AnsiConsole.MarkupLine($"\n[red]Attach error: {ex.Message.EscapeMarkup()}[/]");
                 Pause("Press Enter...", CancellationToken.None);
            }
            // finally block tidak perlu karena _interactiveCts dikelola oleh RunInteractiveMenuAsync
        }

        private static async Task ShowRemoteShellAsync(CancellationToken linkedCancellationToken)
        {
            // Parameter adalah linkedCancellationToken dari menu utama
            if (linkedCancellationToken.IsCancellationRequested) return;

            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Shell").Color(Color.Magenta1));
            var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;

            if (string.IsNullOrEmpty(activeCodespace)) {
                AnsiConsole.MarkupLine("[red]No active codespace recorded in state.[/]");
                AnsiConsole.MarkupLine("[dim]Please start the codespace first (Menu 1).[/]");
                Pause("Press Enter...", linkedCancellationToken); return;
            }

            AnsiConsole.MarkupLine($"[cyan]Attempting to open interactive shell in [green]{activeCodespace.EscapeMarkup()}[/]...[/]");
            AnsiConsole.MarkupLine("[dim](Type [bold]exit[/] or press [bold]Ctrl+D[/] to close the shell)[/]");
            AnsiConsole.MarkupLine("[red](Use Ctrl+C carefully inside shell. It will likely close the shell.)[/]"); // Update pesan

            // Gunakan linkedCancellationToken yang diteruskan
            try
            {
                string args = $"codespace ssh --codespace \"{activeCodespace}\"";
                await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCancellationToken);
                // Jika selesai tanpa cancel, berarti user exit manual
                AnsiConsole.MarkupLine("\n[yellow]✓ Remote shell closed.[/]");

            } catch (OperationCanceledException) {
                // Tangkap cancel dari linkedCancellationToken
                AnsiConsole.MarkupLine("\n[yellow]Remote shell session cancelled (Ctrl+C detected).[/]");
                // Kembali ke menu
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"\n[red]Remote shell error: {ex.Message.EscapeMarkup()}[/]");
                Pause("Press Enter...", CancellationToken.None);
            }
            // finally tidak perlu
        }


        // --- RunOrchestratorLoopAsync ---
        // Menggunakan CancellationToken utama (_mainCts.Token)

        private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken)
        {
            AnsiConsole.Clear(); AnsiConsole.MarkupLine("[cyan]Starting Orchestrator Loop (Continuous Mode)...[/]");
            AnsiConsole.MarkupLine("[dim](Press Ctrl+C ONCE to initiate graceful shutdown)[/]");
            const int MAX_CONSECUTIVE_ERRORS = 3; int consecutiveErrors = 0;

            // Loop jalan terus sampai token utama di-cancel
            while (!cancellationToken.IsCancellationRequested) {
                TokenEntry currentToken = TokenManager.GetCurrentToken(); TokenState currentState = TokenManager.GetState(); string? activeCodespace = currentState.ActiveCodespaceName;
                var username = currentToken.Username ?? "unknown";
                AnsiConsole.MarkupLine($"\n[cyan]Token #{currentState.CurrentIndex + 1}: @{username.EscapeMarkup()}[/]");
                try {
                    // --- Billing Check ---
                    cancellationToken.ThrowIfCancellationRequested(); // Cek cancel sebelum network call
                    AnsiConsole.MarkupLine("Checking billing...");
                    var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                    cancellationToken.ThrowIfCancellationRequested(); // Cek cancel setelah network call
                    BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");

                    // --- Penanganan Quota Rendah / Billing Error ---
                    if (!billingInfo.IsQuotaOk) {
                        // Coba IP Auth jika error proxy
                        if (billingInfo.Error == BillingManager.PersistentProxyError && !_isAttemptingIpAuth) {
                            AnsiConsole.MarkupLine("[magenta]Proxy error detected. Attempting IP Auth & Proxy Test...[/]");
                            _isAttemptingIpAuth = true; // Tandai IP Auth dimulai
                            bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (ipAuthSuccess) {
                                AnsiConsole.MarkupLine("[green]IP Auth OK. Testing & Reloading proxies...[/]");
                                await ProxyManager.RunProxyTestAndSaveAsync(cancellationToken); // Test & Save
                                cancellationToken.ThrowIfCancellationRequested();
                                TokenManager.ReloadProxyListAndReassign(); // Reload & Reassign
                                currentToken = TokenManager.GetCurrentToken(); // Ambil token lagi (mungkin proxy baru)
                                _isAttemptingIpAuth = false; // Tandai IP Auth selesai
                                AnsiConsole.MarkupLine("[yellow]Retrying billing check with potentially new proxy...[/]");
                                await Task.Delay(5000, cancellationToken); continue; // Ulangi loop untuk cek billing lagi
                            } else {
                                AnsiConsole.MarkupLine("[red]IP Auth failed.[/]");
                                _isAttemptingIpAuth = false; // Tandai IP Auth selesai (gagal)
                                // Biarkan jatuh ke logika rotasi token
                            }
                        } else if (_isAttemptingIpAuth) {
                            AnsiConsole.MarkupLine("[yellow]IP Auth already in progress, skipping additional attempt.[/]");
                            // Biarkan jatuh ke logika rotasi token
                        }

                        // Jika quota rendah ATAU billing check gagal (dan IP Auth gagal/tidak dicoba) -> Rotasi Token
                        AnsiConsole.MarkupLine("[yellow]Quota low or billing check failed. Rotating token...[/]");
                        if (!string.IsNullOrEmpty(activeCodespace)) { // Coba delete CS sebelum rotasi
                            AnsiConsole.MarkupLine($"[dim]Attempting to delete codespace {activeCodespace.EscapeMarkup()} before rotating...[/]");
                            try { await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); } catch {}
                        }
                        currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); // Hapus nama CS dari state
                        currentToken = TokenManager.SwitchToNextToken(); // Ganti token
                        activeCodespace = null; // Reset variabel lokal
                        consecutiveErrors = 0; // Reset error count
                        AnsiConsole.MarkupLine($"[cyan]Switched to token: @{(currentToken.Username ?? "unknown").EscapeMarkup()}[/]");
                        await Task.Delay(5000, cancellationToken); continue; // Ulangi loop dengan token baru
                    } // End if (!billingInfo.IsQuotaOk)

                    // --- Ensure Codespace ---
                    cancellationToken.ThrowIfCancellationRequested();
                    AnsiConsole.MarkupLine("Ensuring codespace is healthy...");
                    string ensuredCodespaceName;
                    try {
                        // EnsureHealthyCodespace butuh token utama, bukan interactive
                        ensuredCodespaceName = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}", cancellationToken);
                    } catch (OperationCanceledException) {
                        // Jika EnsureHealthyCodespace di-cancel oleh token utama, throw lagi
                        AnsiConsole.MarkupLine("\n[yellow]Codespace operation cancelled by shutdown request.[/]");
                        throw; // Biarkan catch utama di luar loop menangkap ini
                    } catch (Exception csEx) {
                        // Tangani error saat ensure codespace (quota habis, API error, dll)
                        AnsiConsole.MarkupLine($"\n[red]━━━ ERROR ENSURING CODESPACE ━━━[/]");
                        AnsiConsole.WriteException(csEx);
                        consecutiveErrors++;
                        AnsiConsole.MarkupLine($"\n[yellow]Consecutive errors: {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS}. Retrying after delay...[/]");
                        // Tunggu sebelum retry (bisa di-cancel)
                        try { await Task.Delay(ErrorRetryDelay, cancellationToken); }
                        catch (OperationCanceledException) { throw; } // Jika di-cancel saat delay, throw
                        continue; // Ulangi loop untuk coba ensure lagi
                    }

                    // --- Update State & Log ---
                    cancellationToken.ThrowIfCancellationRequested();
                    bool isNewOrRecreated = currentState.ActiveCodespaceName != ensuredCodespaceName;
                    currentState.ActiveCodespaceName = ensuredCodespaceName;
                    TokenManager.SaveState(currentState); // Simpan nama CS yang aktif
                    activeCodespace = ensuredCodespaceName; // Update variabel lokal

                    if (isNewOrRecreated) { AnsiConsole.MarkupLine($"[green]✓ New/Recreated codespace active: {activeCodespace.EscapeMarkup()}[/]"); }
                    else { AnsiConsole.MarkupLine($"[green]✓ Reusing existing codespace: {activeCodespace.EscapeMarkup()}[/]"); }

                    consecutiveErrors = 0; // Reset error count jika ensure sukses

                    // --- Sleep Interval ---
                    AnsiConsole.MarkupLine($"\n[yellow]Sleeping for {KeepAliveInterval.TotalHours:F1} hours...[/]");
                    try { await Task.Delay(KeepAliveInterval, cancellationToken); }
                    catch (OperationCanceledException) { throw; } // Jika di-cancel saat sleep, throw

                    // --- Keep-Alive Check ---
                    cancellationToken.ThrowIfCancellationRequested();
                    // Ambil state terbaru (mungkin sudah diubah oleh shutdown handler)
                    currentState = TokenManager.GetState();
                    activeCodespace = currentState.ActiveCodespaceName;
                    if (string.IsNullOrEmpty(activeCodespace)) {
                        AnsiConsole.MarkupLine("[yellow]No active codespace recorded after sleep (possibly stopped by shutdown). Restarting check cycle.[/]");
                        continue; // Ulangi loop
                    }

                    AnsiConsole.MarkupLine("\n[yellow]Performing Keep-Alive check...[/]");
                    // Check health butuh token utama
                    if (!await CodespaceManager.CheckHealthWithRetry(currentToken, activeCodespace, cancellationToken)) {
                        AnsiConsole.MarkupLine("[red]Keep-alive health check FAILED! Codespace might be broken. Resetting state...[/]");
                        currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                        // Jangan delete codespace di sini, biarkan EnsureHealthy di loop berikutnya yang handle
                        continue; // Ulangi loop, EnsureHealthy akan deteksi state buruk/kosong
                    } else {
                        AnsiConsole.MarkupLine("[green]Health check OK.[/]");
                        try { // Coba trigger keep-alive script
                            AnsiConsole.MarkupLine("[dim]Triggering keep-alive script...[/]");
                            // Trigger butuh token utama
                            await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                            AnsiConsole.MarkupLine("[green]Keep-alive triggered.[/]");
                        } catch (Exception trigEx) {
                            AnsiConsole.MarkupLine($"[yellow]Keep-alive trigger failed: {trigEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}. Assuming codespace issue, resetting state...[/]");
                            currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                            continue; // Ulangi loop
                        }
                    } // End keep-alive check

                } // End try block loop utama
                // Tangkap cancellation HANYA dari token utama
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    AnsiConsole.MarkupLine("\n[yellow]Orchestrator loop cancellation requested.[/]");
                    break; // Keluar dari loop while
                }
                catch (Exception ex) { // Tangkap error tak terduga dalam loop
                    consecutiveErrors++;
                    AnsiConsole.MarkupLine("\n[bold red]━━━ UNEXPECTED LOOP ERROR ━━━[/]");
                    AnsiConsole.WriteException(ex);

                    if (cancellationToken.IsCancellationRequested) break; // Cek lagi jika error terjadi saat shutdown

                    // --- Emergency Recovery ---
                    if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                        AnsiConsole.MarkupLine($"\n[bold red]CRITICAL: Reached {MAX_CONSECUTIVE_ERRORS} consecutive errors![/]");
                        AnsiConsole.MarkupLine("[yellow]Performing emergency recovery: Rotating token + Force deleting codespace...[/]");
                        if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) {
                            try { await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); } catch {}
                        }
                        currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                        currentToken = TokenManager.SwitchToNextToken();
                        consecutiveErrors = 0; // Reset error count setelah recovery
                        AnsiConsole.MarkupLine($"[cyan]Recovery: Switched to token @{(currentToken.Username ?? "unknown").EscapeMarkup()}. Waiting 30s...[/]");
                        try { await Task.Delay(30000, cancellationToken); } catch (OperationCanceledException) { break; } // Bisa di-cancel saat delay recovery
                    } else { // Jika belum max error, delay biasa
                        AnsiConsole.MarkupLine($"[yellow]Retrying loop in {ErrorRetryDelay.TotalMinutes} minutes... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                        try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { break; } // Bisa di-cancel saat delay error
                    }
                } // End catch unexpected loop error
            } // End while loop utama

            AnsiConsole.MarkupLine("\n[cyan]Orchestrator Loop Stopped.[/]");
        } // End RunOrchestratorLoopAsync

         // Fungsi Pause (tidak perlu diubah)
         private static void Pause(string message, CancellationToken cancellationToken)
        {
           // Cek token utama karena pause bisa dipanggil dari mana saja
           if (cancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
            Console.WriteLine(); AnsiConsole.Markup($"[dim]{message}[/]");
            try {
                // Loop cek KeyAvailable dan CancellationToken
                while (!Console.KeyAvailable) {
                    // Cek kedua token
                    if (cancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
                    Thread.Sleep(100); // Tunggu sebentar
                }
                Console.ReadKey(true); // Baca key jika tersedia
            }
            catch (InvalidOperationException) { // Handle jika console tidak interaktif
                AnsiConsole.MarkupLine("[yellow](Non-interactive console detected, auto-continuing...)[/]");
                try { Task.Delay(2000, cancellationToken).Wait(); } catch { /* Ignored */ }
            }
        }

        // Fungsi GetMainCancellationToken (tidak berubah)
        public static CancellationToken GetMainCancellationToken() => _mainCts.Token;

    } // Akhir class Program
} // Akhir namespace Orchestrator
