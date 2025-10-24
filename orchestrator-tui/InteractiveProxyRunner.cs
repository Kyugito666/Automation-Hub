using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string InputsDir = "../.bot-inputs"; 
    // private const string VenvDirName = ".venv"; // <-- Dihapus, tidak terpakai di file ini

    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Interactive Proxy Mode: {bot.Name} ===[/]");
        AnsiConsole.MarkupLine("[yellow]Step 1: Capturing inputs locally...[/]");

        cancellationToken.ThrowIfCancellationRequested(); 

        Directory.CreateDirectory(InputsDir); 
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath)) { AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]"); return; }

        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, string>? capturedInputs = null;
        // bool cancelledDuringRun = false; // <-- Dihapus, variabel ini tidak terpakai dan bikin warning CS0219

        try
        {
            capturedInputs = await RunBotInCaptureMode(botPath, bot, cancellationToken);
             AnsiConsole.MarkupLine("[green]Capture run finished normally.[/]");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Capture run cancelled by user (Ctrl+C).[/]");
            var inputCapturePath = Path.Combine(botPath, ".input-capture.tmp");
            capturedInputs = ReadAndDeleteCaptureFile(inputCapturePath); 
            if (capturedInputs.Any()) {
                AnsiConsole.MarkupLine("[grey]Partial input data was captured before cancellation.[/]");
            } else {
                 AnsiConsole.MarkupLine("[grey]No input data captured before cancellation.[/]");
            }
            
            throw; // LEMPAR ULANG
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
             AnsiConsole.MarkupLine($"[red]Error during capture run: {ex.Message}[/]");
             AnsiConsole.MarkupLine("[yellow]Skipping remote trigger due to error.[/]");
             var inputCapturePath = Path.Combine(botPath, ".input-capture.tmp");
             if(File.Exists(inputCapturePath)) try {File.Delete(inputCapturePath);} catch{}
             return; 
        }

        // --- Lanjutkan ke Step 2 (Trigger) ---

        if (capturedInputs != null && capturedInputs.Any())
        {
            var table = new Table().Title("Captured Inputs");
            table.AddColumn("Key");
            table.AddColumn("Value");
            foreach (var (key, value) in capturedInputs)
            {
                table.AddRow(key, value.Length > 50 ? value[..47] + "..." : value);
            }
            AnsiConsole.Write(table);
        } else {
             AnsiConsole.MarkupLine("[yellow]No inputs captured (run finished normally). Bot might not be interactive.[/]");
        }

        var inputsFile = Path.Combine(InputsDir, $"{bot.Name}.json");
        var inputsJson = JsonSerializer.Serialize(capturedInputs ?? new Dictionary<string,string>(), new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(inputsFile, inputsJson);
        AnsiConsole.MarkupLine($"[green]✓ Inputs saved to: {inputsFile}[/]");


        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions?[/]");
        bool proceed = await ConfirmAsync("Proceed with remote execution (using captured inputs if any)?", true, cancellationToken);

        if (!proceed)
        {
            AnsiConsole.MarkupLine("[yellow]Remote trigger skipped.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Triggering remote job...[/]");
        await GitHubDispatcher.TriggerBotWithInputs(bot, capturedInputs ?? new Dictionary<string, string>());
        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely![/]");
    }

    private static async Task<Dictionary<string, string>?> RunBotInCaptureMode(string botPath, BotEntry bot, CancellationToken cancellationToken)
    {
        var inputs = new Dictionary<string, string>();
        var absoluteBotPath = Path.GetFullPath(botPath);
        var inputCapturePath = Path.Combine(absoluteBotPath, ".input-capture.tmp");

        string jsWrapperFileName = "capture_wrapper.cjs";
        string jsWrapperFullPath = Path.Combine(absoluteBotPath, jsWrapperFileName);
        string pyWrapperFileName = "capture_wrapper.py";
        string pyWrapperFullPath = Path.Combine(absoluteBotPath, pyWrapperFileName);

        if (File.Exists(pyWrapperFullPath)) try { File.Delete(pyWrapperFullPath); } catch {}
        if (File.Exists(jsWrapperFullPath)) try { File.Delete(jsWrapperFullPath); } catch {}
        if (File.Exists(inputCapturePath)) try { File.Delete(inputCapturePath); } catch {}

        cancellationToken.ThrowIfCancellationRequested();
        if (bot.Type == "python")
        {
            await CreatePythonCaptureWrapper(pyWrapperFullPath, inputCapturePath);
        }
        else if (bot.Type == "javascript")
        {
            await CreateJavaScriptCaptureWrapper(jsWrapperFullPath, inputCapturePath);
        }

        AnsiConsole.MarkupLine("[dim]Running bot interactively (local device)... (Press Ctrl+C to skip)[/]");
        AnsiConsole.MarkupLine("[dim]Answer all prompts normally. Inputs will be captured.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(absoluteBotPath, bot.Type);
        string executor;
        string args;

        if (string.IsNullOrEmpty(originalExecutor)) { return null; }

        if (bot.Type == "python")
        {
            executor = originalExecutor; 
             if (string.IsNullOrEmpty(originalArgs)) { return null; }
            args = $"-u \"{pyWrapperFileName}\" {originalArgs}"; 
        }
        else if (bot.Type == "javascript")
        {
            executor = "node"; 
            string targetScriptArg;
            if (originalExecutor == "npm" && originalArgs == "start") {
                string mainJs = File.Exists(Path.Combine(absoluteBotPath, "index.js")) ? "index.js"
                              : File.Exists(Path.Combine(absoluteBotPath, "main.js")) ? "main.js"
                              : File.Exists(Path.Combine(absoluteBotPath, "bot.js")) ? "bot.js"
                              : "";
                if (!string.IsNullOrEmpty(mainJs)) {
                    targetScriptArg = mainJs;
                    AnsiConsole.MarkupLine($"[grey]Detected 'npm start', assuming target script for capture is '{targetScriptArg}'[/]");
                } else {
                    AnsiConsole.MarkupLine($"[red]✗ Cannot detect main JS file for 'npm start' in {bot.Name}. Capture failed.[/]");
                    return null;
                }
            } else {
                 targetScriptArg = originalArgs;
                 if (string.IsNullOrEmpty(targetScriptArg) || !(targetScriptArg.EndsWith(".js") || targetScriptArg.EndsWith(".cjs") || targetScriptArg.EndsWith(".mjs"))) {
                      AnsiConsole.MarkupLine($"[red]✗ Invalid script argument ('{targetScriptArg}') for {bot.Name}.[/]");
                      return null;
                 }
            }
            args = $"\"{jsWrapperFileName}\" \"{targetScriptArg}\""; 
        }
        else { return null; }

        cancellationToken.ThrowIfCancellationRequested();
        
        await ShellHelper.RunInteractive(executor, args, absoluteBotPath, cancellationToken);
        
        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");

        inputs = ReadAndDeleteCaptureFile(inputCapturePath);

        try {
            if (File.Exists(pyWrapperFullPath)) File.Delete(pyWrapperFullPath);
            if (File.Exists(jsWrapperFullPath)) File.Delete(jsWrapperFullPath);
        } catch { /* Ignore cleanup */ }

        return inputs;
    }

    private static Dictionary<string, string> ReadAndDeleteCaptureFile(string filePath) {
        Dictionary<string, string> data = new Dictionary<string, string>();
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                try {
                     data = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                              ?? new Dictionary<string, string>();
                     AnsiConsole.MarkupLine($"[green]✓ Input capture file processed.[/]");
                } catch (JsonException jsonEx) {
                     AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse capture file: {jsonEx.Message}[/]");
                }
                File.Delete(filePath);
            }
            catch (IOException ioEx)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not process/delete capture file: {ioEx.Message}[/]");
            }
        }
        return data;
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

     private static async Task CreatePythonCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        var wrapper = $@"#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import sys
import json
import builtins
import os
import io
import traceback
import signal

# --- Configuration ---
CAPTURE_OUTPUT_PATH = r""{escapedOutputPath}""

# --- Signal Handling ---
_shutdown_initiated = False
def handle_signal(sig, frame):
    global _shutdown_initiated
    if not _shutdown_initiated:
        _shutdown_initiated = True
        print(f'\nCapture wrapper info: Received signal {{sig}}. Cleaning up and exiting...', file=sys.stderr)
        sys.exit(128 + sig)

signal.signal(signal.SIGINT, handle_signal)
signal.signal(signal.SIGTERM, handle_signal)

# --- Encoding Setup ---
try:
    if hasattr(sys.stdout, 'reconfigure'):
        try: sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        except: pass
    if hasattr(sys.stdin, 'reconfigure'):
         try: sys.stdin.reconfigure(encoding='utf-8', errors='replace')
         except: pass
except Exception as enc_err:
    print(f'Capture wrapper warning: Could not reconfigure stdio encoding: {{enc_err}}', file=sys.stderr)

# --- Input Capture Logic ---
captured = {{}}
_original_input = builtins.input

def capturing_input(prompt=''):
    global captured, _shutdown_initiated
    if _shutdown_initiated: raise KeyboardInterrupt(""Shutdown initiated"")
    try:
        print(prompt, end='', flush=True)
        response = sys.stdin.readline()
        if response is None: raise EOFError(""Stdin closed during input"")
        response = response.rstrip('\n\r')

        key_base = str(prompt).strip().rstrip(':').strip()
        key = key_base or f'input_{{len(captured)}}'

        count = 1
        final_key = key
        while final_key in captured:
            count += 1
            final_key = f'{{key}}_{{count}}'

        captured[final_key] = response
        return response
    except EOFError:
        print('Capture wrapper warning: EOFError detected during input.', file=sys.stderr); raise
    except KeyboardInterrupt:
        print('\nCapture wrapper info: KeyboardInterrupt during input.', file=sys.stderr); raise
    except Exception as input_err:
        print(f'Capture wrapper error during input call: {{input_err}}', file=sys.stderr); return ''

builtins.input = capturing_input

# --- Target Script Determination ---
script_to_run = None
original_argv = list(sys.argv)
if len(original_argv) > 1:
    script_arg = original_argv[1]; script_path = os.path.abspath(script_arg)
    if os.path.exists(script_path): script_to_run = script_path
    else: print(f'Capture wrapper error: Target script not found: {{script_path}}', file=sys.stderr)
else:
    for entry in ['run.py', 'main.py', 'bot.py']:
        entry_path = os.path.abspath(entry)
        if os.path.exists(entry_path):
            print(f'Capture wrapper info: Found entry point: {{entry}}', file=sys.stderr); script_to_run = entry_path; break
    if script_to_run is None: print('Capture wrapper error: No script argument and no entry point found.', file=sys.stderr)

# --- Script Execution and Cleanup ---
exit_code = 1; abs_output_path = os.path.abspath(CAPTURE_OUTPUT_PATH)
try:
    if _shutdown_initiated: sys.exit(1)
    if script_to_run:
        try:
            sys.argv = [script_to_run] + original_argv[2:]
            print(f'--- Starting Target Script: {{os.path.basename(script_to_run)}} ---', file=sys.stderr)
            with open(script_to_run, 'r', encoding='utf-8') as f: source = f.read()
            code = compile(source, script_to_run, 'exec')
            script_globals = {{'__name__': '__main__', '__file__': script_to_run}}
            exec(code, script_globals)
            exit_code = 0; print(f'\n--- Target Script Finished ---', file=sys.stderr)
        except SystemExit as sysexit: exit_code = sysexit.code if isinstance(sysexit.code, int) else 0; print(f'Capture wrapper info: Script exited via SystemExit({{exit_code}}).', file=sys.stderr)
        except KeyboardInterrupt:
             if not _shutdown_initiated: _shutdown_initiated = True; print(f'\nCapture wrapper info: KeyboardInterrupt caught.', file=sys.stderr); exit_code = 130
        except Exception as e: print(f'Capture wrapper FATAL ERROR: {{e}}', file=sys.stderr); traceback.print_exc(file=sys.stderr); exit_code = 1
    else: print(f'Capture wrapper warning: No script executed.', file=sys.stderr); exit_code = 1
finally:
    if not _shutdown_initiated:
        print(f'Capture wrapper info: Saving captured data...', file=sys.stderr)
        try:
            output_dir = os.path.dirname(abs_output_path); os.makedirs(output_dir, exist_ok=True)
            with open(abs_output_path, 'w', encoding='utf-8') as f:
                serializable_captured = {{'k': str(v) for k, v in captured.items()}}
                json.dump(serializable_captured, f, indent=2, ensure_ascii=False)
            print(f'Capture wrapper info: Data saved to {{abs_output_path}}', file=sys.stderr)
        except Exception as save_err: print(f'Capture wrapper FATAL: Save failed {{abs_output_path}}: {{save_err}}', file=sys.stderr); exit_code = 1
";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }

    private static async Task CreateJavaScriptCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        var wrapper = $@"// Force CommonJS mode by using .cjs extension
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const process = require('process');
const {{ Writable }} = require('stream');

const captured = {{}};
let rl = null;
const absOutputPath = path.resolve('{escapedOutputPath}');
let isExiting = false;

// --- Save Function ---
function saveCaptureData() {{
    if (isExiting) return;
    try {{
        const outputDir = path.dirname(absOutputPath);
        if (!fs.existsSync(outputDir)) {{
            fs.mkdirSync(outputDir, {{ recursive: true }});
        }}
        fs.writeFileSync(absOutputPath, JSON.stringify(captured, null, 2));
    }} catch (e) {{ console.error('JS Wrapper FATAL: Save failed:', e); }}
}}

// --- Exit Handling ---
function gracefulExit(signalOrCode = 0) {{
    if (isExiting) return; isExiting = true;
    saveCaptureData();
    if (rl && !rl.closed) {{ rl.close(); }}
    process.exitCode = (typeof signalOrCode === 'number' ? signalOrCode : 1);
    setTimeout(() => {{ process.exit(process.exitCode); }}, 250);
}}

process.on('exit', (code) => {{ if (!isExiting) saveCaptureData(); }});
process.on('SIGINT', () => {{ gracefulExit(130); }});
process.on('SIGTERM', () => {{ gracefulExit(143); }});


// --- Input Capture Setup ---
try {{
    const nullStream = new Writable({{ write(chunk, encoding, callback) {{ callback(); }} }});

    rl = readline.createInterface({{
        input: process.stdin, output: nullStream, prompt: ''
    }});

    const originalQuestion = rl.question;

    rl.question = function(query, optionsOrCallback, callback) {{
        if (isExiting) return;
        let actualCallback = callback; let actualOptions = optionsOrCallback;
        if (typeof optionsOrCallback === 'function') {{ actualCallback = optionsOrCallback; actualOptions = {{}}; }}
        else if (typeof optionsOrCallback !== 'object' || optionsOrCallback === null) {{ actualOptions = {{}}; }}

        process.stdout.write(String(query));

        originalQuestion.call(rl, '', actualOptions, (answer) => {{
             if (isExiting) return;
            process.stdout.write(answer + '\n');
            const keyBase = String(query).trim().replace(/[:?]/g, '').trim();
            let key = keyBase || `input_$${{Object.keys(captured).length}}`;

            let count = 1; const originalKey = key;
            while (captured.hasOwnProperty(key)) {{ count++; key = `$${{originalKey}}_$${{count}}`; }}
            captured[key] = answer;

            if (actualCallback) {{ try {{ actualCallback(answer); }} catch (cbError) {{ console.error('JS Wrapper Error: CB Exception:', cbError); }} }}
        }});
    }};
}} catch (e) {{ console.error('JS Wrapper Error: Readline init failed:', e); gracefulExit(1); }}

// --- Target Script Execution Logic ---
try {{
    if (isExiting) throw new Error(""Exiting before script exec."");
    const scriptRelativePath = process.argv[2];
    if (!scriptRelativePath) throw new Error('No target script.');
    const scriptAbsolutePath = path.resolve(process.cwd(), scriptRelativePath);
    if (!fs.existsSync(scriptAbsolutePath)) throw new Error(`Script not found: $${{scriptAbsolutePath}}`);
    process.argv = [process.argv[0], scriptAbsolutePath, ...process.argv.slice(3)];
    require(scriptAbsolutePath);
}} catch (e) {{ console.error('JS Wrapper FATAL: Script exec error:', e); gracefulExit(1); }}
";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }

} // End of class InteractiveProxyRunner
