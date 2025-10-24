using Spectre.Console;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices; // Untuk OSPlatform
using System.Diagnostics; // Untuk Process
using System; // Untuk AppDomain

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    // Nama helper Go yang kita compile
    private static readonly string PTY_HELPER_NAME = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "pty-helper.exe"
        : "pty-helper";

    // Path ke PTY helper
    // (AppDomain.CurrentDomain.BaseDirectory menunjuk ke 'bin/Debug/net8.0/')
    private static readonly string PTY_HELPER_PATH = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        PTY_HELPER_NAME
    );

    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== PTY Proxy Mode: {bot.Name} ===[/]");

        // Cek apakah .csproj sudah "auto" copy helper-nya
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

        // 1. Install deps dulu
        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Dapatkan perintah asli (misal: "python", "run.py")
        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(originalExecutor))
        {
            AnsiConsole.MarkupLine($"[red]Gagal menemukan perintah eksekusi untuk {bot.Name}[/]");
            return;
        }

        try
        {
            // 3. Jalankan bot di dalam PTY Helper
            await RunBotInPtyMode(originalExecutor, originalArgs, botPath, cancellationToken);
            AnsiConsole.MarkupLine("[green]Local run selesai.[/]");
        }
        catch (OperationCanceledException)
        {
            // Tangkap Ctrl+C
            AnsiConsole.MarkupLine("[yellow]Capture run dibatalkan oleh user (Ctrl+C).[/]");
            throw; // Lempar ulang agar Program.cs bisa menangkap
        }
        catch (Exception ex) // Termasuk Exception dari ShellHelper jika ExitCode != 0
        {
            AnsiConsole.MarkupLine($"[red]Error selama PTY run: {ex.Message}[/]");
            AnsiConsole.MarkupLine("[yellow]Skipping remote trigger due to error.[/]");
            return; // Penting: Jangan lanjut ke Step 2 jika helper gagal
        }

        // --- Lanjutkan ke Step 2 (Trigger) ---
        // (Hanya jalan jika bot selesai NORMAL)

        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions?[/]");
        AnsiConsole.MarkupLine("[dim]Run lokal selesai. Bot akan berjalan non-interaktif di remote.[/]");

        bool proceed = await ConfirmAsync("Lanjutkan trigger remote?", true, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return; // Cek jika user cancel saat konfirmasi

        if (!proceed)
        {
            AnsiConsole.MarkupLine("[yellow]Remote trigger skipped.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Triggering remote job...[/]");

        // Kita kirim input kosong, karena PTY tidak mem-parsing input.
        // GitHub Action *harus* support jalan non-interaktif (headless).
        var emptyInputs = new Dictionary<string, string>();

        // Panggil GitHubDispatcher seperti biasa
        await GitHubDispatcher.TriggerBotWithInputs(bot, emptyInputs);
        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely![/]");
    }

    private static async Task RunBotInPtyMode(string executor, string args, string botPath, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[dim]Menjalankan bot (local PTY)... (Press Ctrl+C to skip)[/]");
        AnsiConsole.MarkupLine("[dim]Jawab semua prompt. Interaksi Raw (y/n) sekarang didukung.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        // Perintah kita sekarang adalah:
        // "D:\...net8.0\pty-helper.exe" "python" "run.py"
        string ptyExecutor = PTY_HELPER_PATH;
        
        // Gabungkan executor asli dan args-nya, pastikan path/args dengan spasi di-quote
        string ptyArgs = $"\"{executor}\" {args}"; // ShellHelper akan handle parsing
        
        // Panggil ShellHelper.RunInteractive, yang sudah support CancellationToken DAN throw Exception on failure
        await ShellHelper.RunInteractive(ptyExecutor, ptyArgs, botPath, cancellationToken);
        
        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
    }

    // Fungsi helper konfirmasi (Copy-paste dari C# lama lu, tidak berubah)
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
