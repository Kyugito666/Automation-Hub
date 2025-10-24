using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string InputsDir = "../.bot-inputs";
    
    public static async Task CaptureAndTriggerBot(BotEntry bot)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Interactive Proxy Mode: {bot.Name} ===[/]");
        AnsiConsole.MarkupLine("[yellow]Step 1: Capturing inputs locally...[/]");
        
        // Ensure inputs directory exists
        Directory.CreateDirectory(InputsDir);
        
        var botPath = Path.Combine("..", bot.Path);
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return;
        }

        // Install dependencies first
        await BotRunner.InstallDependencies(botPath, bot.Type);

        // Run bot in capture mode
        var capturedInputs = await RunBotInCaptureMode(botPath, bot);
        
        if (capturedInputs == null || capturedInputs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No inputs captured. Bot may not be interactive.[/]");
            
            var runDirect = AnsiConsole.Confirm("Run directly on GitHub Actions without inputs?");
            if (!runDirect) return;
            
            capturedInputs = new Dictionary<string, string>();
        }

        // Save inputs to file
        var inputsFile = Path.Combine(InputsDir, $"{bot.Name}.json");
        var inputsJson = JsonSerializer.Serialize(capturedInputs, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        await File.WriteAllTextAsync(inputsFile, inputsJson);
        
        AnsiConsole.MarkupLine($"[green]✓ Inputs saved to: {inputsFile}[/]");

        // Display captured inputs
        if (capturedInputs.Any())
        {
            var table = new Table().Title("Captured Inputs");
            table.AddColumn("Key");
            table.AddColumn("Value");
            
            foreach (var (key, value) in capturedInputs)
            {
                table.AddRow(key, value.Length > 50 ? value[..47] + "..." : value);
            }
            
            AnsiConsole.Write(table);
        }

        // Confirm trigger
        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions...[/]");
        
        if (!AnsiConsole.Confirm("Proceed with remote execution?"))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return;
        }

        // Trigger GitHub Actions with inputs
        await GitHubDispatcher.TriggerBotWithInputs(bot, capturedInputs);
        
        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely with captured inputs![/]");
    }

    private static async Task<Dictionary<string, string>?> RunBotInCaptureMode(string botPath, BotEntry bot)
    {
        var inputs = new Dictionary<string, string>();
        var inputCapturePath = Path.Combine(botPath, ".input-capture.tmp");
        
        // Create input capture wrapper script
        if (bot.Type == "python")
        {
            await CreatePythonCaptureWrapper(botPath, inputCapturePath);
        }
        else if (bot.Type == "javascript")
        {
            await CreateJavaScriptCaptureWrapper(botPath, inputCapturePath);
        }

        AnsiConsole.MarkupLine("[dim]Running bot interactively (local device)...[/]");
        AnsiConsole.MarkupLine("[dim]Answer all prompts normally. Inputs will be captured.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        // Run bot interactively
        var (executor, args) = BotRunner.GetRunCommand(botPath, bot.Type);
        
        // Inject capture flag
        if (bot.Type == "python")
        {
            args = $"-u capture_wrapper.py {args}";
            executor = "python";
        }
        else if (bot.Type == "javascript")
        {
            args = $"capture_wrapper.js {args}";
            executor = "node";
        }

        try
        {
            await ShellHelper.RunInteractive(executor, args, botPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Execution error: {ex.Message}[/]");
        }

        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");

        // Read captured inputs
        if (File.Exists(inputCapturePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(inputCapturePath);
                inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json) 
                    ?? new Dictionary<string, string>();
                File.Delete(inputCapturePath);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse captured inputs: {ex.Message}[/]");
            }
        }

        // Cleanup wrapper
        try
        {
            if (bot.Type == "python" && File.Exists(Path.Combine(botPath, "capture_wrapper.py")))
                File.Delete(Path.Combine(botPath, "capture_wrapper.py"));
            
            if (bot.Type == "javascript" && File.Exists(Path.Combine(botPath, "capture_wrapper.js")))
                File.Delete(Path.Combine(botPath, "capture_wrapper.js"));
        }
        catch { }

        return inputs;
    }

    private static async Task CreatePythonCaptureWrapper(string botPath, string outputPath)
    {
        var wrapper = @$"#!/usr/bin/env python3
import sys
import json
import builtins

captured = {{}}
_original_input = builtins.input

def capturing_input(prompt=''):
    response = _original_input(prompt)
    key = prompt.strip().rstrip(':').strip() or f'input_{{len(captured)}}'
    captured[key] = response
    return response

builtins.input = capturing_input

try:
    if len(sys.argv) > 1:
        script = sys.argv[1]
        with open(script) as f:
            code = compile(f.read(), script, 'exec')
            exec(code)
    else:
        import run
except SystemExit:
    pass
except Exception as e:
    print(f'Capture wrapper error: {{e}}')
finally:
    with open('{outputPath.Replace("\\", "\\\\")}', 'w') as f:
        json.dump(captured, f, indent=2)
";
        await File.WriteAllTextAsync(Path.Combine(botPath, "capture_wrapper.py"), wrapper);
    }

    private static async Task CreateJavaScriptCaptureWrapper(string botPath, string outputPath)
    {
        var wrapper = @$"const fs = require('fs');
const readline = require('readline');

const captured = {{}};
const rl = readline.createInterface({{
    input: process.stdin,
    output: process.stdout
}});

const originalQuestion = rl.question.bind(rl);
rl.question = function(query, callback) {{
    originalQuestion(query, (answer) => {{
        const key = query.trim().replace(/[:\?]/g, '').trim() || `input_${{Object.keys(captured).length}}`;
        captured[key] = answer;
        callback(answer);
    }});
}};

process.on('exit', () => {{
    fs.writeFileSync('{outputPath.Replace("\\", "\\\\")}', JSON.stringify(captured, null, 2));
}});

try {{
    const script = process.argv[2] || './index.js';
    require(script);
}} catch (e) {{
    console.error('Capture wrapper error:', e);
}}
";
        await File.WriteAllTextAsync(Path.Combine(botPath, "capture_wrapper.js"), wrapper);
    }
}
