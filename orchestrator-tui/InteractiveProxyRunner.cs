using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string InputsDir = "../.bot-inputs"; // Direktori ini tidak lagi dipakai, tapi biarkan konstanta
    private const string VenvDirName = ".venv";

    // === METHOD DISEDERHANAKAN: Tidak ada lagi capture ===
    public static async Task RunLocallyAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Interactive Local Run & Remote Trigger: {bot.Name} ===[/]");
        AnsiConsole.MarkupLine("[yellow]Step 1: Running bot locally (interactive)...[/]");
        AnsiConsole.MarkupLine("[grey](Input Anda TIDAK akan disimpan. Tekan Ctrl+C untuk skip bot ini)[/]");

        cancellationToken.ThrowIfCancellationRequested();

        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath)) { /* handle error */ return; }

        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        bool cancelledDuringRun = false;
        try
        {
            // === JALANKAN BOT LANGSUNG ===
            await RunBotLocallyInteractive(botPath, bot, cancellationToken);
            // Jika selesai normal, lanjut ke trigger
        }
        catch (OperationCanceledException)
        {
            cancelledDuringRun = true;
            AnsiConsole.MarkupLine("[yellow]Local run skipped due to cancellation.[/]");
            // Jika dibatalkan, kita tidak trigger remote
            return;
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Error during local run: {ex.Message}[/]");
             AnsiConsole.MarkupLine("[yellow]Skipping remote trigger due to error.[/]");
             return; // Jangan trigger jika error
        }


        // --- Lanjutkan hanya jika TIDAK dibatalkan dan tidak error ---

        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions...[/]");
        if (!AnsiConsole.Confirm("Proceed with remote execution (bot will run headless)?", defaultValue:true)) // Default true?
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled remote trigger.[/]");
            return;
        }

        // Trigger remote tanpa input
        // Kita gunakan Dictionary kosong sebagai placeholder
        await GitHubDispatcher.TriggerBotWithInputs(bot, new Dictionary<string, string>());
        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely![/]");
    }

    // === METHOD BARU: Menjalankan bot tanpa wrapper ===
    private static async Task RunBotLocallyInteractive(string botPath, BotEntry bot, CancellationToken cancellationToken)
    {
        var absoluteBotPath = Path.GetFullPath(botPath);

        AnsiConsole.MarkupLine("[dim]Starting bot... (Press Ctrl+C to skip)[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        var (executor, args) = BotRunner.GetRunCommand(absoluteBotPath, bot.Type);

        if (string.IsNullOrEmpty(executor))
        {
             AnsiConsole.MarkupLine($"[red]✗ Tidak bisa menemukan command utama untuk {bot.Name}.[/]");
             // Throw exception agar pemanggil tahu gagal?
             throw new FileNotFoundException($"Could not find run command for {bot.Name}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Jalankan proses interaktif biasa
        await ShellHelper.RunInteractive(executor, args, absoluteBotPath, cancellationToken);
        // Exception OperationCanceledException akan dilempar jika Ctrl+C ditekan

        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
        AnsiConsole.MarkupLine("[green]Local interactive run finished.[/]"); // Pesan jika selesai normal
    }


    // === HAPUS SEMUA METHOD WRAPPER ===
    // private static async Task<Dictionary<string, string>?> RunBotInCaptureMode(...)
    // private static Dictionary<string, string> ReadAndDeleteCaptureFile(...)
    // private static async Task CreatePythonCaptureWrapper(...)
    // private static async Task CreateJavaScriptCaptureWrapper(...)
    // ===================================

} // End of class InteractiveProxyRunner
