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
    private const string BOT_ANSWERS_DIR = "../config/bot-answers";

    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Proxy Mode: {bot.Name} ===[/]");

        // Deteksi mode: Auto-Answer atau Manual
        var answerFile = Path.Combine(BOT_ANSWERS_DIR, $"{SanitizeBotName(bot.Name)}.json");
        bool hasAnswerFile = File.Exists(answerFile);

        if (hasAnswerFile)
        {
            AnsiConsole.MarkupLine("[yellow]Mode: AUTO-ANSWER (Using saved responses)[/]");
            await RunWithAutoAnswers(bot, answerFile, cancellationToken);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Mode: MANUAL CAPTURE (First-time setup)[/]");
            await RunManualCapture(bot, answerFile, cancellationToken);
        }

        await PromptAndTriggerRemote(bot, cancellationToken);
    }

    private static async Task RunWithAutoAnswers(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[dim]Loading saved answers...[/]");
        
        Dictionary<string, string>? answers;
        try
        {
            var json = File.ReadAllText(answerFile);
            answers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error loading answers: {ex.Message}[/]");
            return;
        }

        if (answers == null || !answers.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Answer file kosong, switching to manual mode...[/]");
            await RunManualCapture(bot, answerFile, cancellationToken);
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

        // Buat answer script
        var answerScriptPath = Path.Combine(botPath, ".auto-answer.tmp");
        CreateAnswerScript(answers, answerScriptPath);

        try
        {
            AnsiConsole.MarkupLine("[dim]Running bot with auto-answers...[/]");
            AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

            // Jalankan dengan input redirection
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await RunWithInputFile(executor, args, botPath, answerScriptPath, cancellationToken);
            }
            else
            {
                await RunWithInputFileLinux(executor, args, botPath, answerScriptPath, cancellationToken);
            }

            AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
            AnsiConsole.MarkupLine("[green]Auto-answer run completed.[/]");
        }
        finally
        {
            // Cleanup temp file
            try { if (File.Exists(answerScriptPath)) File.Delete(answerScriptPath); } catch { }
        }
    }

    private static void CreateAnswerScript(Dictionary<string, string> answers, string outputPath)
    {
        // Buat script yang jawab semua prompt secara otomatis
        var lines = new List<string>();
        
        foreach (var answer in answers.Values)
        {
            // Untuk prompt y/n, langsung kasih jawaban + newline
            lines.Add(answer);
        }

        // Tambah newline ekstra untuk safety
        lines.Add("");
        lines.Add("");

        File.WriteAllLines(outputPath, lines);
    }

    private static async Task RunWithInputFile(string executor, string args, string workingDir, string inputFile, CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"type \"{inputFile}\" | {executor} {args}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.OutputDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[grey]{e.Data.EscapeMarkup()}[/]");
        };
        process.ErrorDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[yellow]{e.Data.EscapeMarkup()}[/]");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
    }

    private static async Task RunWithInputFileLinux(string executor, string args, string workingDir, string inputFile, CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"cat '{inputFile}' | {executor} {args}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.OutputDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[grey]{e.Data.EscapeMarkup()}[/]");
        };
        process.ErrorDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                AnsiConsole.MarkupLineInterpolated($"[yellow]{e.Data.EscapeMarkup()}[/]");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
    }

    private static async Task RunManualCapture(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[yellow]Step 1: Menjalankan bot secara lokal (MANUAL MODE)...[/]");
        AnsiConsole.MarkupLine("[dim]Jawab semua prompt. Jawaban akan disimpan untuk run berikutnya.[/]");
        
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
            // Run interaktif biasa
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await ShellHelper.RunInteractiveWindows(executor, args, botPath, cancellationToken);
            }
            else
            {
                await ShellHelper.RunInteractive(executor, args, botPath, cancellationToken);
            }

            AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
            AnsiConsole.MarkupLine("[green]Local run selesai.[/]");

            // Prompt untuk save answers
            if (AnsiConsole.Confirm("\n[yellow]Save your answers for future auto-runs?[/]", true))
            {
                await CaptureAnswersInteractive(bot, answerFile);
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Manual run dibatalkan.[/]");
            throw;
        }
    }

    private static async Task CaptureAnswersInteractive(BotEntry bot, string answerFile)
    {
        AnsiConsole.MarkupLine("\n[cyan]Configure Auto-Answers for future runs:[/]");
        
        var answers = new Dictionary<string, string>();

        // Deteksi bot type untuk preset questions
        if (bot.Name.Contains("Aster", StringComparison.OrdinalIgnoreCase))
        {
            // Bot Aster punya prompt: "Do You Want Use Proxy? (y/n):"
            var proxyAnswer = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Answer for 'Do You Want Use Proxy?'")
                    .AddChoices("y", "n"));
            answers["proxy_question"] = proxyAnswer;
        }

        // Generic: tanya user untuk custom prompts
        while (AnsiConsole.Confirm("Add custom prompt answer?", false))
        {
            var promptName = AnsiConsole.Ask<string>("Prompt identifier (e.g., 'use_proxy'):");
            var answer = AnsiConsole.Ask<string>("Answer (e.g., 'y' or 'n'):");
            answers[promptName] = answer;
        }

        if (answers.Any())
        {
            Directory.CreateDirectory(BOT_ANSWERS_DIR);
            var json = JsonSerializer.Serialize(answers, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(answerFile, json);
            AnsiConsole.MarkupLine($"[green]✓ Answers saved to {Path.GetFileName(answerFile)}[/]");
        }

        await Task.CompletedTask;
    }

    private static async Task PromptAndTriggerRemote(BotEntry bot, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions?[/]");

        bool proceed = await ConfirmAsync("Lanjutkan trigger remote?", true, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;

        if (!proceed)
        {
            AnsiConsole.MarkupLine("[yellow]Remote trigger skipped.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Triggering remote job...[/]");
        await GitHubDispatcher.TriggerBotWithInputs(bot, new Dictionary<string, string>());
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

    private static string SanitizeBotName(string name)
    {
        return name.Replace(" ", "_").Replace("-", "_").Replace("/", "_");
    }
}
