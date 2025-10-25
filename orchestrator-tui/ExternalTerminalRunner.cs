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
        AnsiConsole.MarkupLine($"[bold cyan]=== Auto-Capture Mode: {bot.Name} ===[/]");
        AnsiConsole.MarkupLine("[yellow]Step 1: Run bot locally (session will be recorded automatically)[/]\n");

        var transcriptFile = await RunInExternalTerminal(bot, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]Local run cancelled, skipping remote trigger.[/]");
            return;
        }

        // Auto-parse transcript
        AnsiConsole.MarkupLine("\n[cyan]Analyzing recorded session...[/]");
        var capturedInputs = ExternalTerminalRunner.ParseTranscript(transcriptFile);

        // Save to answer file
        var answerFile = Path.Combine(BOT_ANSWERS_DIR, $"{SanitizeBotName(bot.Name)}.json");
        
        if (capturedInputs.Any())
        {
            try
            {
                Directory.CreateDirectory(BOT_ANSWERS_DIR);
                var json = JsonSerializer.Serialize(capturedInputs, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(answerFile, json);
                AnsiConsole.MarkupLine($"[green]✓ Answer file created: {Path.GetFileName(answerFile)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to save answer file: {ex.Message}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No inputs detected in transcript[/]");
            AnsiConsole.MarkupLine("[dim]Possible causes:[/]");
            AnsiConsole.MarkupLine("[dim]- Bot ran too fast (no input prompts)[/]");
            AnsiConsole.MarkupLine("[dim]- Transcript format not recognized[/]");
            AnsiConsole.MarkupLine($"[dim]- Check raw transcript: {Path.GetFileName(transcriptFile)}[/]");
            
            var shouldEdit = AnsiConsole.Confirm("Manually create answer file before remote trigger?", false);
            if (shouldEdit)
            {
                AnsiConsole.MarkupLine($"[yellow]Create file at: {answerFile}[/]");
                AnsiConsole.MarkupLine("[dim]Format: {{ \"input_1\": \"value1\", \"input_2\": \"value2\" }}[/]");
                AnsiConsole.MarkupLine("[dim]Press Enter when ready...[/]");
                await WaitForEnterAsync(cancellationToken);
                
                // Re-load after manual edit
                if (File.Exists(answerFile))
                {
                    try
                    {
                        var json = File.ReadAllText(answerFile);
                        capturedInputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                        AnsiConsole.MarkupLine($"[green]✓ Loaded {capturedInputs.Count} inputs from manual file[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to parse answer file: {ex.Message}[/]");
                    }
                }
            }
        }

        await PromptAndTriggerRemote(bot, capturedInputs, cancellationToken);
    }

    private static async Task<string> RunInExternalTerminal(BotEntry bot, CancellationToken cancellationToken)
    {
        var transcriptFile = string.Empty;
        
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return transcriptFile;
        }

        AnsiConsole.MarkupLine($"[cyan]Preparing bot: {bot.Name}[/]");
        
        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        var (executor, args) = BotRunner.GetRunCommand(botPath, bot.Type);
        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine($"[red]No run command found for {bot.Name}[/]");
            return transcriptFile;
        }

        AnsiConsole.MarkupLine("\n[cyan]Opening bot in external terminal with recording...[/]");
        transcriptFile = ExternalTerminalRunner.RunBotInExternalTerminal(botPath, executor, args);

        AnsiConsole.MarkupLine("[yellow]Interact with the bot normally.[/]");
        AnsiConsole.MarkupLine("[dim]All your inputs are being recorded automatically.[/]");
        AnsiConsole.MarkupLine("\n[dim]Press Enter when bot execution is complete...[/]");
        
        await WaitForEnterAsync(cancellationToken);
        AnsiConsole.MarkupLine("[green]✓ Local testing complete[/]");
        
        return transcriptFile;
    }

    private static async Task PromptAndTriggerRemote(BotEntry bot, Dictionary<string, string> capturedInputs, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("\n[bold yellow]Step 2: Trigger remote execution on GitHub Actions?[/]");
        
        if (capturedInputs.Any())
        {
            AnsiConsole.MarkupLine($"[green]✓ {capturedInputs.Count} inputs ready for automation[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No inputs captured[/]");
            AnsiConsole.MarkupLine("[red]WARNING: Remote bot will run without auto-input (may hang)[/]");
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
