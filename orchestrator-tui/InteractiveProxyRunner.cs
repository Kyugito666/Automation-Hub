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
        AnsiConsole.MarkupLine("[yellow]Step 1: Run bot locally[/]\n");

        await RunInExternalTerminal(bot, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled[/]");
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

        AnsiConsole.MarkupLine($"[cyan]Preparing: {bot.Name}[/]");
        
        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        var (executor, args) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine($"[red]No run command found[/]");
            return;
        }

        AnsiConsole.MarkupLine("\n[cyan]Launching bot...[/]");
        ExternalTerminalRunner.RunBotInExternalTerminal(botPath, executor, args);

        var answerFile = Path.Combine(BOT_ANSWERS_DIR, $"{SanitizeBotName(bot.Name)}.json");
        
        AnsiConsole.MarkupLine("\n[yellow]After testing:[/]");
        AnsiConsole.MarkupLine("[dim]1. Note your answers[/]");
        AnsiConsole.MarkupLine("[dim]2. Create answer file (optional):[/]");
        AnsiConsole.MarkupLine($"[dim]   {answerFile}[/]");
        AnsiConsole.MarkupLine("[dim]3. Format:[/]");
        AnsiConsole.MarkupLine("[dim]   {{[/]");
        AnsiConsole.MarkupLine("[dim]     \"input_1\": \"y\",[/]");
        AnsiConsole.MarkupLine("[dim]     \"input_2\": \"2\"[/]");
        AnsiConsole.MarkupLine("[dim]   }}[/]");
        AnsiConsole.MarkupLine("\n[dim]Press Enter when done...[/]");
        
        await WaitForEnterAsync(cancellationToken);
        AnsiConsole.MarkupLine("[green]✓ Done[/]");
    }

    private static async Task PromptAndTriggerRemote(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[bold yellow]Step 2: Trigger GitHub Actions?[/]");
        
        Dictionary<string, string> inputs = new();
        
        if (File.Exists(answerFile))
        {
            try
            {
                var json = File.ReadAllText(answerFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                inputs = data.Where(x => !x.Key.StartsWith("_")).ToDictionary(x => x.Key, x => x.Value);
                
                if (inputs.Any())
                {
                    AnsiConsole.MarkupLine($"[green]✓ Found {inputs.Count} inputs[/]");
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
            AnsiConsole.MarkupLine("[red]Remote bot may hang without input[/]");
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
