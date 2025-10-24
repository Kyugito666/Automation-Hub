using Spectre.Console;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System;
using System.Text.Json;

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    // DIUBAH: Path ini sekarang ada di ../.bot-inputs/
    private const string BOT_ANSWERS_DIR = "../.bot-inputs";

    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Proxy Mode: {bot.Name} ===[/]");

        // DIUBAH: Gunakan .json sesuai standar CI
        var answerFile = Path.Combine(BOT_ANSWERS_DIR, $"{SanitizeBotName(bot.Name)}.json");
        bool hasAnswerFile = File.Exists(answerFile);

        if (!hasAnswerFile)
        {
            // First time: Create default answer untuk bot ini
            CreateDefaultAnswerFile(bot, answerFile);
            hasAnswerFile = File.Exists(answerFile);
        }

        if (hasAnswerFile)
        {
            AnsiConsole.MarkupLine("[yellow]Mode: AUTO-ANSWER (Using saved responses)[/]");
            await RunWithAutoAnswers(bot, answerFile, cancellationToken);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Mode: MANUAL (No answer file)[/]");
            await RunManualCapture(bot, cancellationToken);
        }

        // Jangan trigger remote jika local run di-cancel
        if (cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Local run cancelled, skipping remote trigger.[/]");
            return;
        }

        await PromptAndTriggerRemote(bot, answerFile, cancellationToken);
    }

    private static void CreateDefaultAnswerFile(BotEntry bot, string answerFile)
    {
        // Auto-generate default answers based on bot name
        var answers = new Dictionary<string, string>();

        if (bot.Name.Contains("Aster", StringComparison.OrdinalIgnoreCase))
        {
            answers["proxy_question"] = "y"; // Default: gunakan proxy
        }
        else
        {
            // Generic default
            answers["proxy_question"] = "y";
            answers["continue_question"] = "y";
        }

        try
        {
            Directory.CreateDirectory(BOT_ANSWERS_DIR);
            var json = JsonSerializer.Serialize(answers, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(answerFile, json);
            AnsiConsole.MarkupLine($"[green]✓ Created default answer file: {Path.GetFileName(answerFile)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to create answer file: {ex.Message}[/]");
        }
    }

    private static async Task RunWithAutoAnswers(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[dim]Loading saved answers...[/]");
        
        // Baca file untuk logging
        try
        {
            var json = File.ReadAllText(answerFile);
            var answers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            
            if (answers != null && answers.Any())
            {
                AnsiConsole.MarkupLine("[green]Answers loaded:[/]");
                foreach (var kv in answers)
                {
                    AnsiConsole.MarkupLine($"  [dim]{kv.Key}:[/] [yellow]{kv.Value}[/]");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error loading answers: {ex.Message}[/]");
            return;
        }

        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath)) 
        { 
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]"); 
            return; 
        }

        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        var (executor, args) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine($"[red]Gagal menemukan run command untuk {bot.Name}[/]");
            return;
        }

        try
        {
            AnsiConsole.MarkupLine("[dim]Running bot with auto-answers via PTY...[/]");
            AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

            // === PERUBAHAN UTAMA: Gunakan PTY Helper baru ===
            // Hapus RunWithPipedInput dan RunWithPipedInputLinux
            await ShellHelper.RunPtyWithScript(answerFile, executor, args, botPath, cancellationToken);

            AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
            AnsiConsole.MarkupLine("[green]Auto-answer run completed.[/]");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Run cancelled.[/]");
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
        }
    }

    // HAPUS: Method RunWithPipedInput dan RunWithPipedInputLinux
    // ...

    private static async Task RunManualCapture(BotEntry bot, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[yellow]Running in manual interactive mode via PTY...[/]");
        
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath)) 
        { 
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]"); 
            return; 
        }

        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        var (executor, args) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine($"[red]Gagal menemukan run command untuk {bot.Name}[/]");
            return;
        }

        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        try
        {
            // === PERUBAHAN UTAMA: Selalu gunakan RunInteractivePty ===
            await ShellHelper.RunInteractivePty(executor, args, botPath, cancellationToken);
            
            AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
            AnsiConsole.MarkupLine("[green]Manual run completed.[/]");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Manual run cancelled.[/]");
            throw;
        }
    }

    // Diubah: Kirim juga input yang dipakai
    private static async Task PromptAndTriggerRemote(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions?[/]");

        bool proceed = await ConfirmAsync("Lanjutkan trigger remote?", true, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;

        if (!proceed)
        {
            AnsiConsole.MarkupLine("[yellow]Remote trigger skipped.[/]");
            return;
        }
        
        // Baca input dari file jawaban untuk dikirim ke GitHub Actions
        // Ini menstandarkan input lokal dan remote
        Dictionary<string, string> capturedInputs = new();
        try
        {
            if (File.Exists(answerFile))
            {
                 var json = File.ReadAllText(answerFile);
                 capturedInputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                 AnsiConsole.MarkupLine($"[dim]Menggunakan {capturedInputs.Count} input dari {Path.GetFileName(answerFile)} untuk remote trigger...[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Gagal baca answer file, kirim input kosong: {ex.Message}[/]");
        }

        AnsiConsole.MarkupLine("[cyan]Triggering remote job...[/]");
        await GitHubDispatcher.TriggerBotWithInputs(bot, capturedInputs); // Kirim input yang sudah ditangkap
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
                if (key.Key == ConsoleKey.Y) { AnsiConsole.WriteLine("y"); return true; }
                if (key.Key == ConsoleKey.N) { AnsiConsole.WriteLine("n"); return false; }
                if (key.Key == ConsoleKey.Enter) { AnsiConsole.WriteLine(defaultValue ? "y" : "n"); return defaultValue; }
                AnsiConsole.Markup($"\n{prompt} [[y/n]] ({ (defaultValue ? "Y" : "y") }/{(defaultValue ? "n" : "N")}): ");
            }
            try { await Task.Delay(50, cancellationToken); } catch (TaskCanceledException) { throw new OperationCanceledException(); }
        }
    }

    private static string SanitizeBotName(string name)
    {
        // Ganti karakter ilegal untuk nama file
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Replace(" ", "_").Replace("-", "_");
    }
}
