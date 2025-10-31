using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.TUI; // Namespace baru untuk TuiMenus dan TuiLoop
using Orchestrator.Codespace; // Akan digunakan nanti
using Orchestrator.Services; // Akan digunakan nanti

namespace Orchestrator
{
    internal static class Program
    {
        // --- State Aplikasi Inti ---
        private static CancellationTokenSource _mainCts = new CancellationTokenSource();
        private static CancellationTokenSource? _interactiveCts;
        private static volatile bool _isShuttingDown = false;
        private static volatile bool _forceQuitRequested = false;
        private static readonly object _shutdownLock = new object();


        public static async Task Main(string[] args)
        {
            // Handler Ctrl+C (Logika tidak berubah)
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
            }; // Akhir CancelKeyPress handler

            try {
                TokenManager.Initialize(); // Inisialisasi token (file ini belum dipindah)
                
                if (args.Length > 0 && args[0].ToLower() == "--run") {
                    // Panggil TuiLoop (dari file baru)
                    await TuiLoop.RunOrchestratorLoopAsync(_mainCts.Token); 
                } else {
                    // Panggil TuiMenus (dari file baru)
                    await TuiMenus.RunInteractiveMenuAsync(_mainCts.Token); 
                }
            }
            catch (OperationCanceledException) when (_mainCts.IsCancellationRequested) { 
                AnsiConsole.MarkupLine("\n[yellow]Main application loop cancelled.[/]");
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine("\n[bold red]FATAL UNHANDLED ERROR in Main:[/]");
                AnsiConsole.WriteException(ex);
            }
            finally {
                AnsiConsole.MarkupLine("\n[dim]Application shutdown sequence complete.[/]");
                await Task.Delay(500); 
            }
        } // Akhir Main

        // --- Fungsi Shutdown (Diperlukan oleh TuiMenus) ---
        
        // Diubah ke 'internal static'
        internal static void TriggerFullShutdown() {
             lock(_shutdownLock) {
                if (_isShuttingDown) return; 

                AnsiConsole.MarkupLine("[bold red]SHUTDOWN TRIGGERED! Attempting graceful exit...[/]");
                AnsiConsole.MarkupLine("[dim](Attempting to stop active codespace if running...)[/]");
                AnsiConsole.MarkupLine("[dim](Press Ctrl+C again to force quit immediately.)[/]");

                _isShuttingDown = true; 
                _forceQuitRequested = false; 

                Task.Run(async () => {
                    await PerformGracefulShutdownAsync(); 
                    if (!_mainCts.IsCancellationRequested) {
                        AnsiConsole.MarkupLine("[yellow]Signalling main loop/menu to exit...[/]");
                        try { _mainCts.Cancel(); } catch (ObjectDisposedException) {} 
                    }
                });
             }
        }

        // Diubah ke 'internal static' agar TuiMenus bisa set
        internal static void SetInteractiveCts(CancellationTokenSource cts)
        {
            _interactiveCts = cts;
        }

        // Diubah ke 'internal static' agar TuiMenus bisa clear
        internal static void ClearInteractiveCts()
        {
            _interactiveCts = null;
        }

        // Helper shutdown (Logika tidak berubah)
        private static async Task PerformGracefulShutdownAsync()
        {
            AnsiConsole.MarkupLine("[dim]Executing graceful shutdown: Attempting to stop codespace...[/]");
            try {
                var token = TokenManager.GetCurrentToken();
                var state = TokenManager.GetState();
                var activeCodespace = state.ActiveCodespaceName;

                if (!string.IsNullOrEmpty(activeCodespace)) {
                    AnsiConsole.MarkupLine($"[yellow]Sending STOP command to codespace '{activeCodespace.EscapeMarkup()}' via gh cli...[/]");
                    // Memanggil CodeManager (dulu CodespaceManager)
                    // TODO: Ganti ini saat CodespaceManager direfactor
                    await CodespaceManager.StopCodespace(token, activeCodespace);
                    AnsiConsole.MarkupLine($"[green]✓ Stop command attempt finished for '{activeCodespace.EscapeMarkup()}'.[/]");
                } else {
                    AnsiConsole.MarkupLine("[yellow]No active codespace recorded to stop.[/]");
                }
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Exception during codespace stop attempt: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
            AnsiConsole.MarkupLine("[dim]Graceful shutdown steps finished.[/]");
        }

        // --- Fungsi Helper Global ---

        // Diubah ke 'public static' agar semua kelas bisa akses
        public static CancellationToken GetMainCancellationToken() => _mainCts.Token;

        // Pause (Diubah ke 'internal static' agar TuiMenus bisa pakai)
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
