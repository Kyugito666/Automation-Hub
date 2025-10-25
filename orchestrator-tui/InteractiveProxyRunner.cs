using Spectre.Console;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Text.Json;
using System.Linq;

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string BOT_ANSWERS_DIR = "../.bot-inputs";

    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Interactive Mode: {bot.Name} ===[/]");
        
        // Ensure answer dir exists
        Directory.CreateDirectory(BOT_ANSWERS_DIR);
        
        var answerFile = Path.Combine(BOT_ANSWERS_DIR, $"{SanitizeBotName(bot.Name)}.txt");
        
        // Step 1: Check if answer file exists
        bool hasExistingAnswers = File.Exists(answerFile);
        
        if (hasExistingAnswers)
        {
            AnsiConsole.MarkupLine($"[green]✓ Found existing answers: {Path.GetFileName(answerFile)}[/]");
            var preview = File.ReadAllLines(answerFile).Take(3);
            AnsiConsole.MarkupLine("[dim]Preview:[/]");
            foreach (var line in preview)
            {
                AnsiConsole.MarkupLine($"[dim]  {line}[/]");
            }
            
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[yellow]Choose action:[/]")
                    .AddChoices(new[]
                    {
                        "1. Use existing answers (skip local run)",
                        "2. Re-record answers (run bot again)",
                        "3. Edit answers manually",
                        "0. Cancel"
                    }));
            
            switch (choice.Split('.')[0])
            {
                case "1":
                    await TriggerRemoteWithAnswers(bot, answerFile, cancellationToken);
                    return;
                case "2":
                    // Continue to recording
                    break;
                case "3":
                    ShellHelper.RunInNewTerminal(
                        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "notepad" : "nano",
                        $"\"{answerFile}\"",
                        BOT_ANSWERS_DIR
                    );
                    AnsiConsole.MarkupLine("[yellow]Press Enter after editing...[/]");
                    await WaitForEnterAsync(cancellationToken);
                    await TriggerRemoteWithAnswers(bot, answerFile, cancellationToken);
                    return;
                case "0":
                    return;
            }
        }
        
        // Step 2: Record mode
        AnsiConsole.MarkupLine("\n[yellow]Step 1: Recording Mode[/]");
        AnsiConsole.MarkupLine("[dim]Bot will run locally. Record your answers.[/]\n");
        
        await RunInExternalTerminal(bot, cancellationToken);
        
        if (cancellationToken.IsCancellationRequested) return;
        
        // Step 3: Capture answers
        AnsiConsole.MarkupLine("\n[bold yellow]Step 2: Save Your Answers[/]");
        
        var captureChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]How do you want to provide answers?[/]")
                .AddChoices(new[]
                {
                    "1. Type answers manually (one per line)",
                    "2. Open text editor to create answer file",
                    "3. Skip (no auto-answer for remote)"
                }));
        
        switch (captureChoice.Split('.')[0])
        {
            case "1":
                await CaptureAnswersInteractive(answerFile, cancellationToken);
                break;
            case "2":
                File.WriteAllText(answerFile, "# Enter your answers here (one per line)\n# Lines starting with # are ignored\n");
                ShellHelper.RunInNewTerminal(
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "notepad" : "nano",
                    $"\"{answerFile}\"",
                    BOT_ANSWERS_DIR
                );
                AnsiConsole.MarkupLine("[yellow]Press Enter after saving...[/]");
                await WaitForEnterAsync(cancellationToken);
                break;
            case "3":
                AnsiConsole.MarkupLine("[yellow]Skipping answer capture[/]");
                break;
        }
        
        // Step 4: Trigger remote
        await PromptAndTriggerRemote(bot, answerFile, cancellationToken);
    }

    private static async Task CaptureAnswersInteractive(string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[cyan]Enter your answers (one per line, empty line to finish):[/]");
        
        var answers = new List<string>();
        int lineNum = 1;
        
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            AnsiConsole.Markup($"[dim]Answer {lineNum}:[/] ");
            var input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input)) break;
            
            answers.Add(input);
            lineNum++;
        }
        
        if (answers.Any())
        {
            File.WriteAllLines(answerFile, answers);
            AnsiConsole.MarkupLine($"[green]✓ Saved {answers.Count} answers to {Path.GetFileName(answerFile)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No answers captured[/]");
        }
    }

    private static async Task RunInExternalTerminal(BotEntry bot, CancellationToken cancellationToken)
    {
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Preparing: {bot.Name}[/]");
        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        var (executor, args) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine($"[red]No run command found[/]");
            return;
        }

        AnsiConsole.MarkupLine("\n[cyan]Launching bot in external terminal...[/]");
        ExternalTerminalRunner.RunBotInExternalTerminal(botPath, executor, args);
        
        AnsiConsole.MarkupLine("\n[yellow]Interact with the bot manually[/]");
        AnsiConsole.MarkupLine("[dim]Press Enter when done...[/]");
        await WaitForEnterAsync(cancellationToken);
        AnsiConsole.MarkupLine("[green]✓ Done[/]");
    }

    private static async Task TriggerRemoteWithAnswers(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        Dictionary<string, string> inputs = new();
        
        if (File.Exists(answerFile))
        {
            try
            {
                var lines = File.ReadAllLines(answerFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                    .ToList();
                
                // Convert to indexed dictionary for GitHub Actions
                for (int i = 0; i < lines.Count; i++)
                {
                    inputs[$"input_{i + 1}"] = lines[i];
                }
                
                AnsiConsole.MarkupLine($"[green]✓ Loaded {inputs.Count} answers[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading answers: {ex.Message}[/]");
            }
        }
        
        await PromptAndTriggerRemote(bot, answerFile, cancellationToken);
    }

    private static async Task PromptAndTriggerRemote(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[bold yellow]Step 3: Trigger GitHub Actions?[/]");
        
        Dictionary<string, string> inputs = new();
        
        if (File.Exists(answerFile))
        {
            try
            {
                var lines = File.ReadAllLines(answerFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                    .ToList();
                
                for (int i = 0; i < lines.Count; i++)
                {
                    inputs[$"input_{i + 1}"] = lines[i];
                }
                
                if (inputs.Any())
                {
                    AnsiConsole.MarkupLine($"[green]✓ Found {inputs.Count} answers[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: {ex.Message}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No answer file[/]");
            AnsiConsole.MarkupLine("[red]Remote bot may hang without answers[/]");
        }

        bool proceed = await ConfirmAsync("\nTrigger remote?", true, cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;

        if (!proceed)
        {
            AnsiConsole.MarkupLine("[yellow]Skipped[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Triggering workflow...[/]");
        await GitHubDispatcher.TriggerBotWithInputs(bot, inputs);
        AnsiConsole.MarkupLine("\n[bold green]✅ Triggered![/]");
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
            }
            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task WaitForEnterAsync(CancellationToken cancellationToken)
    {
        while (!Console.KeyAvailable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken);
        }
        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
    }

    private static string SanitizeBotName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Replace(" ", "_").Replace("-", "_");
    }
}
