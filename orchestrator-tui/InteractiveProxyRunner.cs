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
    private const string WRAPPER_TEMPLATE_PATH = "../orchestrator-tui/templates/bot-wrapper-template.js";

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
                await RunWithWrapper(bot, cancellationToken);
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
            // If "2", continue to auto-answer (will likely fail)
        }

        if (hasAnswerFile && !isRawKeyboardBot)
        {
            AnsiConsole.MarkupLine("[yellow]Mode: AUTO-ANSWER (Using saved responses)[/]");
            await RunWithAutoAnswers(bot, answerFile, cancellationToken);
        }
        else if (hasAnswerFile && isRawKeyboardBot)
        {
            AnsiConsole.MarkupLine("[yellow]Mode: AUTO-ANSWER (Forced attempt despite raw keyboard detection)[/]");
            await RunWithAutoAnswers(bot, answerFile, cancellationToken);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Mode: MANUAL (No answer file)[/]");
            await RunManualCapture(bot, cancellationToken);
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

    private static async Task RunWithAutoAnswers(BotEntry bot, string answerFile, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[dim]Loading saved answers...[/]");
        
        try
        {
            var json = File.ReadAllText(answerFile);
            var answers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            
            if (answers != null && answers.Any())
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
                else
                {
                    AnsiConsole.MarkupLine("[yellow]No user answers found (metadata only)[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Warning: Answer file is empty[/]");
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
            await ShellHelper.RunInteractivePty(executor, args, botPath, cancellationToken);
            
            AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");
            AnsiConsole.MarkupLine("[green]Manual run completed.[/]");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Manual run cancelled.[/]");
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during manual run: {ex.Message}[/]");
        }
    }

    private static async Task RunWithWrapper(BotEntry bot, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[yellow]Generating wrapper script...[/]");
        
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
            AnsiConsole.MarkupLine($"[red]No run command found for {bot.Name}[/]");
            return;
        }

        if (bot.Type == "javascript")
        {
            var entryFile = args;
            var wrapperPath = Path.Combine(botPath, "_auto_wrapper.js");
            
            try
            {
                if (!File.Exists(WRAPPER_TEMPLATE_PATH))
                {
                    AnsiConsole.MarkupLine($"[red]Wrapper template not found: {WRAPPER_TEMPLATE_PATH}[/]");
                    AnsiConsole.MarkupLine("[yellow]Falling back to direct terminal launch...[/]");
                    ShellHelper.RunInNewTerminal(executor, args, botPath);
                    
                    AnsiConsole.MarkupLine("[dim]Press Enter when finished...[/]");
                    await WaitForEnterAsync(cancellationToken);
                    return;
                }

                var template = File.ReadAllText(WRAPPER_TEMPLATE_PATH);
                var wrapper = template
                    .Replace("__BOT_DIR__", botPath.Replace("\\", "\\\\"))
                    .Replace("__BOT_ENTRY__", entryFile);

                File.WriteAllText(wrapperPath, wrapper);
                
                AnsiConsole.MarkupLine($"[green]✓ Wrapper created: {Path.GetFileName(wrapperPath)}[/]");
                AnsiConsole.MarkupLine("[cyan]Launching bot in separate terminal...[/]");
                
                ShellHelper.RunInNewTerminal("node", "_auto_wrapper.js", botPath);
                
                AnsiConsole.MarkupLine("[green]✓ Bot launched. Interact with it in the new window.[/]");
                AnsiConsole.MarkupLine("[dim]Press Enter when finished...[/]");
                
                await WaitForEnterAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Wrapper error: {ex.Message}[/]");
                AnsiConsole.MarkupLine("[yellow]Falling back to direct launch...[/]");
                ShellHelper.RunInNewTerminal(executor, args, botPath);
                
                AnsiConsole.MarkupLine("[dim]Press Enter when finished...[/]");
                await WaitForEnterAsync(cancellationToken);
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Python bot - launching directly in terminal...[/]");
            ShellHelper.RunInNewTerminal(executor, args, botPath);
            
            AnsiConsole.MarkupLine("[dim]Press Enter when finished...[/]");
            await WaitForEnterAsync(cancellationToken);
        }
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
                capturedInputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                
                // Remove metadata fields
                capturedInputs = capturedInputs.Where(x => !x.Key.StartsWith("_")).ToDictionary(x => x.Key, x => x.Value);
                
                AnsiConsole.MarkupLine($"[dim]Menggunakan {capturedInputs.Count} input dari {Path.GetFileName(answerFile)} untuk remote trigger...[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Warning: Answer file not found, sending empty inputs[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Gagal baca answer file, kirim input kosong: {ex.Message}[/]");
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
