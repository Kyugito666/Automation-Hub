using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.IO;

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

        // === TAMBAHKAN PENGECEKAN DI SINI ===
        if (Program.InteractiveBotCancelled)
        {
            AnsiConsole.MarkupLine("[yellow]Skipping remaining steps for this bot due to cancellation.[/]");
            // File capture .tmp mungkin sudah/belum dihapus oleh RunBotInCaptureMode,
            // tapi kita tidak melanjutkan proses penyimpanan/trigger.
            return; // Langsung keluar dari method ini
        }
        // ===================================

        // Kode di bawah ini hanya jalan jika TIDAK di-cancel
        if (capturedInputs == null || capturedInputs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No inputs captured. Bot may not be interactive.[/]");

            // Tanya hanya jika tidak dicancel dan tidak ada input
            var runDirect = AnsiConsole.Confirm("Run directly on GitHub Actions without inputs?");
            if (!runDirect) return;

            capturedInputs = new Dictionary<string, string>(); // Buat dictionary kosong
        }

        // Simpan file (kosong atau berisi)
        var inputsFile = Path.Combine(InputsDir, $"{bot.Name}.json");
        var inputsJson = JsonSerializer.Serialize(capturedInputs, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(inputsFile, inputsJson);
        AnsiConsole.MarkupLine($"[green]✓ Inputs saved to: {inputsFile}[/]");

        // Tampilkan tabel jika ada input
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

        // Tanya trigger hanya jika tidak dicancel
        AnsiConsole.MarkupLine("\n[yellow]Step 2: Trigger remote execution on GitHub Actions...[/]");
        if (!AnsiConsole.Confirm("Proceed with remote execution?"))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return;
        }

        await GitHubDispatcher.TriggerBotWithInputs(bot, capturedInputs);
        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely![/]");
    }

    // ... (RunBotInCaptureMode, CreatePythonCaptureWrapper, CreateJavaScriptCaptureWrapper tidak berubah dari versi sebelumnya) ...
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
            // Reset cancel flag tepat sebelum menjalankan
            Program.InteractiveBotCancelled = false;
            await ShellHelper.RunInteractive(executor, args, absoluteBotPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Execution error: {ex.Message}[/]");
            AnsiConsole.MarkupLine($"[dim]Command: {executor} {args}[/]");
            AnsiConsole.MarkupLine($"[dim]Working Dir: {absoluteBotPath}[/]");
        }
        // Jangan reset flag di sini, biarkan Program.cs yang handle

        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");

        // Cek apakah file capture ADA setelah proses selesai (atau dicancel)
        if (File.Exists(inputCapturePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(inputCapturePath);
                // Coba deserialisasi, tangani jika file korup/kosong karena cancel mendadak
                try {
                     inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                              ?? new Dictionary<string, string>();
                } catch (JsonException jsonEx) {
                     AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse capture file (might be incomplete due to cancel): {jsonEx.Message}[/]");
                     inputs = new Dictionary<string, string>(); // Anggap kosong jika parse gagal
                }
                File.Delete(inputCapturePath);
                 AnsiConsole.MarkupLine($"[green]✓ Input capture file processed and deleted.[/]");
            }
            catch (IOException ioEx)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not process/delete capture file: {ioEx.Message}[/]");
                 // Tetap lanjutkan dengan input kosong jika file tidak bisa dibaca/dihapus
                 inputs = new Dictionary<string, string>();
            }
        } else if (!Program.InteractiveBotCancelled) {
             // Hanya tampilkan warning jika TIDAK dicancel dan file tidak ada
             AnsiConsole.MarkupLine($"[yellow]Warning: Input capture file not found at {inputCapturePath} after normal execution.[/]");
        } else {
             // Jika dicancel dan file tidak ada (mungkin belum sempat dibuat/ditulis) -> normal
             AnsiConsole.MarkupLine($"[grey]Input capture file not created (expected due to cancellation).[/]");
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

        // Kembalikan input yang berhasil di-capture (bisa jadi kosong)
        return inputs;
    }
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
    print(f'\nCapture wrapper info: Received signal {{sig}}. Cleaning up...', file=sys.stderr)
    # The 'finally' block will handle saving the captured data.
    # Exit with a code indicating interruption, if possible.
    sys.exit(128 + sig) # Standard exit code for signals

signal.signal(signal.SIGINT, handle_signal) # Ctrl+C
signal.signal(signal.SIGTERM, handle_signal) # Termination signal


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
        # Write directly to buffer to bypass potential wrapper encoding issues?
        sys.stdout.buffer.write(prompt.encode('utf-8', errors='replace'))
        sys.stdout.flush()
        #response = _original_input() # Read input without prompt arg
        response = sys.stdin.readline().rstrip('\n\r') # Read directly from stdin

        # If readline returns None (EOF), treat as EOFError
        if response is None:
            raise EOFError(""Stdin closed"")

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
        raise # Re-raise for the target script
    except KeyboardInterrupt: # Handle Ctrl+C during input specifically
        print('\nCapture wrapper info: KeyboardInterrupt during input.', file=sys.stderr)
        raise # Re-raise for signal handler or outer try/except
    except Exception as input_err:
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
abs_output_path = os.path.abspath(CAPTURE_OUTPUT_PATH) # Define here for finally block

try: # Outer try for execution and saving
    if script_to_run:
        try: # Inner try specifically for script execution
            # Prepare environment for the target script
            sys.argv = [script_to_run] + original_argv[2:] # Target script sees its name + remaining args
            script_dir = os.path.dirname(script_to_run)

            print(f'--- Starting Target Script: {{os.path.basename(script_to_run)}} ---', file=sys.stderr)

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
            script_executed_successfully = (exit_code == 0)
        except KeyboardInterrupt:
             print(f'\nCapture wrapper info: KeyboardInterrupt caught during script execution.', file=sys.stderr)
             exit_code = 130 # Standard exit code for SIGINT
        except Exception as e:
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
            # Ensure captured is serializable (handle potential non-string values if necessary)
            serializable_captured = {{k: str(v) for k, v in captured.items()}}
            json.dump(serializable_captured, f, indent=2, ensure_ascii=False)
        print(f'Capture wrapper info: Captured data saved to {{abs_output_path}}', file=sys.stderr)
    except Exception as save_err:
        print(f'Capture wrapper FATAL ERROR: Failed to write capture file {{abs_output_path}}: {{save_err}}', file=sys.stderr)
        # If saving fails, we might want to ensure the exit code reflects an error
        if exit_code == 0: exit_code = 1

# Exit with the determined code. If signal handler exited, this won't be reached.
# print(f'Capture wrapper info: Exiting wrapper with code {{exit_code}}.', file=sys.stderr)
# sys.exit(exit_code) # Let C# determine end by process exit

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
let isExiting = false; // Flag to prevent multiple exit attempts

// --- Save Function ---
function saveCaptureData() {{
    if (isExiting) return; // Avoid saving multiple times during exit cascade
    // console.log(`Debug JS: Attempting save to ${{absOutputPath}}`);
    try {{
        const outputDir = path.dirname(absOutputPath);
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
    // console.log(`Debug JS: Initiating exit with code/signal: ${{signalOrCode}}`);
    saveCaptureData(); // Save data first
    if (rl && !rl.closed) {{
        // console.log('Debug JS: Closing readline.');
        rl.close(); // Closing readline might trigger 'exit' again, handled by isExiting flag
    }}
    // Set exit code if it's a number, otherwise use default
    if (typeof signalOrCode === 'number') {{
         process.exitCode = signalOrCode;
    }}
     // Let node exit naturally after closing streams / handling signals if possible
     // process.exit() might cut off async operations or the save itself
     // Ensure process exits even if readline close hangs? Add a timeout?
     setTimeout(() => {{ process.exit(process.exitCode || 0); }}, 200); // Force exit after 200ms if needed
}}

process.on('exit', (code) => {{
    // console.log(`Debug JS: 'exit' event triggered with code ${{code}}.`);
    if (!isExiting) {{ // Should ideally be set by gracefulExit, but as fallback
         saveCaptureData();
    }
}});
process.on('SIGINT', () => {{ /* console.log('Debug JS: SIGINT received.');*/ gracefulExit(130); }}); // 130 = 128 + 2
process.on('SIGTERM', () => {{ /* console.log('Debug JS: SIGTERM received.');*/ gracefulExit(143); }}); // 143 = 128 + 15


// --- Input Capture Setup ---
try {{
    const nullStream = new Writable({{
      write(chunk, encoding, callback) {{ callback(); }}
    }});

    rl = readline.createInterface({{
        input: process.stdin,
        output: nullStream, // Prevent readline from echoing input
        prompt: ''
    }});

    const originalQuestion = rl.question;

    rl.question = function(query, optionsOrCallback, callback) {{
        let actualCallback = callback;
        let actualOptions = optionsOrCallback;

        if (typeof optionsOrCallback === 'function') {{
            actualCallback = optionsOrCallback;
            actualOptions = {{}};
        }} else if (typeof optionsOrCallback !== 'object' || optionsOrCallback === null) {{
            actualOptions = {{}};
        }}

        // Manually write the prompt to the real stdout
        process.stdout.write(String(query));

        // Call original question - it reads input but shouldn't echo due to output: nullStream
        originalQuestion.call(rl, '', actualOptions, (answer) => {{ // Pass empty prompt to original
            // Manually echo the answer AFTER it's received
            process.stdout.write(answer + '\n'); // Add newline after answer

            const keyBase = String(query).trim().replace(/[:?]/g, '').trim();
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
         throw new Error(`Target script not found at ${{scriptAbsolutePath}}`);
    }}
    process.argv = [process.argv[0], scriptAbsolutePath, ...process.argv.slice(3)];

    // console.log(`Debug JS: Executing target script: ${{scriptAbsolutePath}}`);
    // console.log(`Debug JS: Target script argv: ${{JSON.stringify(process.argv)}}`);

    require(scriptAbsolutePath);
    // console.log(`Debug JS: Target script ${{scriptAbsolutePath}} finished synchronous execution.`);

    // If the script runs and finishes synchronously without keeping the event loop alive,
    // Node.js might exit before the 'exit' handler fully runs or saves.
    // However, adding artificial delays is bad. Rely on correct exit handling.

}} catch (e) {{
    console.error('Capture wrapper FATAL ERROR during script execution:', e);
    gracefulExit(1); // Exit with error code
}}

// console.log(""Debug JS: Capture wrapper script finished synchronous execution."");

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }

} // End of class InteractiveProxyRunner
