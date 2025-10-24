using Spectre.Console;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System;

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private static readonly string PTY_HELPER_NAME = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "pty-helper.exe"
        : "pty-helper";

    private static readonly string PTY_HELPER_PATH = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        PTY_HELPER_NAME
    );

    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== PTY Proxy Mode: {bot.Name} ===[/]");

        // === FIX: Deteksi OS Windows ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AnsiConsole.MarkupLine("[yellow]Windows detected: PTY not supported. Using direct execution mode.[/]");
            await RunBotDirectMode(bot, cancellationToken);
            return;
        }

        // === Linux/macOS: Gunakan PTY Helper ===
        if (!File.Exists(PTY_HELPER_PATH))
        {
            AnsiConsole.MarkupLine($"[red]Error: PTY Helper '{PTY_HELPER_NAME}' tidak ditemukan di {PTY_HELPER_PATH}[/]");
            AnsiConsole.MarkupLine("[yellow]Pastikan lu sudah build Go helper dan .csproj sudah benar.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[yellow]Step 1: Menjalankan bot secara lokal (via PTY)...[/]");
        cancellationToken.ThrowIfCancellationRequested();

        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath)) { AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]"); return; }

        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(originalExecutor))
        {
            AnsiConsole.MarkupLine($"[red]Gagal menemukan perintah eksekusi untuk {bot.Name}[/]");
            return;
        }

        try
        {
            await RunBotInPtyMode(originalExecutor, originalArgs, botPath, cancellationToken);
            AnsiConsole.MarkupLine("[green]Local run selesai.[/]");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Capture run dibatalkan oleh user (Ctrl+C).[/]");
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error selama PTY run: {ex.Message}[/]");
            AnsiConsole.MarkupLine("[yellow]Skipping remote trigger due to error.[/]");
            return;
        }

        await PromptAndTriggerRemote(bot, new Dictionary<string, string>(), cancellationToken);
    }

    // === MODE BARU: Direct Execution (untuk Windows) ===
    private static async Task RunBotDirectMode(BotEntry bot, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[yellow]Step 1: Menjalankan bot secara lokal (Direct Mode)...[/]");
        cancellationToken.ThrowIfCancellationRequested();

        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath)) { AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]"); return; }

        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        var (executor, args) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine($"[red]Gagal menemukan perintah eksekusi untuk {bot.Name}[/]");
            return;
        }

        AnsiConsole.MarkupLine("[dim]Menjalankan bot (local direct)... (Press Ctrl+C to skip)[/]");
        AnsiConsole.MarkupLine("[dim]Jawab semua prompt. Interaksi langsung dengan bot.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        try
        {
            await ShellHelper.RunInteractiveWindows(executor, args, botPath, cancellationToken);
            AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
            AnsiConsole.MarkupLine("[green]Local run selesai.[/]");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Direct run dibatalkan oleh user (Ctrl+C).[/]");
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error selama direct run: {ex.Message}[/]");
            AnsiConsole.MarkupLine("[yellow]Skipping remote trigger due to error.[/]");
            return;
        }

        await PromptAndTriggerRemote(bot, new Dictionary<string, string>(), cancellationToken);
    }

    private static async Task RunBotInPtyMode(string executor, string args, string botPath, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[dim]Menjalankan bot (local PTY)... (Press Ctrl+C to skip)[/]");
        AnsiConsole.MarkupLine("[dim]Jawab semua prompt. Interaksi Raw (y/n) sekarang didukung.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        string ptyExecutor = PTY_HELPER_PATH;
        string ptyArgs = $"\"{executor}\" {args}";
        
        await ShellHelper.RunInteractive(ptyExecutor, ptyArgs, botPath, cancellationToken);
        
        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
    }

    // === Helper untuk Prompt & Trigger Remote (DRY) ===
    private static async Task PromptAndTriggerRemote(BotEntry bot, Dictionary<string, string> inputs, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions?[/]");
        AnsiConsole.MarkupLine("[dim]Run lokal selesai. Bot akan berjalan non-interaktif di remote.[/]");

        bool proceed = await ConfirmAsync("Lanjutkan trigger remote?", true, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;

        if (!proceed)
        {
            AnsiConsole.MarkupLine("[yellow]Remote trigger skipped.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Triggering remote job...[/]");
        await GitHubDispatcher.TriggerBotWithInputs(bot, inputs);
        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely![/]");
    }

    private static async Task<bool> ConfirmAsync(string prompt, bool defaultValue, CancellationToken cancellationToken)
    {
        AnsiConsole.Markup($"{prompt} [[y/n]] ({ (defaultValue ? "Y" : "y") }/{(defaultValue ? "n" : "N")}): ");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                AnsiConsole.WriteLine();
                if (key.Key == ConsoleKey.Y) return true;
                if (key.Key == ConsoleKey.N) return false;
                if (key.Key == ConsoleKey.Enter) return defaultValue;
                AnsiConsole.Markup($"{prompt} [[y/n]] ({ (defaultValue ? "Y" : "y") }/{(defaultValue ? "n" : "N")}): ");
            }
            try { await Task.Delay(50, cancellationToken); } catch (TaskCanceledException) { throw new OperationCanceledException(); }
        }
    }
}
