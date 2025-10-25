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
        AnsiConsole.MarkupLine("[yellow]Step 1: Run bot locally (manual interaction)[/]");
        AnsiConsole.MarkupLine("[dim]You will interact with the bot normally in a new window[/]\n");

        await RunInExternalTerminal(bot, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Local run cancelled, skipping remote trigger.[/]");
            return;
        }

        var answerFile = Path.Combine(BOT_ANSWERS_DIR, $"{SanitizeBotName(bot.Name)}.json");
        await PromptAndTriggerRemote(bot, answerFile, cancellationToken);
    }

    private static async Task RunInExternalTerminal(BotEntry bot, CancellationToken cancellationToken)
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

        AnsiConsole.MarkupLine("\n[cyan]Opening bot in external terminal...[/]");
        ExternalTerminalRunner.RunBotInExternalTerminal(botPath, executor, args);

        AnsiConsole.MarkupLine("[yellow]After testing the bot:[/]");
        AnsiConsole.MarkupLine("[dim]1. Note down your answers/choices[/]");
        AnsiConsole.MarkupLine("[dim]2. Create answer file for remote execution (optional)[/]");
        AnsiConsole.MarkupLine($"[dim]   Location: {Path.Combine(BOT_ANSWERS_DIR, $"{SanitizeBotName(bot.Name)}.json")}[/]");
        AnsiConsole.MarkupLine($"[dim]   Format: {{ \"question1\": \"answer1\", \"question2\": \"answer2\" }}[/]");
        AnsiConsole.MarkupLine("\n[dim]Press Enter when bot execution is complete...[/]");
        
        await WaitForEnterAsync(cancellationToken);
        AnsiConsole.MarkupLine("[green]✓ Local testing complete[/]");
    }

    private static async Task PromptAndTriggerRemote(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[bold yellow]Step 2: Trigger remote execution on GitHub Actions?[/]");
        
        Dictionary<string, string> capturedInputs = new();
        
        if (File.Exists(answerFile))
        {
            try
            {
                var json = File.ReadAllText(answerFile);
                var allData = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                capturedInputs = allData.Where(x => !x.Key.StartsWith("_")).ToDictionary(x => x.Key, x => x.Value);
                
                if (capturedInputs.Any())
                {
                    AnsiConsole.MarkupLine($"[green]✓ Found answer file with {capturedInputs.Count} inputs[/]");
                    AnsiConsole.MarkupLine("[dim]These will be sent to GitHub Actions for automation[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not read answer file: {ex.Message}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No answer file found[/]");
            AnsiConsole.MarkupLine("[dim]Remote bot will run without auto-input (may hang if bot needs input)[/]");
        }

        bool proceed = await ConfirmAsync("\nTrigger remote execution?", true, cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;

        if (!proceed)
        {
            AnsiConsole.MarkupLine("[yellow]Remote trigger skipped.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Triggering GitHub Actions workflow...[/]");
        await GitHubDispatcher.TriggerBotWithInputs(bot, capturedInputs);
        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely![/]");
        
        if (capturedInputs.Any())
        {
            AnsiConsole.MarkupLine($"[dim]Sent {capturedInputs.Count} inputs for automation[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Note: Bot triggered without input data[/]");
            AnsiConsole.MarkupLine("[dim]Check GitHub Actions logs if bot hangs[/]");
        }
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
