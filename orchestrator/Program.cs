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

        // --- BARU: Setter untuk flag ---
        public static void SetLoopActive(bool active)
        {
            _isLoopActive = active;
        }

        public static async Task Main(string[] args)
        {
            // Handler Ctrl+C (TIDAK BERUBAH)
            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true; 
                lock (_shutdownLock)
                {
                    AnsiConsole.MarkupLine("\n[yellow]Ctrl+C detected...[/]");
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
                    if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
                    {
                        AnsiConsole.MarkupLine("[yellow]Attempting to cancel interactive operation...[/]");
                        try {
                             _interactiveCts.Cancel(); 
                        } catch (ObjectDisposedException) {
                             AnsiConsole.MarkupLine("[dim]Interactive operation already disposed.[/]");
                             _interactiveCts = null;
                             TriggerFullShutdown(); 
                        } catch (Exception ex) {
                             AnsiConsole.MarkupLine($"[red]Error cancelling interactive op: {ex.Message.EscapeMarkup()}[/]");
                             TriggerFullShutdown(); 
                        }
                    }
                    else 
                    {
                         TriggerFullShutdown(); 
                    }
                }
            }; 

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

        // --- PERUBAHAN: Logika Trigger Shutdown ---
        internal static void TriggerFullShutdown() {
             lock(_shutdownLock) {
                if (_isShuttingDown) return; 

                // 1. Set flag shutdown dulu
                _isShuttingDown = true; 
                AnsiConsole.MarkupLine("[bold red]SHUTDOWN TRIGGERED! Attempting graceful exit...[/]");
                AnsiConsole.MarkupLine("[dim](Press Ctrl+C again to force quit immediately.)[/]");

                _forceQuitRequested = false; 

                // 2. Jalankan shutdown (yang nge-cek flag _isLoopActive) 
                //    SEBELUM nge-cancel token utama
                Task.Run(async () => {
                    
                    await PerformGracefulShutdownAsync(); 
                    
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

        // --- PERUBAHAN: Logika Stop Codespace ---
        private static async Task PerformGracefulShutdownAsync()
        {
            AnsiConsole.MarkupLine("[dim]Executing graceful shutdown...[/]");
            // Baca flag SEKALI di awal
            bool shouldStopCodespace = _isLoopActive; 
            try {
                var token = TokenManager.GetCurrentToken();
                var state = TokenManager.GetState();
                var activeCodespace = state.ActiveCodespaceName;

                // Cek flagnya
                if (shouldStopCodespace && !string.IsNullOrEmpty(activeCodespace)) {
                    AnsiConsole.MarkupLine($"[yellow]Loop was active. Sending STOP command to '{activeCodespace.EscapeMarkup()}'...[/]");
                    // Panggil CodeManager.StopCodespace (yang akan kita ubah jadi no-proxy)
                    await CodeManager.StopCodespace(token, activeCodespace);
                    AnsiConsole.MarkupLine($"[green]✓ Stop command attempt finished for '{activeCodespace.EscapeMarkup()}'.[/]");
                } else {
                    AnsiConsole.MarkupLine("[yellow]Loop was not active or no codespace recorded. Skipping codespace stop.[/]");
                }
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Exception during codespace stop attempt: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
            AnsiConsole.MarkupLine("[dim]Graceful shutdown steps finished.[/]");
        }

        public static CancellationToken GetMainCancellationToken() => _mainCts.Token;

        // Fungsi Pause (TIDAK BERUBAH)
        internal static void Pause(string message, CancellationToken linkedCancellationToken) 
        {
           if (linkedCancellationToken.IsCancellationRequested || _mainCts.IsCancellationRequested) return;
            Console.WriteLine(); AnsiConsole.Markup($"[dim]{message.EscapeMarkup()}[/]");
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
        }
    } 
}
