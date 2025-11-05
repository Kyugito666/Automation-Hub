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
        private static volatile bool _isLoopActive = false;
        private static volatile bool _forceQuitRequested = false;
        private static readonly object _shutdownLock = new object();

        public static void SetLoopActive(bool active)
        {
            _isLoopActive = active;
        }

        public static async Task Main(string[] args)
        {
            Console.CancelKeyPress += (sender, e) => {
                e.Cancel = true; 
                lock (_shutdownLock)
                {
                    if (_isShuttingDown) {
                        AnsiConsole.MarkupLine("[bold red]FORCE QUIT CONFIRMED. Exiting immediately![/]");
                        Environment.Exit(1); 
                        return;
                    }
        
                    if (_isLoopActive) 
                    {
                        if (_forceQuitRequested)
                        {
                            AnsiConsole.MarkupLine("\n[bold red]FORCE SHUTDOWN TRIGGERED! Stopping codespace...[/]");
                            _isShuttingDown = true;
                            TriggerFullShutdown(true); 
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("\n[yellow]Loop cancellation requested. (Press Ctrl+C again to force quit & stop codespace)[/]");
                            _forceQuitRequested = true;
                            
                            if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
                            {
                                try { _interactiveCts.Cancel(); } catch { }
                            }
                            
                            Task.Delay(3000).ContinueWith(_ => {
                                 lock(_shutdownLock) { _forceQuitRequested = false; }
                            });
                        }
                        return;
                    }
        
                    if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
                    {
                        AnsiConsole.MarkupLine("\n[yellow]Menu operation cancelled. Returning to main menu...[/]");
                        try { _interactiveCts.Cancel(); } catch { }
                    } 
                    else 
                    {
                        AnsiConsole.MarkupLine("\n[bold red]SHUTDOWN TRIGGERED! (Exiting TUI only, codespace left running)[/]");
                        _isShuttingDown = true;
                        TriggerFullShutdown(false);
                    }
                }
            }; 

            try {
                TokenManager.LoadState(); 
                
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

        internal static void TriggerFullShutdown(bool stopCodespace) {
             lock(_shutdownLock) {
                if (_isShuttingDown && !stopCodespace) return; 
                _isShuttingDown = true; 

                AnsiConsole.MarkupLine(stopCodespace 
                    ? "[dim]Executing graceful shutdown (TUI + Codespace Stop)...[/]" 
                    : "[dim]Executing graceful shutdown (TUI Only)...[/]");

                Task.Run(async () => {
                    await PerformGracefulShutdownAsync(stopCodespace); 
                    
                    if (!_mainCts.IsCancellationRequested) {
                        try { _mainCts.Cancel(); } catch { } 
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

        private static async Task PerformGracefulShutdownAsync(bool shouldStopCodespace)
        {
            AnsiConsole.MarkupLine("[dim]Executing graceful shutdown...[/]");
            try {
                var token = TokenManager.GetCurrentToken();
                var state = TokenManager.GetState();
                var activeCodespace = state.ActiveCodespaceName;

                if (shouldStopCodespace && !string.IsNullOrEmpty(activeCodespace)) {
                    AnsiConsole.MarkupLine($"[yellow]Graceful Stop: Sending STOP command to '{activeCodespace.EscapeMarkup()}'...[/]");
                    await CodeManager.StopCodespace(token, activeCodespace);
                    AnsiConsole.MarkupLine($"[green]✓ Stop command attempt finished for '{activeCodespace.EscapeMarkup()}' (via SSH).[/]");
                } else if (shouldStopCodespace) {
                    AnsiConsole.MarkupLine("[yellow]Loop was active, but no active codespace recorded. Skipping codespace stop.[/]");
                } else {
                    AnsiConsole.MarkupLine("[dim]Loop was not active (or force stop not requested). Skipping codespace stop.[/]");
                }
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Exception during codespace stop attempt: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
            }
            AnsiConsole.MarkupLine("[dim]Graceful shutdown steps finished.[/]");
        }

        public static CancellationToken GetMainCancellationToken() => _mainCts.Token;

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
                 } catch { }
            }
        }
    } 
}
