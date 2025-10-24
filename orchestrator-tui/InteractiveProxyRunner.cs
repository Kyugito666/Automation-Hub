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

        await PromptAndTriggerRemote(bot, cancellationToken);
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
        
        Dictionary<string, string>? answers;
        try
        {
            var json = File.ReadAllText(answerFile);
            answers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            
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

        if (answers == null || !answers.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Answer file kosong[/]");
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

        // CRITICAL: Buat input script dengan newline setelah setiap answer
        var inputScript = string.Join("\n", answers.Values) + "\n\n\n"; // Triple newline for safety

        try
        {
            AnsiConsole.MarkupLine("[dim]Running bot with auto-answers...[/]");
            AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await RunWithPipedInput(executor, args, botPath, inputScript, cancellationToken);
            }
            else
            {
                await RunWithPipedInputLinux(executor, args, botPath, inputScript, cancellationToken);
            }

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

    private static async Task RunWithPipedInput(string executor, string args, string workingDir, string input, CancellationToken cancellationToken)
    {
        // Windows: echo input | cmd
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, input);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"type \"{tempFile}\" | {executor} {args}\"",
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

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Process exit code {process.ExitCode}[/]");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static async Task RunWithPipedInputLinux(string executor, string args, string workingDir, string input, CancellationToken cancellationToken)
    {
        // Linux: echo -e "input" | command
        var escapedInput = input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"echo -e '{escapedInput}' | {executor} {args}\"",
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

            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Process exit code {process.ExitCode}[/]");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static async Task RunManualCapture(BotEntry bot, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[yellow]Running in manual interactive mode...[/]");
        
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await ShellHelper.RunInteractiveWindows(executor, args, botPath, cancellationToken);
            }
            else
            {
                await ShellHelper.RunInteractive(executor, args, botPath, cancellationToken);
            }

            AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
            AnsiConsole.MarkupLine("[green]Manual run completed.[/]");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Manual run cancelled.[/]");
            throw;
        }
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
