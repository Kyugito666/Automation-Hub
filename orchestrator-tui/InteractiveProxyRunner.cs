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
        var inputCapturePath = Path.Combine(botPath, ".input-capture.tmp");

        string jsWrapperFileName = "capture_wrapper.cjs";
        string jsWrapperFullPath = Path.Combine(botPath, jsWrapperFileName);
        string pyWrapperFileName = "capture_wrapper.py";
        string pyWrapperFullPath = Path.Combine(botPath, pyWrapperFileName);

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

        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(botPath, bot.Type);

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
            args = $"-u {pyWrapperFileName} {originalArgs}";
        }
        else if (bot.Type == "javascript")
        {
            executor = "node"; // Wrapper *harus* dijalankan dengan node
            string targetScriptArg;

            // Jika command asli 'npm start', kita perlu tebak file target
            if (originalExecutor == "npm" && originalArgs == "start")
            {
                string mainJs = File.Exists(Path.Combine(botPath, "index.js")) ? "index.js"
                              : File.Exists(Path.Combine(botPath, "main.js")) ? "main.js"
                              : File.Exists(Path.Combine(botPath, "bot.js")) ? "bot.js"
                              : "";
                if (!string.IsNullOrEmpty(mainJs))
                {
                    targetScriptArg = mainJs;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗ Tidak bisa otomatis mendeteksi file JS utama untuk 'npm start' di {bot.Name}. Capture mungkin gagal.[/]");
                    targetScriptArg = "index.js"; // Coba tebak index.js
                }
            }
            else // Jika command asli adalah "node file.js"
            {
                 targetScriptArg = originalArgs; // Argumen asli adalah nama file target
            }
            args = $"{jsWrapperFileName} {targetScriptArg}"; // Wrapper + file target
        }
        else
        {
             AnsiConsole.MarkupLine($"[red]✗ Tipe bot tidak dikenal: {bot.Type}[/]");
             return null;
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
            if (bot.Type == "python" && File.Exists(pyWrapperFullPath))
                File.Delete(pyWrapperFullPath);

            if (bot.Type == "javascript" && File.Exists(jsWrapperFullPath))
                File.Delete(jsWrapperFullPath);
        }
        catch { /* Abaikan error cleanup */ }

        return inputs;
    }

    // === METHOD PYTHON WRAPPER (DIUPDATE) ===
    private static async Task CreatePythonCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        // Escape outputPath untuk string literal Python di C#
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        // String Python wrapper (hati-hati dengan escaping '{', '}', '"')
        var wrapper = $@"#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import sys
import json
import builtins
import os
import io # Required for reconfigure
import traceback # Added for better error reporting

# Set encoding explicitly (safer for Windows)
try:
    if sys.stdout.encoding != 'utf-8':
        sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    if sys.stdin.encoding != 'utf-8':
        sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding='utf-8', errors='replace')
except Exception as enc_err:
    print(f'Capture wrapper warning: Could not reconfigure stdio encoding: {{enc_err}}', file=sys.stderr)


captured = {{}}
_original_input = builtins.input

def capturing_input(prompt=''):
    try:
        response = _original_input(prompt)
        # More robust key generation: handle potential non-string prompts? Use repr?
        key_base = str(prompt).strip().rstrip(':').strip()
        key = key_base or f'input_{{len(captured)}}'
        # Handle duplicate keys (e.g., empty prompt multiple times)
        count = 1
        final_key = key
        while final_key in captured:
            count += 1
            final_key = f'{{key}}_{{count}}'

        captured[final_key] = response
        return response
    except EOFError:
        print('Capture wrapper warning: EOFError detected during input, script might expect piped input.', file=sys.stderr)
        raise # Re-raise EOFError as the script might handle it
    except Exception as input_err:
        print(f'Capture wrapper error during input call: {{input_err}}', file=sys.stderr)
        return '' # Return empty string on error? Or re-raise?

builtins.input = capturing_input

script_executed = False
original_argv = list(sys.argv) # Make a copy
script_to_run = None

if len(original_argv) > 1:
    script_arg = original_argv[1] # Argumen setelah wrapper.py adalah target script
    # Assume script_arg is relative to the CWD (botPath set by C#)
    script_path = os.path.abspath(script_arg)
    if os.path.exists(script_path):
        script_to_run = script_path
    else:
        print(f'Capture wrapper error: Target script specified but not found: {{script_path}}', file=sys.stderr)
else:
    # Try importing common entry points if no script specified
    entry_points = ['run.py', 'main.py', 'bot.py']
    for entry in entry_points:
        entry_path = os.path.abspath(entry)
        if os.path.exists(entry_path):
            print(f'Capture wrapper info: No script argument, found {{entry}}. Running it.', file=sys.stderr)
            script_to_run = entry_path
            break
    if script_to_run is None:
         print('Capture wrapper error: No script argument and run.py/main.py/bot.py not found in CWD.', file=sys.stderr)

# Ensure output path is absolute
abs_output_path = os.path.abspath('{escapedOutputPath}')

if script_to_run:
    try:
        # Set sys.argv for the target script: [target_script_path, ...original args after target...]
        sys.argv = [script_to_run] + original_argv[2:]

        # Prepare execution environment
        script_dir = os.path.dirname(script_to_run)
        # Add script's directory to the beginning of sys.path to handle local imports
        if script_dir not in sys.path:
             sys.path.insert(0, script_dir)

        print(f'Capture wrapper info: Executing {{script_to_run}}', file=sys.stderr)
        print(f'Capture wrapper info: Target argv: {{sys.argv}}', file=sys.stderr)
        print(f'Capture wrapper info: CWD: {{os.getcwd()}}', file=sys.stderr)

        with open(script_to_run, 'r', encoding='utf-8') as f:
            source = f.read()
            code = compile(source, script_to_run, 'exec')
            # Provide globals including __file__ so the script knows its location
            script_globals = {{'__name__': '__main__', '__file__': script_to_run}}
            exec(code, script_globals)
            script_executed = True
            print(f'Capture wrapper info: Script {{script_to_run}} finished execution.', file=sys.stderr)

    except SystemExit as sysexit:
        print(f'Capture wrapper info: Script exited with code {{sysexit.code}}.', file=sys.stderr)
        # We still want to save captured data even if script exits
    except Exception as e:
        print(f'Capture wrapper FATAL ERROR during script execution: {{e}}', file=sys.stderr)
        traceback.print_exc(file=sys.stderr) # Print full traceback to stderr
    finally:
        # Save captured data regardless of script success/failure/exit
        try:
            # Ensure the directory exists
            output_dir = os.path.dirname(abs_output_path)
            if not os.path.exists(output_dir):
                os.makedirs(output_dir, exist_ok=True)

            with open(abs_output_path, 'w', encoding='utf-8') as f:
                json.dump(captured, f, indent=2, ensure_ascii=False)
            print(f'Capture wrapper info: Captured data saved to {{abs_output_path}}', file=sys.stderr)
        except Exception as save_err:
            print(f'Capture wrapper FATAL ERROR: Failed to write capture file {{abs_output_path}}: {{save_err}}', file=sys.stderr)
else:
     # No script found to execute, save empty capture file? Or just log?
     print(f'Capture wrapper warning: No script was executed. Saving potentially empty capture file.', file=sys.stderr)
     try:
         output_dir = os.path.dirname(abs_output_path)
         if not os.path.exists(output_dir):
             os.makedirs(output_dir, exist_ok=True)
         with open(abs_output_path, 'w', encoding='utf-8') as f:
             json.dump(captured, f, indent=2, ensure_ascii=False) # Save empty if nothing captured
     except Exception as save_err:
            print(f'Capture wrapper FATAL ERROR: Failed to write empty capture file {{abs_output_path}}: {{save_err}}', file=sys.stderr)

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }

    // === METHOD JAVASCRIPT WRAPPER (DIUPDATE) ===
    private static async Task CreateJavaScriptCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        // Escape outputPath untuk string literal JavaScript di C#
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        // String JavaScript wrapper (hati-hati escaping '{', '}', '"', '`')
        var wrapper = $@"// Force CommonJS mode by using .cjs extension
const fs = require('fs');
const path = require('path');
const readline = require('readline');

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

  const originalQuestion = rl.question; // Keep reference to original

  // Overwrite rl.question to capture input
  rl.question = function(query, optionsOrCallback, callback) {{
      let actualCallback = callback;
      let actualOptions = optionsOrCallback;

      // Handle the overloaded signature: question(query, callback)
      if (typeof optionsOrCallback === 'function') {{
          actualCallback = optionsOrCallback;
          actualOptions = {{}}; // No options provided
      }}

      // Call original question method with potentially adjusted args
      originalQuestion.call(rl, query, actualOptions, (answer) => {{
          // More robust key generation
          const keyBase = query.toString().trim().replace(/[:?]/g, '').trim();
          let key = keyBase || `input_${{Object.keys(captured).length}}`;
          // Handle duplicate keys
          let count = 1;
          const originalKey = key;
          while (captured.hasOwnProperty(key)) {{
              count++;
              key = `${{originalKey}}_${{count}}`;
          }}

          captured[key] = answer;

          if(actualCallback) {{ // If a callback was provided, call it
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
    // Attempt to save any captured data before exiting
    saveCaptureData();
    process.exit(1);
}}


// --- Save Captured Data Function ---
function saveCaptureData() {{
     // console.log(`Debug: Attempting to save capture data to ${{absOutputPath}}`); // Optional debug
    try {{
      // Ensure the directory exists before writing
      const outputDir = path.dirname(absOutputPath);
      if (!fs.existsSync(outputDir)){{
        fs.mkdirSync(outputDir, {{ recursive: true }});
      }}
      fs.writeFileSync(absOutputPath, JSON.stringify(captured, null, 2));
       // console.log(`Debug: Captured data successfully written.`); // Optional debug
    }} catch (e) {{
      console.error('Capture wrapper FATAL ERROR: Failed to write capture file:', e);
    }}
}}

// --- Exit Handling ---
process.on('exit', (code) => {{
    // console.log(`Capture wrapper: Process exiting with code ${{code}}. Saving data.`); // Optional debug
    saveCaptureData(); // Save data on normal exit
    if (rl && !rl.closed) {{
         // console.log("Debug: Closing readline interface on exit."); // Optional debug
         rl.close();
    }}
}});

// Gracefully handle termination signals
const handleSignalExit = (signal) => {{
    // console.log(`Capture wrapper: Received signal ${{signal}}. Exiting and saving data.`); // Optional debug
    // saveCaptureData(); // saveCaptureData is called by 'exit' event anyway
    if (rl && !rl.closed) {{
         // console.log("Debug: Closing readline interface on signal."); // Optional debug
         rl.close();
    }}
    // Let the 'exit' event handle saving. Exit with conventional signal code offset.
    // process.exit(128 + (signal === 'SIGINT' ? 2 : 15)); // SIGINT=2, SIGTERM=15
    process.exit(0); // Simpler: just trigger normal exit flow
}};
process.on('SIGINT', () => handleSignalExit('SIGINT'));
process.on('SIGTERM', () => handleSignalExit('SIGTERM'));

// --- Target Script Execution Logic ---
let scriptExecuted = false;
try {{
    // process.argv[0] is node, [1] is this script (.cjs), [2] is the target script argument from C#
    const scriptRelativePath = process.argv[2];
    if (!scriptRelativePath) {{
        console.error('Capture wrapper error: No target script provided.');
        // saveCaptureData(); // Save before exiting
        process.exit(1);
    }}
     // Resolve the absolute path of the target script relative to CWD (botPath set by C#)
    const scriptAbsolutePath = path.resolve(process.cwd(), scriptRelativePath);

    if (!fs.existsSync(scriptAbsolutePath)) {{
         console.error(`Capture wrapper error: Target script not found at ${{scriptAbsolutePath}}`);
         // saveCaptureData(); // Save before exiting
         process.exit(1);
    }}

    // Set argv for the target script: [node executable, target script path, ...original args...]
    // Original args start from process.argv[3]
    process.argv = [process.argv[0], scriptAbsolutePath, ...process.argv.slice(3)];

    // console.log(`Debug: Executing target script: ${{scriptAbsolutePath}}`); // Optional debug
    // console.log(`Debug: Target script argv: ${{JSON.stringify(process.argv)}}`); // Optional debug

    // Check if the target script is likely an ES module (needs dynamic import)
    // Basic check: file extension and potentially package.json type (though less reliable here)
    // For simplicity, stick with require as the wrapper is CommonJS.
    // If the target *is* ESM and top-level await is used, this might fail.
    require(scriptAbsolutePath); // Execute the target script
    scriptExecuted = true;
    // console.log(`Debug: Target script ${{scriptAbsolutePath}} finished execution (or initiated async ops).`); // Optional debug

}} catch (e) {{
    console.error('Capture wrapper FATAL ERROR during script execution:', e);
    // saveCaptureData(); // Save before exiting
    // Exit with error code if script fails catastrophically during load/sync execution
    // process.exit(1);
}}

// If the script didn't execute synchronously or threw an error early,
// we might reach here. We rely on the 'exit' handler to save data.
// console.log("Debug: Capture wrapper script finished synchronous execution."); // Optional debug

// DO NOT add artificial keep-alive like setTimeout. Let the target script manage its lifecycle.

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }
}
