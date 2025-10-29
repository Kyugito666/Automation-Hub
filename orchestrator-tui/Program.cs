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
    
    // === PERBAIKAN: Flag untuk menandai shutdown sedang berjalan ===
    private static volatile bool _isShuttingDown = false; 
    // === AKHIR PERBAIKAN ===

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes((3 * 60) + 30);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task Main(string[] args)
    {
        // === PERBAIKAN: Handler Ctrl+C yang lebih Blocking ===
        // Deklarasikan variabel di luar handler agar bisa diakses
        bool forceQuitRequested = false;
        object shutdownLock = new object(); // Untuk mencegah double execution

        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true; // Selalu cegah exit default

            lock (shutdownLock) // Pastikan hanya satu thread yang memproses Ctrl+C
            {
                if (_isShuttingDown) // Shutdown sudah berjalan
                {
                    if (forceQuitRequested) // Ctrl+C kedua (atau ketiga)
                    {
                        AnsiConsole.MarkupLine("\n[red]Force quit confirmed. Exiting immediately...[/]");
                        Environment.Exit(1); // Hard exit
                    }
                    AnsiConsole.MarkupLine("\n[red]Shutdown already in progress. Press Ctrl+C again to force quit.[/]");
                    forceQuitRequested = true;
                    // Reset flag force quit setelah beberapa detik
                    Task.Delay(3000).ContinueWith(_ => forceQuitRequested = false); 
                    return; // Jangan lakukan apa-apa lagi jika shutdown sudah berjalan
                }

                // --- Logika Ctrl+C Pertama ---
                
                if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested) // Sedang dalam mode interaktif (Attach)
                {
                    AnsiConsole.MarkupLine("\n[yellow]Ctrl+C: Stopping interactive operation...[/]");
                    _interactiveCts.Cancel(); // Batalkan hanya operasi interaktif
                    // Tidak perlu force quit flag di sini, biarkan user Ctrl+C lagi jika mau force
                }
                else // Berada di Menu Utama atau Loop Menu 1 (atau operasi non-interaktif lainnya)
                {
                    AnsiConsole.MarkupLine("\n[red]SHUTDOWN TRIGGERED! Attempting to stop remote codespace...[/]");
                    AnsiConsole.MarkupLine("[dim](This might take up to 2 minutes. Press Ctrl+C again to force quit.)[/]");
                    
                    _isShuttingDown = true; // Tandai bahwa shutdown sedang berlangsung
                    forceQuitRequested = true; // Aktifkan mode force quit untuk Ctrl+C berikutnya
                    Task.Delay(3000).ContinueWith(_ => forceQuitRequested = false); // Reset force quit flag

                    // JALANKAN SHUTDOWN SECARA SINCRONOUS (Tunggu di sini)
                    try
                    {
                        // Panggil dan TUNGGU (Wait) sampai selesai atau timeout internalnya tercapai.
                        // Kita tidak butuh CancellationToken khusus karena kita ingin ini selesai.
                        PerformGracefulShutdownAsync().Wait(); 
                    }
                    catch (Exception shutdownEx)
                    {
                        AnsiConsole.MarkupLine($"[red]Error during graceful shutdown attempt: {shutdownEx.Message.Split('\n').FirstOrDefault()}[/]");
                        // Tetap lanjut untuk cancel loop utama
                    }
                    finally
                    {
                        // SETELAH mencoba stop, baru batalkan loop utama
                        if (!_mainCts.IsCancellationRequested)
                        {
                             AnsiConsole.MarkupLine("[yellow]Signalling main loop to exit...[/]");
                            _mainCts.Cancel();
                        }
                    }
                }
            } // Akhir lock
        };
        // === AKHIR PERBAIKAN ===
        
        try {
            TokenManager.Initialize();
            // Jalankan loop/menu seperti biasa
            if (args.Length > 0 && args[0].ToLower() == "--run") { await RunOrchestratorLoopAsync(_mainCts.Token); }
            else { await RunInteractiveMenuAsync(_mainCts.Token); }
        } 
        // Tangkap OperationCanceledException HANYA jika dari _mainCts (bukan dari Ctrl+C langsung)
        catch (OperationCanceledException) when (_mainCts.IsCancellationRequested) 
        { 
            AnsiConsole.MarkupLine("\n[yellow]Main loop cancelled.[/]"); 
        }
        catch (Exception ex) 
        { 
            AnsiConsole.MarkupLine("\n[red]FATAL ERROR:[/]"); 
            AnsiConsole.WriteException(ex); 
        }
        finally 
        {
            // Tidak perlu wait lagi di sini karena sudah ditunggu di handler Ctrl+C
            AnsiConsole.MarkupLine("\n[dim]Application shutdown complete.[/]"); 
        }
    }

    // Fungsi ini sekarang dipanggil dan ditunggu oleh handler Ctrl+C
    private static async Task PerformGracefulShutdownAsync()
    {
        AnsiConsole.MarkupLine("[dim]Executing graceful shutdown steps...[/]");
        try
        {
            var token = TokenManager.GetCurrentToken();
            var state = TokenManager.GetState();
            var activeCodespace = state.ActiveCodespaceName;

            if (!string.IsNullOrEmpty(activeCodespace))
            {
                AnsiConsole.MarkupLine($"[yellow]Sending STOP command to codespace '{activeCodespace}'...[/]");
                
                // Panggil fungsi public dari CodespaceManager. 
                // Fungsi ini punya timeout internal 120 detik (STOP_TIMEOUT_MS)
                await CodespaceManager.StopCodespace(token, activeCodespace); 
                
                // Kita tidak tahu pasti apakah stop berhasil atau tidak dari return value,
                // tapi setidaknya perintah sudah dikirim. Log dari StopCodespace akan muncul.
                AnsiConsole.MarkupLine($"[green]✓ Stop command attempt finished for '{activeCodespace}'.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No active codespace recorded in state file to stop.[/]");
            }
        }
        catch (Exception ex)
        {
            // Tangkap dan log error jika terjadi saat mencoba stop
            AnsiConsole.MarkupLine($"[red]Exception during codespace stop attempt: {ex.Message.Split('\n').FirstOrDefault()}[/]");
            // Jangan lempar ulang error, biarkan aplikasi tetap mencoba exit
        }
        AnsiConsole.MarkupLine("[dim]Graceful shutdown steps finished.[/]");
    }

    // --- Sisa fungsi (RunInteractiveMenuAsync, Show..., RunOrchestratorLoopAsync, Pause) tidak berubah ---
    // --- Salin sisa fungsi dari versi sebelumnya ke sini ---
    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
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
                        "4. Attach to Bot Session (Remote)",
                        "5. Migrasi Kredensial Lokal (Jalankan 1x)", 
                        "0. Exit"
                    }));

            var choice = selection[0].ToString();
            try {
                switch (choice) {
                    case "1": await RunOrchestratorLoopAsync(cancellationToken); if (cancellationToken.IsCancellationRequested) return; break;
                    case "2": await ShowSetupMenuAsync(cancellationToken); break;
                    case "3": await ShowLocalMenuAsync(cancellationToken); break;
                    case "4": await ShowAttachMenuAsync(cancellationToken); break;
                    case "5": await CredentialMigrator.RunMigration(cancellationToken); Pause("Tekan Enter...", cancellationToken); break; 
                    case "0": AnsiConsole.MarkupLine("Exiting..."); _mainCts.Cancel(); return; // Langsung cancel jika pilih exit
                }
            } 
            // Tangkap cancel dari sub-menu (misal Ctrl+C di dalam migrasi)
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_mainCts.IsCancellationRequested) 
            { AnsiConsole.MarkupLine("\n[yellow]Sub-menu operation cancelled.[/]"); Pause("Press Enter...", CancellationToken.None); }
            // Tangkap cancel dari handler Ctrl+C utama
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) 
            { AnsiConsole.MarkupLine("\n[yellow]Main operation cancelled.[/]"); return; } // Keluar dari menu
            catch (Exception ex) 
            { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); AnsiConsole.WriteException(ex); Pause("Press Enter...", CancellationToken.None); }
        }
    }

     private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        // Cek cancel di awal loop
        while (!cancellationToken.IsCancellationRequested && !_mainCts.IsCancellationRequested) {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Color(Color.Yellow));
             var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                     .Title("\n[bold white]TOKEN & COLLABORATOR[/]").PageSize(10).WrapAround(true)
                     .AddChoices(new[] { "1. Validate Tokens", "2. Invite Collaborators", "3. Accept Invitations", "4. Show Status", "0. Back" }));
             var sel = selection[0].ToString(); if (sel == "0") return;
             
             // Gunakan linked token untuk operasi di dalam menu
             using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mainCts.Token);
             try {
                 switch (sel) {
                     case "1": await CollaboratorManager.ValidateAllTokens(linkedCts.Token); break;
                     case "2": await CollaboratorManager.InviteCollaborators(linkedCts.Token); break;
                     case "3": await CollaboratorManager.AcceptInvitations(linkedCts.Token); break;
                     case "4": await Task.Run(() => TokenManager.ShowStatus(), linkedCts.Token); break;
                 }
                 Pause("Press Enter...", linkedCts.Token);
             } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); if (_mainCts.IsCancellationRequested) return; // Keluar jika main cancel
             } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        }
     }

     private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
         while (!cancellationToken.IsCancellationRequested && !_mainCts.IsCancellationRequested) {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Proxy").Color(Color.Green));
             var selection = AnsiConsole.Prompt( new SelectionPrompt<string>()
                     .Title("\n[bold white]LOCAL PROXY MGMT[/]").PageSize(10).WrapAround(true)
                     .AddChoices(new[] { "1. Run ProxySync (Interactive)", "0. Back" }));
             var sel = selection[0].ToString(); if (sel == "0") return;

             using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mainCts.Token);
             try {
                if (sel == "1") await ProxyManager.DeployProxies(linkedCts.Token);
                Pause("Press Enter...", linkedCts.Token);
             } catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); if (_mainCts.IsCancellationRequested) return;
             } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        }
    }

     private static async Task ShowAttachMenuAsync(CancellationToken mainCancellationToken) {
        if (mainCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
        AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Attach").Color(Color.Blue));
        var currentToken = TokenManager.GetCurrentToken(); var state = TokenManager.GetState(); var activeCodespace = state.ActiveCodespaceName;
        if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[red]No active codespace.[/]"); Pause("Press Enter...", mainCancellationToken); return; }
        List<string> sessions;
        
        using var linkedCtsFetch = CancellationTokenSource.CreateLinkedTokenSource(mainCancellationToken, _mainCts.Token);
        try { sessions = await CodespaceManager.GetTmuxSessions(currentToken, activeCodespace); } // GetTmuxSessions tidak butuh CancellationToken
        catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Fetching sessions cancelled.[/]"); return; } // Seharusnya tidak terjadi
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error fetching sessions: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); return; }
        
        if (!sessions.Any()) { AnsiConsole.MarkupLine("[yellow]No running bot sessions found.[/]"); Pause("Press Enter...", mainCancellationToken); return; }
        var backOption = "[ (Back) ]"; sessions.Add(backOption);
        var selectedBot = AnsiConsole.Prompt( new SelectionPrompt<string>().Title($"Attach to (in [green]{activeCodespace}[/]):").PageSize(15).WrapAround(true).AddChoices(sessions) );
        if (selectedBot == backOption || mainCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;

        AnsiConsole.MarkupLine($"\n[cyan]Attaching to [yellow]{selectedBot}[/].[/]"); AnsiConsole.MarkupLine("[dim](Ctrl+B, D to detach)[/]"); AnsiConsole.MarkupLine("[red]⚠ Use Ctrl+C carefully inside attach. TWICE might force quit TUI.[/]");
        
        _interactiveCts = new CancellationTokenSource();
        // Gabungkan token interaktif dengan token utama
        using var linkedCtsAttach = CancellationTokenSource.CreateLinkedTokenSource(_interactiveCts.Token, mainCancellationToken, _mainCts.Token);
        try {
            string tmuxSessionName = "automation_hub_bots"; string escapedBotName = selectedBot.Replace("\"", "\\\"");
            // Perintah tmux yang benar untuk attach ATAU create/select window
            string args = $"codespace ssh --codespace {activeCodespace} -- tmux new-session -A -s {tmuxSessionName} \\; select-window -t \\\"{escapedBotName}\\\"";
            // Jalankan interaktif
            await ShellHelper.RunInteractiveWithFullInput("gh", args, null, currentToken, linkedCtsAttach.Token);
        } catch (OperationCanceledException) {
            if (_interactiveCts?.IsCancellationRequested == true) AnsiConsole.MarkupLine("\n[yellow]✓ Detached / Interactive operation cancelled.[/]");
            else if (mainCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) AnsiConsole.MarkupLine("\n[yellow]Main application cancelled during attach.[/]");
            else AnsiConsole.MarkupLine("\n[yellow]Attach operation cancelled unexpectedly.[/]");
        } catch (Exception ex) { AnsiConsole.MarkupLine($"\n[red]Attach error: {ex.Message.EscapeMarkup()}[/]"); Pause("Press Enter...", CancellationToken.None); }
        finally { _interactiveCts?.Dispose(); _interactiveCts = null; }
    }


    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Clear(); AnsiConsole.MarkupLine("[cyan]Loop Started[/]");
        const int MAX_CONSECUTIVE_ERRORS = 3; int consecutiveErrors = 0;
        // Gunakan token dari _mainCts di sini
        while (!cancellationToken.IsCancellationRequested) { 
            TokenEntry currentToken = TokenManager.GetCurrentToken(); TokenState currentState = TokenManager.GetState(); string? activeCodespace = currentState.ActiveCodespaceName;
            var username = currentToken.Username ?? "unknown";
            AnsiConsole.MarkupLine($"\n[cyan]Token #{currentState.CurrentIndex + 1}: @{username}[/]");
            try {
                // Periksa pembatalan SEBELUM operasi network
                cancellationToken.ThrowIfCancellationRequested(); 
                AnsiConsole.MarkupLine("Checking billing...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken); // GetBillingInfo tidak perlu CancellationToken
                
                cancellationToken.ThrowIfCancellationRequested(); 
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");
                
                if (!billingInfo.IsQuotaOk) {
                    if (billingInfo.Error == BillingManager.PersistentProxyError) {
                        AnsiConsole.MarkupLine("[magenta]Proxy error detected during billing. Attempting IP Auth...[/]");
                        // Gunakan token utama untuk operasi recovery
                        bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (ipAuthSuccess) {
                            AnsiConsole.MarkupLine("[green]IP Auth OK. Testing proxies...[/]");
                            bool testSuccess = await ProxyManager.RunProxyTestAndSaveAsync(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if(testSuccess) {
                                AnsiConsole.MarkupLine("[green]Test OK. Reloading proxies...[/]");
                                TokenManager.ReloadProxyListAndReassign();
                                currentToken = TokenManager.GetCurrentToken(); // Ambil token lagi (mungkin proxy berubah)
                                AnsiConsole.MarkupLine("[yellow]Retrying billing check with potentially new proxy...[/]");
                                await Task.Delay(5000, cancellationToken); continue; // Ulangi loop
                            } else { AnsiConsole.MarkupLine("[red]Proxy test failed after IP Auth.[/]"); }
                        } else { AnsiConsole.MarkupLine("[red]IP Auth failed.[/]"); }
                    }
                    // Jika IP Auth gagal atau kuota memang habis
                    AnsiConsole.MarkupLine("[yellow]Quota low or billing check failed. Rotating token...[/]");
                    if (!string.IsNullOrEmpty(activeCodespace)) { 
                        AnsiConsole.MarkupLine($"[dim]Attempting to delete codespace {activeCodespace} before rotating...[/]");
                        // Jangan pakai CancellationToken di sini, biarkan delete berjalan
                        try { await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); } catch {} 
                    }
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState);
                    currentToken = TokenManager.SwitchToNextToken(); activeCodespace = null; consecutiveErrors = 0;
                    AnsiConsole.MarkupLine($"[cyan]Switched to token: @{currentToken.Username ?? "unknown"}[/]");
                    await Task.Delay(5000, cancellationToken); continue; // Ulangi loop dengan token baru
                }
                
                // --- Jika Billing OK ---
                cancellationToken.ThrowIfCancellationRequested(); 
                AnsiConsole.MarkupLine("Ensuring codespace is healthy...");
                
                try {
                    // EnsureHealthyCodespace akan menangani pembatalan internalnya
                    activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken, $"{currentToken.Owner}/{currentToken.Repo}", cancellationToken);
                } catch (OperationCanceledException) {
                    AnsiConsole.MarkupLine("\n[yellow]Codespace operation cancelled by user during ensure process.[/]");
                    throw; // Biarkan Main menangkap ini
                } catch (Exception csEx) {
                    AnsiConsole.MarkupLine($"\n[red]━━━ ERROR ENSURING CODESPACE ━━━[/]");
                    AnsiConsole.WriteException(csEx);
                    consecutiveErrors++;
                    AnsiConsole.MarkupLine($"\n[yellow]Consecutive errors: {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS}[/]");
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        AnsiConsole.MarkupLine("\n[yellow]Press Enter to retry or Ctrl+C to abort...[/]");
                        // Beri kesempatan user cancel sebelum retry
                        var readTask = Task.Run(() => Console.ReadLine());
                        var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
                        await Task.WhenAny(readTask, cancelTask);
                        cancellationToken.ThrowIfCancellationRequested(); // Jika user Ctrl+C saat menunggu Enter
                    }
                    continue; // Retry loop
                }
                
                // Jika EnsureHealthyCodespace berhasil atau tidak dibatalkan
                cancellationToken.ThrowIfCancellationRequested();
                bool isNew = currentState.ActiveCodespaceName != activeCodespace; 
                currentState.ActiveCodespaceName = activeCodespace; // Update state
                TokenManager.SaveState(currentState); // Simpan state BARU
                if (isNew) { AnsiConsole.MarkupLine($"[green]✓ New/Recreated codespace active: {activeCodespace}[/]"); } 
                else { AnsiConsole.MarkupLine($"[green]✓ Reusing existing codespace: {activeCodespace}[/]"); }
                
                consecutiveErrors = 0; // Reset error count on success

                AnsiConsole.MarkupLine($"\n[yellow]Sleeping for {KeepAliveInterval.TotalHours:F1} hours...[/]");
                await Task.Delay(KeepAliveInterval, cancellationToken); // Tunggu di sini, bisa di-cancel
                
                // --- Setelah Bangun Tidur ---
                cancellationToken.ThrowIfCancellationRequested(); 
                // Baca ulang state jika token dirotasi saat tidur (meski tidak mungkin)
                currentState = TokenManager.GetState(); 
                activeCodespace = currentState.ActiveCodespaceName; 
                if (string.IsNullOrEmpty(activeCodespace)) { 
                    AnsiConsole.MarkupLine("[yellow]No active codespace recorded after sleep. Restarting check cycle.[/]"); 
                    continue; // Mulai dari awal loop
                }

                AnsiConsole.MarkupLine("\n[yellow]Performing Keep-Alive check...[/]");
                // CheckHealthWithRetry akan menangani pembatalan internal
                if (!await CodespaceManager.CheckHealthWithRetry(currentToken, activeCodespace, cancellationToken)) {
                    AnsiConsole.MarkupLine("[red]Keep-alive health check FAILED! Codespace might be broken. Resetting...[/]"); 
                    currentState.ActiveCodespaceName = null; 
                    TokenManager.SaveState(currentState); 
                    continue; // Mulai dari awal loop untuk recreate
                } else {
                    AnsiConsole.MarkupLine("[green]Health check OK.[/]");
                    try { 
                        AnsiConsole.MarkupLine("[dim]Triggering keep-alive script...[/]"); 
                        // TriggerStartupScript tidak perlu CancellationToken
                        await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace); 
                        AnsiConsole.MarkupLine("[green]Keep-alive triggered.[/]"); 
                    }
                    catch (Exception trigEx) { 
                        // Jika trigger gagal, anggap codespace bermasalah
                        AnsiConsole.MarkupLine($"[yellow]Keep-alive trigger failed: {trigEx.Message.Split('\n').FirstOrDefault()}. Resetting...[/]"); 
                        currentState.ActiveCodespaceName = null; 
                        TokenManager.SaveState(currentState); 
                        continue; // Mulai dari awal loop untuk recreate
                    }
                }
                // Jika semua OK, loop akan berulang
            }
            // Tangkap cancel yang spesifik untuk loop ini
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) 
            { 
                AnsiConsole.MarkupLine("\n[yellow]Orchestrator loop cancelled.[/]"); 
                break; // Keluar dari while loop
            }
            // Tangkap error umum dalam satu iterasi loop
            catch (Exception ex) {
                consecutiveErrors++; 
                AnsiConsole.MarkupLine("\n[red]━━━ UNEXPECTED LOOP ERROR ━━━[/]"); 
                AnsiConsole.WriteException(ex);
                
                // Periksa pembatalan SEBELUM logic recovery
                if (cancellationToken.IsCancellationRequested) break;

                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS) {
                    AnsiConsole.MarkupLine($"\n[red]CRITICAL: Reached {MAX_CONSECUTIVE_ERRORS} consecutive errors![/]"); 
                    AnsiConsole.MarkupLine("[yellow]Performing emergency recovery: Rotating token + Force deleting codespace...[/]");
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName)) { 
                        // Jangan pakai CancellationToken saat delete darurat
                        try { await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName); } catch {} 
                    }
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); 
                    currentToken = TokenManager.SwitchToNextToken(); 
                    consecutiveErrors = 0; // Reset error count
                    AnsiConsole.MarkupLine($"[cyan]Recovery: Switched to token @{currentToken.Username ?? "unknown"}. Waiting 30s...[/]");
                    try { await Task.Delay(30000, cancellationToken); } catch (OperationCanceledException) { break; } // Bisa di-cancel saat delay
                } else {
                    AnsiConsole.MarkupLine($"[yellow]Retrying loop in {ErrorRetryDelay.TotalMinutes} minutes... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})[/]");
                    try { await Task.Delay(ErrorRetryDelay, cancellationToken); } catch (OperationCanceledException) { break; } // Bisa di-cancel saat delay
                }
            }
        } // Akhir while loop
        AnsiConsole.MarkupLine("\n[cyan]Orchestrator Loop Stopped[/]");
    }

     private static void Pause(string message, CancellationToken cancellationToken)
    {
       // Cek cancel di awal
       if (cancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return; 
        Console.WriteLine(); AnsiConsole.Markup($"[dim]{message}[/]");
        try { 
            // Loop cek key sambil cek cancel
            while (!Console.KeyAvailable) { 
                if (cancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return; 
                Thread.Sleep(100); 
            } 
            Console.ReadKey(true); // Baca key jika ada
        }
        // Handle jika console tidak interaktif (misal redirect output)
        catch (InvalidOperationException) { 
            AnsiConsole.MarkupLine("[yellow](Non-interactive console detected, auto-continuing...)[/]"); 
            // Tunggu sebentar saja, bisa di-cancel
            try { Task.Delay(2000, cancellationToken).Wait(); } catch { } 
        }
    }
} // Akhir class Program
