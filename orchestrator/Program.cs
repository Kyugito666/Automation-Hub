using Spectre.Console;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core;
using Orchestrator.TUI;
using Orchestrator.Services;
using Orchestrator.Codespace;

namespace Orchestrator
{
    internal static class Program
    {
        private static readonly CancellationTokenSource _mainCts = new CancellationTokenSource();
        private static CancellationTokenSource? _interactiveCts = null;

        static async Task Main(string[] args)
        {
            Console.Title = "Automation Hub Orchestrator";
            
            AppDomain.CurrentDomain.ProcessExit += (s, e) => OnExit();
            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true; 
                HandleCtrlC();
            };

            try
            {
                await RunMainAsync(args);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("\n[red]An unhandled exception occurred in Main:[/]");
                AnsiConsole.WriteException(ex);
            }
            finally
            {
                OnExit();
            }
        }

        private static async Task RunMainAsync(string[] args)
        {
            var config = BotConfig.Load();
            if (config == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to load config/bots_config.json. Exiting.[/]");
                return;
            }

            if (args.Length > 0)
            {
                await HandleCommandLineArgs(args);
            }
            else
            {
                await TuiMenus.RunInteractiveMenuAsync(_mainCts.Token);
            }
        }

        private static async Task HandleCommandLineArgs(string[] args)
        {
            var command = args[0].ToLower();
            switch (command)
            {
                case "proxysync":
                    AnsiConsole.MarkupLine("[cyan]Running ProxySync (non-interactive)...[/]");
                    await ProxyService.DeployProxies(_mainCts.Token);
                    break;
                case "validate":
                    AnsiConsole.MarkupLine("[cyan]Validating all tokens...[/]");
                    await CollabService.ValidateAllTokens(_mainCts.Token);
                    break;
                case "loop":
                    AnsiConsole.MarkupLine("[cyan]Starting Orchestrator Loop (non-interactive)...[/]");
                    bool useProxy = args.Length > 1 && (args[1] == "--proxy" || args[1] == "-p");
                    TokenManager.SetProxyUsage(useProxy);
                    await TuiLoop.RunOrchestratorLoopAsync(_mainCts.Token);
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]Unknown command: {command}[/]");
                    AnsiConsole.MarkupLine("Available commands: proxysync, validate, loop");
                    break;
            }
        }

        private static void HandleCtrlC()
        {
            if (_interactiveCts != null && !_interactiveCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[yellow](Ctrl+C) Interactive operation cancelled. Press Ctrl+C again to exit program.[/]");
                _interactiveCts.Cancel();
            }
            else if (!_mainCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[red]MAIN SHUTDOWN REQUESTED. Cleaning up...[/]");
                _mainCts.Cancel();
            }
            else
            {
                AnsiConsole.MarkupLine("\n[red]Forcing exit...[/]");
                Environment.Exit(1);
            }
        }

        public static void TriggerFullShutdown()
        {
            if (!_mainCts.IsCancellationRequested)
            {
                _mainCts.Cancel();
            }
        }

        public static CancellationToken GetMainCancellationToken()
        {
            return _mainCts.Token;
        }

        public static void SetInteractiveCts(CancellationTokenSource cts)
        {
            _interactiveCts = cts;
        }

        public static void ClearInteractiveCts()
        {
            _interactiveCts = null;
        }

        public static void Pause(string message, CancellationToken cancellationToken)
        {
            AnsiConsole.Markup($"[dim]{message.EscapeMarkup()}[/]");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Enter) break;
                    }
                    Task.Delay(50, cancellationToken).Wait(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[yellow]Pause cancelled.[/]");
            }
            AnsiConsole.WriteLine();
        }

        private static void OnExit()
        {
            if (!_mainCts.IsCancellationRequested)
            {
                _mainCts.Cancel();
            }
            
            AnsiConsole.MarkupLine("\n[yellow]Orchestrator shutting down...[/]");

            if (TokenManager.GetState().IsCodespaceActive)
            {
                if (AnsiConsole.Confirm("[bold yellow]Do you want to stop the active codespace?[/]", false))
                {
                    var shutdownCts = new CancellationTokenSource(10000);
                    try
                    {
                        CodeManager.StopCodespace(shutdownCts.Token);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Error stopping codespace: {ex.Message.EscapeMarkup()}[/]");
                    }
                }
            }

            AnsiConsole.MarkupLine("[dim]Goodbye.[/]");
        }
    }
}
