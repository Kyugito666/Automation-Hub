using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.IO; // Ditambahkan

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string InputsDir = "../.bot-inputs";

    public static async Task CaptureAndTriggerBot(BotEntry bot)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Interactive Proxy Mode: {bot.Name} ===[/]");
        AnsiConsole.MarkupLine("[yellow]Step 1: Capturing inputs locally...[/]");

        Directory.CreateDirectory(InputsDir);

        var botPath = Path.Combine("..", bot.Path);
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return;
        }

        await BotRunner.InstallDependencies(botPath, bot.Type);

        var capturedInputs = await RunBotInCaptureMode(botPath, bot);

        if (capturedInputs == null || capturedInputs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No inputs captured. Bot may not be interactive.[/]");

            var runDirect = AnsiConsole.Confirm("Run directly on GitHub Actions without inputs?");
            if (!runDirect) return;

            capturedInputs = new Dictionary<string, string>();
        }

        var inputsFile = Path.Combine(InputsDir, $"{bot.Name}.json");
        var inputsJson = JsonSerializer.Serialize(capturedInputs, new JsonSerializerOptions
        {
            WriteIndented = true
        });
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
        // Pastikan path absolut untuk menghindari ambiguitas
        var absoluteBotPath = Path.GetFullPath(botPath);
        var inputCapturePath = Path.Combine(absoluteBotPath, ".input-capture.tmp");

        string jsWrapperFileName = "capture_wrapper.cjs";
        string jsWrapperFullPath = Path.Combine(absoluteBotPath, jsWrapperFileName);
        string pyWrapperFileName = "capture_wrapper.py";
        string pyWrapperFullPath = Path.Combine(absoluteBotPath, pyWrapperFileName);

        // Hapus wrapper lama jika ada sebelum membuat yang baru
        if (File.Exists(pyWrapperFullPath)) File.Delete(pyWrapperFullPath);
        if (File.Exists(jsWrapperFullPath)) File.Delete(jsWrapperFullPath);
        if (File.Exists(inputCapturePath)) File.Delete(inputCapturePath);


        if (bot.Type == "python")
        {
            await CreatePythonCaptureWrapper(pyWrapperFullPath, inputCapturePath); // Path lengkap
        }
        else if (bot.Type == "javascript")
        {
            await CreateJavaScriptCaptureWrapper(jsWrapperFullPath, inputCapturePath); // Path lengkap
        }

        AnsiConsole.MarkupLine("[dim]Running bot interactively (local device)...[/]");
        AnsiConsole.MarkupLine("[dim]Answer all prompts normally. Inputs will be captured.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(absoluteBotPath, bot.Type);

        string executor;
        string args;

        if (string.IsNullOrEmpty(originalExecutor))
        {
             AnsiConsole.MarkupLine($"[red]✗ Tidak bisa menemukan command utama (npm start atau file .js/.py) untuk {bot.Name}.[/]");
             return null;
        }

        if (bot.Type == "python")
        {
            executor = "python";
            // Argumen wrapper diikuti argumen asli (nama file python)
            // Pastikan originalArgs dikutip jika mengandung spasi
            args = $"-u \"{pyWrapperFileName}\" {originalArgs}";
        }
        else if (bot.Type == "javascript")
        {
            executor = "node"; // Wrapper *harus* dijalankan dengan node
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
                    AnsiConsole.MarkupLine($"[grey]Detected 'npm start', assuming target script is '{targetScriptArg}'[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗ Tidak bisa otomatis mendeteksi file JS utama untuk 'npm start' di {bot.Name}. Capture mungkin gagal. Mencoba 'index.js'.[/]");
                    targetScriptArg = "index.js"; // Default tebakan
                }
            }
            else // Jika command asli adalah "node file.js"
            {
                 // originalArgs harusnya adalah nama file .js nya saja
                 targetScriptArg = originalArgs;
            }
            // Pastikan targetScriptArg dikutip jika mengandung spasi
            args = $"\"{jsWrapperFileName}\" \"{targetScriptArg}\""; // Wrapper + file target
        }
        else
        {
             AnsiConsole.MarkupLine($"[red]✗ Tipe bot tidak dikenal: {bot.Type}[/]");
             return null;
        }


        try
        {
            // Jalankan dari path bot
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
                inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
                File.Delete(inputCapturePath);
                 AnsiConsole.MarkupLine($"[green]✓ Input capture file processed and deleted.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse captured inputs: {ex.Message}[/]");
            }
        } else {
             AnsiConsole.MarkupLine($"[yellow]Warning: Input capture file not found at {inputCapturePath}[/]");
        }

        // Cleanup wrapper
        try
        {
            if (File.Exists(pyWrapperFullPath))
                File.Delete(pyWrapperFullPath);

            if (File.Exists(jsWrapperFullPath))
                File.Delete(jsWrapperFullPath);
        }
        catch { /* Abaikan error cleanup */ }

        return inputs;
    }

    // === METHOD PYTHON WRAPPER (FIXED) ===
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
import traceback

# --- Configuration ---
CAPTURE_OUTPUT_PATH = r""{escapedOutputPath}"" # Use raw string for path

# --- Encoding Setup ---
try:
    if sys.stdout.encoding != 'utf-8':
        sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    if sys.stdin.encoding != 'utf-8':
        sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding='utf-8', errors='replace')
except Exception as enc_err:
    print(f'Capture wrapper warning: Could not reconfigure stdio encoding: {{enc_err}}', file=sys.stderr)

# --- Input Capture Logic ---
captured = {{}}
_original_input = builtins.input

def capturing_input(prompt=''):
    global captured
    try:
        # Display the prompt using the original stdout encoding behavior if possible
        print(prompt, end='', flush=True)
        response = _original_input() # Read input without prompt arg, as we printed it manually

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
        print('Capture wrapper warning: EOFError detected during input.', file=sys.stderr)
        raise
    except Exception as input_err:
        print(f'Capture wrapper error during input call: {{input_err}}', file=sys.stderr)
        return '' # Return empty string on error

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
        print(f'Capture wrapper error: Target script specified but not found: {{script_path}}', file=sys.stderr)
else:
    # Try common entry points if no script argument given
    entry_points = ['run.py', 'main.py', 'bot.py']
    for entry in entry_points:
        entry_path = os.path.abspath(entry)
        if os.path.exists(entry_path):
            print(f'Capture wrapper info: No script argument, found {{entry}}. Running it.', file=sys.stderr)
            script_to_run = entry_path
            break
    if script_to_run is None:
         print('Capture wrapper error: No script argument and no common entry point (run.py/main.py/bot.py) found.', file=sys.stderr)

# --- Script Execution and Cleanup ---
script_executed_successfully = False
exit_code = 1 # Default to error

if script_to_run:
    try:
        # Prepare environment for the target script
        sys.argv = [script_to_run] + original_argv[2:] # Target script sees its name + remaining args
        script_dir = os.path.dirname(script_to_run)
        # Add script's directory to sys.path *if needed* (usually CWD is enough)
        # if script_dir not in sys.path:
        #     sys.path.insert(0, script_dir)

        print(f'--- Starting Target Script: {{os.path.basename(script_to_run)}} ---', file=sys.stderr)
        # print(f'Debug - CWD: {{os.getcwd()}}', file=sys.stderr)
        # print(f'Debug - Target argv: {{sys.argv}}', file=sys.stderr)

        with open(script_to_run, 'r', encoding='utf-8') as f:
            source = f.read()
            code = compile(source, script_to_run, 'exec')
            script_globals = {{'__name__': '__main__', '__file__': script_to_run}}
            exec(code, script_globals)
            script_executed_successfully = True
            exit_code = 0 # Assume success if no exception/SystemExit
            print(f'\n--- Target Script Finished ---', file=sys.stderr)

    except SystemExit as sysexit:
        exit_code = sysexit.code if isinstance(sysexit.code, int) else (0 if sysexit.code is None else 1)
        print(f'Capture wrapper info: Script exited via SystemExit with code {{exit_code}}.', file=sys.stderr)
        script_executed_successfully = (exit_code == 0) # Consider exit 0 as success
    except Exception as e:
        print(f'Capture wrapper FATAL ERROR during script execution: {{e}}', file=sys.stderr)
        traceback.print_exc(file=sys.stderr)
        exit_code = 1 # Explicitly set error code
    finally:
        # Save captured data regardless of outcome
        try:
            output_dir = os.path.dirname(CAPTURE_OUTPUT_PATH)
            os.makedirs(output_dir, exist_ok=True)
            with open(CAPTURE_OUTPUT_PATH, 'w', encoding='utf-8') as f:
                json.dump(captured, f, indent=2, ensure_ascii=False)
            print(f'Capture wrapper info: Captured data saved to {{CAPTURE_OUTPUT_PATH}}', file=sys.stderr)
        except Exception as save_err:
            print(f'Capture wrapper FATAL ERROR: Failed to write capture file {{CAPTURE_OUTPUT_PATH}}: {{save_err}}', file=sys.stderr)
            exit_code = 1 # Mark failure if saving fails
else:
     print(f'Capture wrapper warning: No script was executed. Saving empty capture file.', file=sys.stderr)
     exit_code = 1 # No script found is an error state for capture
     try:
         output_dir = os.path.dirname(CAPTURE_OUTPUT_PATH)
         os.makedirs(output_dir, exist_ok=True)
         with open(CAPTURE_OUTPUT_PATH, 'w', encoding='utf-8') as f:
             json.dump(captured, f, indent=2, ensure_ascii=False)
     except Exception as save_err:
            print(f'Capture wrapper FATAL ERROR: Failed to write empty capture file {{CAPTURE_OUTPUT_PATH}}: {{save_err}}', file=sys.stderr)

# Exit with the code from the target script (or error code)
# sys.exit(exit_code) # Avoid exiting the wrapper itself if possible, let C# detect end

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }


    // === METHOD JAVASCRIPT WRAPPER (FIXED) ===
    private static async Task CreateJavaScriptCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        // Escape outputPath for the generated JS string literal in C#
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        // Use @"" and double braces {{ }}
        var wrapper = $@"// Force CommonJS mode by using .cjs extension
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const process = require('process'); // Ensure process is available

const captured = {{}};
let rl = null; // Declare rl, initialize later

// Ensure output path is absolute from the start
const absOutputPath = path.resolve('{escapedOutputPath}');

// --- Input Capture Setup ---
try {{
  rl = readline.createInterface({{
      input: process.stdin,
      output: process.stdout
  }});

  // Keep original 'question' method reference
  const originalQuestion = rl.question;

  // Overwrite rl.question
  rl.question = function(query, optionsOrCallback, callback) {{
      let actualCallback = callback;
      let actualOptions = optionsOrCallback;

      // Handle overloaded signature: question(query, callback)
      if (typeof optionsOrCallback === 'function') {{
          actualCallback = optionsOrCallback;
          actualOptions = {{}};
      }} else if (typeof optionsOrCallback !== 'object' || optionsOrCallback === null) {{
          // Ensure options is an object if provided incorrectly
          actualOptions = {{}};
      }}

      // Capture the prompt and call original question
      // We still let the original function print the prompt
      originalQuestion.call(rl, query, actualOptions, (answer) => {{
          const keyBase = String(query).trim().replace(/[:?]/g, '').trim(); // Ensure query is string
          let key = keyBase || `input_${{Object.keys(captured).length}}`;

          let count = 1;
          const originalKey = key;
          while (captured.hasOwnProperty(key)) {{
              count++;
              key = `${{originalKey}}_${{count}}`;
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
    saveCaptureData(); // Attempt save before exit
    process.exit(1);
}}


// --- Save Captured Data Function ---
function saveCaptureData() {{
    // console.log(`Debug: Attempting to save capture data to ${{absOutputPath}}`); // Optional debug
    try {{
        const outputDir = path.dirname(absOutputPath);
        if (!fs.existsSync(outputDir)) {{
            fs.mkdirSync(outputDir, {{ recursive: true }});
        }}
        fs.writeFileSync(absOutputPath, JSON.stringify(captured, null, 2));
        // console.log(`Debug: Captured data successfully written.`); // Optional debug
    }} catch (e) {{
        console.error('Capture wrapper FATAL ERROR: Failed to write capture file:', e);
    }}
}}

// --- Exit Handling ---
let isExiting = false;
process.on('exit', (code) => {{
    if (!isExiting) {{
        isExiting = true; // Prevent recursive calls if rl.close() triggers exit
        // console.log(`Capture wrapper: Process exiting with code ${{code}}. Saving data.`); // Optional debug
        saveCaptureData();
        if (rl && !rl.closed) {{
             // console.log(""Debug: Closing readline interface on exit.""); // Optional debug
            rl.close();
        }}
    }}
}});

// Gracefully handle termination signals
const handleSignalExit = (signal) => {{
    if (!isExiting) {{
        // console.log(`Capture wrapper: Received signal ${{signal}}. Initiating exit.`); // Optional debug
        // No need to save data here, 'exit' event will handle it.
        // Close readline first if it's open
         if (rl && !rl.closed) {{
             // console.log(""Debug: Closing readline interface on signal.""); // Optional debug
             rl.close();
             // The close might trigger the 'exit' event, or we exit manually if needed
             // Add a small delay to allow close event to potentially trigger exit
             setTimeout(() => {{
                 if (!isExiting) process.exit(0); // Exit if rl.close() didn't trigger it
             }}, 50);
         }} else {{
            process.exit(0); // Trigger normal exit flow if readline wasn't active
         }}
    }}
}};
process.on('SIGINT', () => handleSignalExit('SIGINT'));
process.on('SIGTERM', () => handleSignalExit('SIGTERM'));


// --- Target Script Execution Logic ---
try {{
    // process.argv[0] = node, [1] = wrapper.cjs, [2] = target script arg
    const scriptRelativePath = process.argv[2];
    if (!scriptRelativePath) {{
        throw new Error('No target script provided.');
    }}

    // Resolve absolute path relative to CWD (set by C# runner)
    const scriptAbsolutePath = path.resolve(process.cwd(), scriptRelativePath);

    if (!fs.existsSync(scriptAbsolutePath)) {{
         throw new Error(`Target script not found at ${{scriptAbsolutePath}}`);
    }}

    // Prepare argv for the target script: [node executable, target script path, ...original args...]
    process.argv = [process.argv[0], scriptAbsolutePath, ...process.argv.slice(3)];

    // console.log(`Debug: Executing target script: ${{scriptAbsolutePath}}`); // Optional debug
    // console.log(`Debug: Target script argv: ${{JSON.stringify(process.argv)}}`); // Optional debug

    // Execute the target script using require (as wrapper is CommonJS)
    require(scriptAbsolutePath);
    // console.log(`Debug: Target script ${{scriptAbsolutePath}} finished synchronous execution.`); // Optional debug

}} catch (e) {{
    console.error('Capture wrapper FATAL ERROR during script execution:', e);
    // Exit with error code if script fails catastrophically during load/sync execution
    // Ensure data is saved via exit handler
    if (!isExiting) {{ // Avoid double exit call
        process.exitCode = 1; // Set exit code for the 'exit' event
        if (rl && !rl.closed) rl.close(); // Try to close readline
        else if (!isExiting) process.exit(1); // Force exit if readline wasn't running
    }}
}}

// console.log(""Debug: Capture wrapper script finished synchronous execution.""); // Optional debug

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }

} // End of class InteractiveProxyRunner
