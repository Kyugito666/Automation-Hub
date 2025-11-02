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
            // === PERBAIKAN: Handler Ctrl+C Disederhanakan ===
            // (Tidak ada lagi logic shutdown codespace)
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
                    
                    _isShuttingDown = true;
                    _forceQuitRequested = false;

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

        // === PERBAIKAN: Trigger Shutdown Disederhanakan ===
        internal static void TriggerFullShutdown() {
             lock(_shutdownLock) {
                if (!_mainCts.IsCancellationRequested) {
                    AnsiConsole.MarkupLine("[yellow]Signalling main loop/menu to exit...[/]");
                    try { 
                        // Cukup cancel token utama, JANGAN panggil PerformGracefulShutdownAsync
                        _mainCts.Cancel(); 
                    } catch (ObjectDisposedException) {} 
                }
             }
        }
        // === AKHIR PERBAIKAN ===

        internal static void SetInteractiveCts(CancellationTokenSource cts)
        {
            _interactiveCts = cts;
        }

        internal static void ClearInteractiveCts()
        {
            _interactiveCts = null;
        }

        // === PERBAIKAN: Fungsi Stop Codespace DIKOSONGKAN ===
        private static async Task PerformGracefulShutdownAsync()
        {
            AnsiConsole.MarkupLine("[dim]Graceful shutdown (Stop Codespace) is DISABLED.[/]");
            // JANGAN LAKUKAN APA-APA. Biarkan codespace hidup.
            await Task.CompletedTask;
        }
        // === AKHIR PERBAIKAN ===

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
