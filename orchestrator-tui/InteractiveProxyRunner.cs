using Spectre.Console;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System;
using System.Text.Json;
using System.Linq;

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string BOT_ANSWERS_DIR = "../.bot-inputs";

    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Proxy Mode: {bot.Name} ===[/]");

        var answerFile = Path.Combine(BOT_ANSWERS_DIR, $"{SanitizeBotName(bot.Name)}.json");
        bool hasAnswerFile = File.Exists(answerFile);

        if (!hasAnswerFile)
        {
            CreateDefaultAnswerFile(bot, answerFile);
            hasAnswerFile = File.Exists(answerFile);
        }

        bool isRawKeyboardBot = false;
        if (hasAnswerFile)
        {
            try
            {
                var json = File.ReadAllText(answerFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (data != null && data.ContainsKey("_bot_type") && data["_bot_type"] == "raw_keyboard")
                {
                    isRawKeyboardBot = true;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse answer file: {ex.Message}[/]");
            }
        }

        if (isRawKeyboardBot)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ RAW KEYBOARD bot detected (uses arrow keys)[/]");
            AnsiConsole.MarkupLine("[red]Auto-answer NOT supported for this bot type[/]");
            
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]How to proceed?[/]")
                    .AddChoices(new[]
                    {
                        "1. Launch in separate terminal (manual interaction)",
                        "2. Try auto-answer anyway (may fail/hang)",
                        "3. Skip this bot"
                    }));
            
            if (choice.StartsWith("1"))
            {
                await RunInExternalTerminal(bot, answerFile: null, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    await PromptAndTriggerRemote(bot, answerFile, cancellationToken);
                }
                return;
            }
            else if (choice.StartsWith("3"))
            {
                AnsiConsole.MarkupLine("[yellow]Bot skipped.[/]");
                return;
            }
        }

        if (hasAnswerFile)
        {
            AnsiConsole.MarkupLine("[yellow]Mode: AUTO-ANSWER (Using saved responses)[/]");
            await RunInExternalTerminal(bot, answerFile, cancellationToken);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Mode: MANUAL (No answer file)[/]");
            await RunInExternalTerminal(bot, answerFile: null, cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Local run cancelled, skipping remote trigger.[/]");
            return;
        }

        await PromptAndTriggerRemote(bot, answerFile, cancellationToken);
    }

    private static void CreateDefaultAnswerFile(BotEntry bot, string answerFile)
    {
        var answers = new Dictionary<string, string>();

        if (bot.Name.Contains("Aster", StringComparison.OrdinalIgnoreCase))
        {
            answers["proxy_question"] = "y";
        }
        else
        {
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

    private static async Task RunInExternalTerminal(BotEntry bot, string? answerFile, CancellationToken cancellationToken)
    {
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Preparing bot: {bot.Name}[/]");
        
        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        var (executor, args) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine($"[red]No run command found for {bot.Name}[/]");
            return;
        }

        if (!string.IsNullOrEmpty(answerFile) && File.Exists(answerFile))
        {
            try
            {
                var json = File.ReadAllText(answerFile);
                var answers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (answers != null)
                {
                    var displayAnswers = answers.Where(x => !x.Key.StartsWith("_")).ToList();
                    if (displayAnswers.Any())
                    {
                        AnsiConsole.MarkupLine("[green]Answers loaded:[/]");
                        foreach (var kv in displayAnswers)
                        {
                            AnsiConsole.MarkupLine($"  [dim]{kv.Key}:[/] [yellow]{kv.Value}[/]");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse answers: {ex.Message}[/]");
                answerFile = null;
            }
        }

        AnsiConsole.MarkupLine("\n[cyan]Opening bot in external terminal...[/]");
        AnsiConsole.MarkupLine("[dim]Interact with the bot in the new window[/]");

        ExternalTerminalRunner.RunBotInExternalTerminal(botPath, executor, args, answerFile);

        AnsiConsole.MarkupLine("[green]✓ Bot terminal opened[/]");
        AnsiConsole.MarkupLine("[dim]Press Enter when bot execution is complete...[/]");
        
        await WaitForEnterAsync(cancellationToken);
        
        AnsiConsole.MarkupLine("[green]Continuing to next step...[/]");
    }

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
        
        Dictionary<string, string> capturedInputs = new();
        try
        {
            if (File.Exists(answerFile))
            {
                var json = File.ReadAllText(answerFile);
                var allData = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                
                capturedInputs = allData.Where(x => !x.Key.StartsWith("_")).ToDictionary(x => x.Key, x => x.Value);
                
                AnsiConsole.MarkupLine($"[dim]Sending {capturedInputs.Count} inputs to remote trigger...[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Warning: Answer file not found, sending empty inputs[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to read answer file, sending empty inputs: {ex.Message}[/]");
        }

        AnsiConsole.MarkupLine("[cyan]Triggering remote job...[/]");
        await GitHubDispatcher.TriggerBotWithInputs(bot, capturedInputs);
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
