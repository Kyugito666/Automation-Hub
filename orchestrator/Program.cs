using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.TUI; 
using Orchestrator.Codespace; 
using Orchestrator.Services; 
using Orchestrator.Core; 

namespace Orchestrator
{
    internal static class Program
    {
        private static CancellationTokenSource _mainCts = new CancellationTokenSource();
        private static CancellationTokenSource? _interactiveCts;
        private static volatile bool _isShuttingDown = false;
        private static volatile bool _forceQuitRequested = false;
        private static readonly object _shutdownLock = new object();

        // --- BARU: Flag untuk menandai jika Menu 1 (Loop) sedang aktif ---
        private static volatile bool _isLoopActive = false;
        // --- BARU: Flag menandakan bahwa TUI sedang meminta Enter dari user ---
        private static volatile bool _isAwaitingPause = false;


        // --- BARU: Setter untuk flag ---
        public static void SetLoopActive(bool active)
        {
            _isLoopActive = active;
        }

        public static async Task Main(string[] args)
        {
            // === PERBAIKAN: Handler Ctrl+C Cerdas (Mendukung Enter setelah Ctrl+C) ===
            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true; 
                lock (_shutdownLock)
                {
                    AnsiConsole.MarkupLine("\n[yellow]Ctrl+C detected...[/]");
                    
                    // 1. Cek spam Ctrl+C (buat force quit)
                    if (_isShuttingDown) 
                    {
                        if (_forceQuitRequested) {
                            AnsiConsole.MarkupLine("[bold red]FORCE QUIT CONFIRMED. Exiting immediately![/]");
                            Environment.Exit(1); 
                        }
                        AnsiConsole.MarkupLine("[bold red]Shutdown already in progress. Press Ctrl+C again to force quit.[/]");
                        _forceQuitRequested = true;
                        Task.Delay(3000).ContinueWith(_ => {
                             lock(_shutdownLock) { _forceQuitRequested = false; }
                             AnsiConsole.MarkupLine("[dim](Force quit flag reset)[/]");
                        });
                        return; 
                    }

                    // 2. Cek apakah kita di dalem Menu 1 (Loop)
                    if (_isLoopActive)
                    {
                        // Jika di Menu 1, sinyalkan loop untuk berhenti
                        AnsiConsole.MarkupLine("[yellow]Loop cancellation requested. Press Ctrl+C again to force stop TUI or Enter to return to menu.[/]");
                        
                        // Batalkan token interaktif (yang dipakai Menu 1)
                        if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
                        {
                            try { _interactiveCts.Cancel(); } catch (ObjectDisposedException) { _interactiveCts = null; }
                        }
                        // Jika loop sudah berhenti (atau sedang menunggu), panggil shutdown
                        else
                        {
                           TriggerFullShutdown();
                        }
                        return;
                    }

                    // 3. Cek apakah kita di dalem menu interaktif lain (Menu 2-7)
                    if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
                    {
                        AnsiConsole.MarkupLine("[yellow]Attempting to cancel interactive operation... (Returning to menu)[/]");
                        try {
                             _interactiveCts.Cancel(); 
                        } catch (ObjectDisposedException) {
                             AnsiConsole.MarkupLine("[dim]Interactive operation already disposed.[/]");
                             _interactiveCts = null;
                        } catch (Exception ex) {
                             AnsiConsole.MarkupLine($"[red]Error cancelling interactive op: {ex.Message.EscapeMarkup()}[/]");
                        }
                    }
                    // 4. Jika kita di Menu Utama (atau di Pause biasa)
                    else 
                    {
                         // === PERBAIKAN (CS0414): Baca flag _isAwaitingPause ===
                         if (_isAwaitingPause)
                         {
                             AnsiConsole.MarkupLine("[dim](Acknowledging Ctrl+C during pause...)[/]");
                         }
                         // === AKHIR PERBAIKAN ===
                        
                         // Panggil shutdown penuh (yang akan stop codespace jika _isLoopActive=true)
                         TriggerFullShutdown(); 
                    }
                }
            }; 
            // === AKHIR PERBAIKAN ===

            try {
                TokenManager.Initialize(); 
                
                if (args.Length > 0 && args[0].ToLower() == "--run") {
                    await TuiLoop.RunOrchestratorLoopAsync(_mainCts.Token); 
                } else {
                    await TuiMenus.RunInteractiveMenuAsync(_mainCts.Token); 
                }
            }
            catch (OperationCanceledException) when (_mainCts.IsCancellationRequested) { 
                AnsiConsole.MarkupLine("\n[yellow]Main application loop/menu cancelled.[/]");
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine("\n[bold red]FATAL UNHANDLED ERROR in Main:[/]");
                AnsiConsole.WriteException(ex);
            }
            finally {
                AnsiConsole.MarkupLine("\n[dim]Application shutdown sequence complete.[/]");
                await Task.Delay(500); 
            }
        } 

        // --- Logika Trigger Shutdown (DIUBAH UNTUK MEMENUHI REQUEST) ---
        internal static void TriggerFullShutdown() {
             lock(_shutdownLock) {
                if (_isShuttingDown) return; 

                // 1. Set flag shutdown dulu
                _isShuttingDown = true; 
                AnsiConsole.MarkupLine("[bold red]SHUTDOWN TRIGGERED! Attempting graceful exit...[/]");
                AnsiConsole.MarkupLine("[dim](Press Ctrl+C again to force quit immediately.)[/]");

                _forceQuitRequested = false; 

                // 2. Jalankan shutdown (yang nge-cek flag _isLoopActive) 
                Task.Run(async () => {
                    
                    // Logic: Jika loop tidak aktif, JANGAN stop codespace.
                    // Jika loop aktif, codespace akan di-stop.
                    bool shouldStopCodespace = _isLoopActive; 
                    await PerformGracefulShutdownAsync(shouldStopCodespace); 
                    
                    // 3. Baru sinyalkan aplikasi untuk mati
                    if (!_mainCts.IsCancellationRequested) {
                        AnsiConsole.MarkupLine("[yellow]Signalling main loop/menu to exit...[/]");
                        try { _mainCts.Cancel(); } catch (ObjectDisposedException) {} 
                    }
                });
             }
        }

        internal static void SetInteractiveCts(CancellationTokenSource cts)
        {
            _interactiveCts = cts;
        }

        internal static void ClearInteractiveCts()
        {
            _interactiveCts = null;
        }

        // --- Logika Stop Codespace (DIUBAH - menerima parameter shouldStop) ---
        private static async Task PerformGracefulShutdownAsync(bool shouldStopCodespace)
        {
            AnsiConsole.MarkupLine("[dim]Executing graceful shutdown...[/]");
            try {
                var token = TokenManager.GetCurrentToken();
                var state = TokenManager.GetState();
                var activeCodespace = state.ActiveCodespaceName;

                // Cek flag shouldStopCodespace
                if (shouldStopCodespace && !string.IsNullOrEmpty(activeCodespace)) {
                    AnsiConsole.MarkupLine($"[yellow]Graceful Stop: Sending STOP command to '{activeCodespace.EscapeMarkup()}'...[/]");
                    // Panggil CodeManager.StopCodespace (yang akan kita ubah jadi no-proxy)
                    await CodeManager.StopCodespace(token, activeCodespace);
                    AnsiConsole.MarkupLine($"[green]✓ Stop command attempt finished for '{activeCodespace.EscapeMarkup()}' (via SSH).[/]");
                } else if (shouldStopCodespace) {
                    AnsiConsole.MarkupLine("[yellow]Loop was active, but no active codespace recorded. Skipping codespace stop.[/]");
                } else {
                    AnsiConsole.MarkupLine("[dim]Loop was not active. Skipping codespace stop.[/]");
                }
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Exception during codespace stop attempt: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
            AnsiConsole.MarkupLine("[dim]Graceful shutdown steps finished.[/]");
        }

        public static CancellationToken GetMainCancellationToken() => _mainCts.Token;

        // Fungsi Pause (DIUBAH untuk mendukung Enter setelah Ctrl+C)
        internal static void Pause(string message, CancellationToken linkedCancellationToken) 
        {
           if (linkedCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
            Console.WriteLine(); AnsiConsole.Markup($"[dim]{message.EscapeMarkup()}[/]");
            
            // Set flag Awaiting Pause
            lock (_shutdownLock) { _isAwaitingPause = true; }
            
            try {
                while (!Console.KeyAvailable) {
                    if (linkedCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
                    Thread.Sleep(100);
                }
                Console.ReadKey(true); 
            }
            catch (InvalidOperationException) { 
                AnsiConsole.MarkupLine("[yellow](Non-interactive, auto-continuing...)[/]");
                try {
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCancellationToken, _mainCts.Token);
                    Task.Delay(2000, combinedCts.Token).Wait(); 
                 } catch { /* Ignored */ }
            }
            finally
            {
                 // Hapus flag Awaiting Pause
                 lock (_shutdownLock) { _isAwaitingPause = false; }
            }
        }
    } 
}
