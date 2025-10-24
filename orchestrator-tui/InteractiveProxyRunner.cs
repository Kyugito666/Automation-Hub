using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.IO;
using System.Runtime.InteropServices; // Ditambahkan

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string InputsDir = "../.bot-inputs";
    private const string VenvDirName = ".venv"; // Sama seperti di BotRunner

    // ... (CaptureAndTriggerBot method unchanged) ...
    public static async Task CaptureAndTriggerBot(BotEntry bot)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Interactive Proxy Mode: {bot.Name} ===[/]");
        AnsiConsole.MarkupLine("[yellow]Step 1: Capturing inputs locally...[/]");

        Directory.CreateDirectory(InputsDir);

        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path)); // Use full path
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return;
        }

        // Install dependencies (will handle venv creation/update)
        await BotRunner.InstallDependencies(botPath, bot.Type);

        // Run capture mode (will use venv python if applicable)
        var capturedInputs = await RunBotInCaptureMode(botPath, bot);

        if (Program.InteractiveBotCancelled)
        {
            AnsiConsole.MarkupLine("[yellow]Skipping remaining steps for this bot due to cancellation.[/]");
            return;
        }

        if (capturedInputs == null || capturedInputs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No inputs captured. Bot may not be interactive, or capture failed.[/]"); // Update message
            var runDirect = AnsiConsole.Confirm("Run directly on GitHub Actions without inputs?");
            if (!runDirect) return;
            capturedInputs = new Dictionary<string, string>();
        }

        var inputsFile = Path.Combine(InputsDir, $"{bot.Name}.json");
        var inputsJson = JsonSerializer.Serialize(capturedInputs, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(inputsFile, inputsJson);
        AnsiConsole.MarkupLine($"[green]✓ Inputs saved to: {inputsFile}[/]");

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

        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions...[/]");
        if (!AnsiConsole.Confirm("Proceed with remote execution?"))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return;
        }

        await GitHubDispatcher.TriggerBotWithInputs(bot, capturedInputs);
        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely![/]");
    }


    private static async Task<Dictionary<string, string>?> RunBotInCaptureMode(string botPath, BotEntry bot)
    {
        var inputs = new Dictionary<string, string>();
        var absoluteBotPath = Path.GetFullPath(botPath); // Ensure absolute path
        var inputCapturePath = Path.Combine(absoluteBotPath, ".input-capture.tmp");

        string jsWrapperFileName = "capture_wrapper.cjs";
        string jsWrapperFullPath = Path.Combine(absoluteBotPath, jsWrapperFileName);
        string pyWrapperFileName = "capture_wrapper.py";
        string pyWrapperFullPath = Path.Combine(absoluteBotPath, pyWrapperFileName);

        if (File.Exists(pyWrapperFullPath)) File.Delete(pyWrapperFullPath);
        if (File.Exists(jsWrapperFullPath)) File.Delete(jsWrapperFullPath);
        if (File.Exists(inputCapturePath)) File.Delete(inputCapturePath);

        if (bot.Type == "python")
        {
            await CreatePythonCaptureWrapper(pyWrapperFullPath, inputCapturePath);
        }
        else if (bot.Type == "javascript")
        {
            await CreateJavaScriptCaptureWrapper(jsWrapperFullPath, inputCapturePath);
        }

        AnsiConsole.MarkupLine("[dim]Running bot interactively (local device)...[/]");
        AnsiConsole.MarkupLine("[dim]Answer all prompts normally. Inputs will be captured.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        // === GUNAKAN GetRunCommand YANG SUDAH VENV-AWARE ===
        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(absoluteBotPath, bot.Type);
        // ===================================================

        string executor;
        string args;

        if (string.IsNullOrEmpty(originalExecutor))
        {
             AnsiConsole.MarkupLine($"[red]✗ Tidak bisa menemukan command utama (npm start, venv python, atau file .js/.py) untuk {bot.Name}.[/]");
             return null;
        }

        if (bot.Type == "python")
        {
            // Executor *sudah* path ke python venv dari GetRunCommand
            executor = originalExecutor;
            // Args: -u wrapper.py original_script.py
            args = $"-u \"{pyWrapperFileName}\" {originalArgs}";
        }
        else if (bot.Type == "javascript")
        {
            executor = "node"; // Wrapper tetap pakai node global
            string targetScriptArg;

            if (originalExecutor == "npm" && originalArgs == "start")
            {
                string mainJs = File.Exists(Path.Combine(absoluteBotPath, "index.js")) ? "index.js"
                              : File.Exists(Path.Combine(absoluteBotPath, "main.js")) ? "main.js"
                              : File.Exists(Path.Combine(absoluteBotPath, "bot.js")) ? "bot.js"
                              : "";
                if (!string.IsNullOrEmpty(mainJs))
                {
                    targetScriptArg = mainJs;
                    AnsiConsole.MarkupLine($"[grey]Detected 'npm start', assuming target script for capture is '{targetScriptArg}'[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗ Tidak bisa otomatis mendeteksi file JS utama untuk 'npm start' di {bot.Name}. Capture mungkin gagal. Mencoba 'index.js'.[/]");
                    targetScriptArg = "index.js";
                }
            }
            else // Asumsi originalExecutor="node", originalArgs="file.js"
            {
                 targetScriptArg = originalArgs;
            }
            args = $"\"{jsWrapperFileName}\" \"{targetScriptArg}\"";
        }
        else
        {
             AnsiConsole.MarkupLine($"[red]✗ Tipe bot tidak dikenal: {bot.Type}[/]");
             return null;
        }


        try
        {
            Program.InteractiveBotCancelled = false;
            // Jalankan dari path bot, gunakan executor yang sudah ditentukan (bisa python venv)
            await ShellHelper.RunInteractive(executor, args, absoluteBotPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Execution error: {ex.Message}[/]");
            AnsiConsole.MarkupLine($"[dim]Command: {executor} {args}[/]");
            AnsiConsole.MarkupLine($"[dim]Working Dir: {absoluteBotPath}[/]");
        }

        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");

        if (File.Exists(inputCapturePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(inputCapturePath);
                try {
                     inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                              ?? new Dictionary<string, string>();
                } catch (JsonException jsonEx) {
                     AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse capture file (might be incomplete due to cancel): {jsonEx.Message}[/]");
                     inputs = new Dictionary<string, string>();
                }
                File.Delete(inputCapturePath);
                 AnsiConsole.MarkupLine($"[green]✓ Input capture file processed and deleted.[/]");
            }
            catch (IOException ioEx)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not process/delete capture file: {ioEx.Message}[/]");
                 inputs = new Dictionary<string, string>();
            }
        } else if (!Program.InteractiveBotCancelled) {
             AnsiConsole.MarkupLine($"[yellow]Warning: Input capture file not found at {inputCapturePath} after normal execution.[/]");
        } else {
             AnsiConsole.MarkupLine($"[grey]Input capture file not created (expected due to cancellation).[/]");
        }

        try
        {
            if (File.Exists(pyWrapperFullPath))
                File.Delete(pyWrapperFullPath);
            if (File.Exists(jsWrapperFullPath))
                File.Delete(jsWrapperFullPath);
        }
        catch { /* Ignore cleanup errors */ }

        return inputs;
    }

    // ... (CreatePythonCaptureWrapper dan CreateJavaScriptCaptureWrapper tidak berubah dari versi fix escaping terakhir) ...
     private static async Task CreatePythonCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        // Escape outputPath for the generated Python string literal in C#
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        // Use @"" for verbatim string literal and double braces {{ }} for Python's f-string braces
        var wrapper = $@"#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import sys
import json
import builtins
import os
import io
import traceback # Added for better error reporting
import signal # Added for signal handling

# --- Configuration ---
CAPTURE_OUTPUT_PATH = r""{escapedOutputPath}"" # Use raw string for path

# --- Signal Handling ---
def handle_signal(sig, frame):
    print(f'\nCapture wrapper info: Received signal {{sig}}. Cleaning up...', file=sys.stderr) # Python f-string -> {{{{}}}}
    # The 'finally' block will handle saving the captured data.
    # Exit with a code indicating interruption, if possible.
    sys.exit(128 + sig) # Standard exit code for signals

signal.signal(signal.SIGINT, handle_signal) # Ctrl+C
signal.signal(signal.SIGTERM, handle_signal) # Termination signal


# --- Encoding Setup ---
try:
    # Use 'o' mode for binary write, then wrap with TextIOWrapper for encoding control
    # sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    # sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding='utf-8', errors='replace')
    # More robust check and reconfiguration:
    if hasattr(sys.stdout, 'reconfigure'):
        try:
            sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        except: # Handle cases where reconfigure might fail (e.g., already closed)
             pass
    if hasattr(sys.stdin, 'reconfigure'):
         try:
            sys.stdin.reconfigure(encoding='utf-8', errors='replace')
         except:
             pass

except Exception as enc_err:
    print(f'Capture wrapper warning: Could not reconfigure stdio encoding: {{enc_err}}', file=sys.stderr) # Python f-string -> {{{{}}}}

# --- Input Capture Logic ---
captured = {{}} # Python dict init -> {{{{}}}}
_original_input = builtins.input

def capturing_input(prompt=''):
    global captured
    try:
        # Display the prompt using the original stdout encoding behavior if possible
        # Write directly to buffer to bypass potential wrapper encoding issues? No, use print.
        print(prompt, end='', flush=True)
        #response = _original_input() # Read input without prompt arg
        # Use readline directly to avoid potential issues with wrapped input() under some terminals/libs
        response = sys.stdin.readline()
        if response is None: # Check for EOF
             raise EOFError(""Stdin closed during input"")
        response = response.rstrip('\n\r') # Strip newline

        key_base = str(prompt).strip().rstrip(':').strip()
         # Python f-string -> {{{{}}}}
        key = key_base or f'input_{{len(captured)}}'

        count = 1
        final_key = key
        while final_key in captured:
            count += 1
             # Python f-string -> {{{{}}}}
            final_key = f'{{key}}_{{count}}'

        captured[final_key] = response
        return response
    except EOFError:
        print('Capture wrapper warning: EOFError detected during input.', file=sys.stderr)
        raise # Re-raise for the target script
    except KeyboardInterrupt: # Handle Ctrl+C during input specifically
        print('\nCapture wrapper info: KeyboardInterrupt during input.', file=sys.stderr)
        raise # Re-raise for signal handler or outer try/except
    except Exception as input_err:
         # Python f-string -> {{{{}}}}
        print(f'Capture wrapper error during input call: {{input_err}}', file=sys.stderr)
        # Depending on the error, maybe raise it? For now, return empty.
        return ''

builtins.input = capturing_input

# --- Target Script Determination ---
script_to_run = None
original_argv = list(sys.argv) # sys.argv[0] is wrapper.py, sys.argv[1] is target script arg from C#

if len(original_argv) > 1:
    script_arg = original_argv[1]
    # Assume script_arg is relative to the CWD (botPath set by C#)
    script_path = os.path.abspath(script_arg)
    if os.path.exists(script_path):
        script_to_run = script_path
    else:
         # Python f-string -> {{{{}}}}
        print(f'Capture wrapper error: Target script specified but not found: {{script_path}}', file=sys.stderr)
else:
    # Try common entry points if no script argument given
    entry_points = ['run.py', 'main.py', 'bot.py']
    for entry in entry_points:
        entry_path = os.path.abspath(entry)
        if os.path.exists(entry_path):
             # Python f-string -> {{{{}}}}
            print(f'Capture wrapper info: No script argument, found {{entry}}. Running it.', file=sys.stderr)
            script_to_run = entry_path
            break
    if script_to_run is None:
         print('Capture wrapper error: No script argument and no common entry point (run.py/main.py/bot.py) found.', file=sys.stderr)

# --- Script Execution and Cleanup ---
script_executed_successfully = False
exit_code = 1 # Default to error
abs_output_path = os.path.abspath(CAPTURE_OUTPUT_PATH) # Define here for finally block

try: # Outer try for execution and saving
    if script_to_run:
        try: # Inner try specifically for script execution
            # Prepare environment for the target script
            sys.argv = [script_to_run] + original_argv[2:] # Target script sees its name + remaining args
            script_dir = os.path.dirname(script_to_run)
            # Change CWD to script's directory? Might break relative paths expected from botPath.
            # Let's keep CWD as botPath set by C# runner.

             # Python f-string -> {{{{}}}}
            print(f'--- Starting Target Script: {{os.path.basename(script_to_run)}} ---', file=sys.stderr)

            with open(script_to_run, 'r', encoding='utf-8') as f:
                source = f.read()
                code = compile(source, script_to_run, 'exec')
                 # Python dict init -> {{{{}}}}
                script_globals = {{'__name__': '__main__', '__file__': script_to_run}}
                exec(code, script_globals)
                script_executed_successfully = True
                exit_code = 0 # Assume success if no exception/SystemExit
                print(f'\n--- Target Script Finished ---', file=sys.stderr)

        except SystemExit as sysexit:
            exit_code = sysexit.code if isinstance(sysexit.code, int) else (0 if sysexit.code is None else 1)
             # Python f-string -> {{{{}}}}
            print(f'Capture wrapper info: Script exited via SystemExit with code {{exit_code}}.', file=sys.stderr)
            script_executed_successfully = (exit_code == 0)
        except KeyboardInterrupt:
             print(f'\nCapture wrapper info: KeyboardInterrupt caught during script execution.', file=sys.stderr)
             exit_code = 130 # Standard exit code for SIGINT
        except Exception as e:
             # Python f-string -> {{{{}}}}
            print(f'Capture wrapper FATAL ERROR during script execution: {{e}}', file=sys.stderr)
            traceback.print_exc(file=sys.stderr)
            exit_code = 1
    else:
         print(f'Capture wrapper warning: No script was executed.', file=sys.stderr)
         exit_code = 1 # No script found is an error

finally:
    # --- SAVE CAPTURED DATA ---
    # This block executes even if SystemExit, KeyboardInterrupt, or other exceptions occur
    print(f'Capture wrapper info: Entering finally block. Saving captured data...', file=sys.stderr)
    try:
        output_dir = os.path.dirname(abs_output_path)
        os.makedirs(output_dir, exist_ok=True)
        with open(abs_output_path, 'w', encoding='utf-8') as f:
             # Python dict comprehension -> {{{{}}}}
            serializable_captured = {{k: str(v) for k, v in captured.items()}}
            json.dump(serializable_captured, f, indent=2, ensure_ascii=False)
         # Python f-string -> {{{{}}}}
        print(f'Capture wrapper info: Captured data saved to {{abs_output_path}}', file=sys.stderr)
    except Exception as save_err:
         # Python f-string -> {{{{}}}}
        print(f'Capture wrapper FATAL ERROR: Failed to write capture file {{abs_output_path}}: {{save_err}}', file=sys.stderr)
        # If saving fails, we might want to ensure the exit code reflects an error
        if exit_code == 0: exit_code = 1

# Exit attempt removed, C# will detect process end

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }


    private static async Task CreateJavaScriptCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        // Gunakan @"" dan gandakan SEMUA kurung kurawal internal {{ }}
        // Untuk template literal JS (` `), $ juga perlu di-escape ($$) jika bukan untuk C#
        var wrapper = $@"// Force CommonJS mode by using .cjs extension
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const process = require('process');
const {{ Writable }} = require('stream');

const captured = {{}}; // JS object literal -> {{{{}}}}
let rl = null;
const absOutputPath = path.resolve('{escapedOutputPath}'); // C# Interpolation
let isExiting = false;

// --- Save Function ---
function saveCaptureData() {{
    if (isExiting) return;
    // JS template literal -> `${{{{...}}}}`, escape C# var with double $$
    // console.log(`Debug JS: Attempting save to $${{{{absOutputPath}}}}`);
    try {{
        const outputDir = path.dirname(absOutputPath);
        // JS object literal -> {{{{}}}}
        if (!fs.existsSync(outputDir)) {{
            fs.mkdirSync(outputDir, {{ recursive: true }});
        }}
        fs.writeFileSync(absOutputPath, JSON.stringify(captured, null, 2));
        // console.log(`Debug JS: Saved data.`);
    }} catch (e) {{
        console.error('Capture wrapper FATAL ERROR JS: Failed to write capture file:', e);
    }}
}}

// --- Exit Handling ---
function gracefulExit(signalOrCode = 0) {{
    if (isExiting) return;
    isExiting = true;
    // JS template literal -> `${{{{...}}}}`
    // console.log(`Debug JS: Initiating exit with code/signal: $${{{{signalOrCode}}}}`);
    saveCaptureData();
    if (rl && !rl.closed) {{
        // console.log('Debug JS: Closing readline.');
        rl.close();
    }}
    if (typeof signalOrCode === 'number') {{
         process.exitCode = signalOrCode;
    }}
    // Force exit after delay
    setTimeout(() => {{ process.exit(process.exitCode || 0); }}, 200);
}}

process.on('exit', (code) => {{
    // JS template literal -> `${{{{...}}}}`
    // console.log(`Debug JS: 'exit' event triggered with code $${{{{code}}}}.`);
    if (!isExiting) {{
         saveCaptureData();
    }}
}});
// JS Arrow function -> () => {{ ... }}
process.on('SIGINT', () => {{ /* console.log('Debug JS: SIGINT received.');*/ gracefulExit(130); }});
process.on('SIGTERM', () => {{ /* console.log('Debug JS: SIGTERM received.');*/ gracefulExit(143); }});


// --- Input Capture Setup ---
try {{
    const nullStream = new Writable({{ write(chunk, encoding, callback) {{ callback(); }} }}); // JS object literal -> {{{{}}}}

    rl = readline.createInterface({{ // JS object literal -> {{{{}}}}
        input: process.stdin,
        output: nullStream,
        prompt: ''
    }});

    const originalQuestion = rl.question;

    // JS function definition -> function(...) {{ ... }}
    rl.question = function(query, optionsOrCallback, callback) {{
        let actualCallback = callback;
        let actualOptions = optionsOrCallback;

        if (typeof optionsOrCallback === 'function') {{
            actualCallback = optionsOrCallback;
            actualOptions = {{}}; // JS object literal -> {{{{}}}}
        }} else if (typeof optionsOrCallback !== 'object' || optionsOrCallback === null) {{
            actualOptions = {{}}; // JS object literal -> {{{{}}}}
        }}

        process.stdout.write(String(query));

        // JS Arrow function -> (...) => {{ ... }}
        originalQuestion.call(rl, '', actualOptions, (answer) => {{
            process.stdout.write(answer + '\n');

            const keyBase = String(query).trim().replace(/[:?]/g, '').trim();
            // JS template literal -> `${{{{...}}}}`
            let key = keyBase || `input_$${{{{Object.keys(captured).length}}}}`;

            let count = 1;
            const originalKey = key;
            while (captured.hasOwnProperty(key)) {{
                count++;
                // JS template literal -> `${{{{...}}}}`
                key = `$${{{{originalKey}}}}_$${{{{count}}}}`;
            }}
            captured[key] = answer;

            if (actualCallback) {{
                try {{
                    actualCallback(answer);
                }} catch (cbError) {{
                    console.error('Capture wrapper error: Exception in readline callback:', cbError);
                }}
            }}
        }});
    }};

}} catch (e) {{
    console.error('Capture wrapper error initializing readline:', e);
    gracefulExit(1);
}}

// --- Target Script Execution Logic ---
try {{
    const scriptRelativePath = process.argv[2];
    if (!scriptRelativePath) {{
        throw new Error('No target script provided.');
    }}
    const scriptAbsolutePath = path.resolve(process.cwd(), scriptRelativePath);

    if (!fs.existsSync(scriptAbsolutePath)) {{
        // JS template literal -> `${{{{...}}}}`
         throw new Error(`Target script not found at $${{{{scriptAbsolutePath}}}}`);
    }}
    process.argv = [process.argv[0], scriptAbsolutePath, ...process.argv.slice(3)];

    // console.log(`Debug JS: Executing target script: $${{{{scriptAbsolutePath}}}}`);
    // console.log(`Debug JS: Target script argv: $${{{{JSON.stringify(process.argv)}}}}`);

    require(scriptAbsolutePath);
    // console.log(`Debug JS: Target script $${{{{scriptAbsolutePath}}}} finished synchronous execution.`);

}} catch (e) {{
    console.error('Capture wrapper FATAL ERROR during script execution:', e);
    gracefulExit(1);
}}

// console.log(""Debug JS: Capture wrapper script finished synchronous execution."");

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }


} // End of class InteractiveProxyRunner
