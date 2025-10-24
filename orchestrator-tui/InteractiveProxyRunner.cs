using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading; // <-- Tambahkan using
using System.Threading.Tasks; // <-- Tambahkan using


namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string InputsDir = "../.bot-inputs";
    private const string VenvDirName = ".venv";

    // === MODIFIKASI: Tambahkan CancellationToken ===
    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        // ... (Bagian awal tidak berubah) ...
        AnsiConsole.MarkupLine($"[bold cyan]=== Interactive Proxy Mode: {bot.Name} ===[/]");
        AnsiConsole.MarkupLine("[yellow]Step 1: Capturing inputs locally...[/]");

        Directory.CreateDirectory(InputsDir);

        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return;
        }

        // Cek cancel sebelum install dependencies
        cancellationToken.ThrowIfCancellationRequested();
        await BotRunner.InstallDependencies(botPath, bot.Type);

        Dictionary<string, string>? capturedInputs = null;
        bool cancelledDuringRun = false; // Flag baru

        try
        {
            // === MODIFIKASI: Kirim CancellationToken ===
            capturedInputs = await RunBotInCaptureMode(botPath, bot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Tangkap pembatalan dari RunBotInCaptureMode
            cancelledDuringRun = true;
            AnsiConsole.MarkupLine("[yellow]Capture dibatalkan.[/]");
            // capturedInputs akan null atau kosong, file .tmp mungkin ada/tidak
            // Coba baca file .tmp kalaupun dibatalkan, mungkin ada data parsial
            var inputCapturePath = Path.Combine(botPath, ".input-capture.tmp");
             if (File.Exists(inputCapturePath)) {
                 try {
                     var json = await File.ReadAllTextAsync(inputCapturePath);
                     capturedInputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                     File.Delete(inputCapturePath); // Hapus setelah dibaca
                     AnsiConsole.MarkupLine("[grey]Data input parsial berhasil dibaca sebelum dibatalkan.[/]");
                 } catch (Exception ex) {
                     AnsiConsole.MarkupLine($"[yellow]Warning: Gagal membaca data input parsial: {ex.Message}[/]");
                     if (File.Exists(inputCapturePath)) try { File.Delete(inputCapturePath); } catch {} // Coba hapus
                     capturedInputs = new Dictionary<string, string>(); // Anggap kosong
                 }
             } else {
                  capturedInputs = new Dictionary<string, string>(); // Anggap kosong
             }
        }
        // Exception lain biarkan propagate? Atau tangkap di Program.cs?

        // Cek cancel *setelah* RunBotInCaptureMode selesai (baik normal maupun cancel)
        // Program.InteractiveBotCancelled masih bisa dipakai sebagai indikator global
        if (Program.InteractiveBotCancelled || cancelledDuringRun)
        {
            AnsiConsole.MarkupLine("[yellow]Skipping remaining steps for this bot due to cancellation.[/]");
            // Simpan input parsial jika ada (berguna untuk debug)
             if (capturedInputs != null && capturedInputs.Any()) {
                var inputsFilePartial = Path.Combine(InputsDir, $"{bot.Name}.partial.json");
                var inputsJsonPartial = JsonSerializer.Serialize(capturedInputs, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(inputsFilePartial, inputsJsonPartial);
                AnsiConsole.MarkupLine($"[grey]Input parsial disimpan ke: {inputsFilePartial}[/]");
             }
            return; // Keluar
        }


        // --- Lanjutkan hanya jika TIDAK dibatalkan ---

        if (capturedInputs == null || capturedInputs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No inputs captured. Bot may not be interactive, or capture failed.[/]");
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
            // ... (Tampilkan tabel input - tidak berubah) ...
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

    // === MODIFIKASI: Tambahkan CancellationToken ===
    private static async Task<Dictionary<string, string>?> RunBotInCaptureMode(string botPath, BotEntry bot, CancellationToken cancellationToken)
    {
        // ... (Bagian awal, pembuatan wrapper tidak berubah) ...
        var inputs = new Dictionary<string, string>();
        var absoluteBotPath = Path.GetFullPath(botPath);
        var inputCapturePath = Path.Combine(absoluteBotPath, ".input-capture.tmp");

        string jsWrapperFileName = "capture_wrapper.cjs";
        string jsWrapperFullPath = Path.Combine(absoluteBotPath, jsWrapperFileName);
        string pyWrapperFileName = "capture_wrapper.py";
        string pyWrapperFullPath = Path.Combine(absoluteBotPath, pyWrapperFileName);

        if (File.Exists(pyWrapperFullPath)) File.Delete(pyWrapperFullPath);
        if (File.Exists(jsWrapperFullPath)) File.Delete(jsWrapperFullPath);
        if (File.Exists(inputCapturePath)) File.Delete(inputCapturePath);

        cancellationToken.ThrowIfCancellationRequested(); // Cek sebelum buat wrapper
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

        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(absoluteBotPath, bot.Type);
        string executor;
        string args;

        if (string.IsNullOrEmpty(originalExecutor)) { /* ... (handle error) ... */
             AnsiConsole.MarkupLine($"[red]✗ Tidak bisa menemukan command utama (npm start, venv python, atau file .js/.py) untuk {bot.Name}.[/]");
             return null;
        }
        if (bot.Type == "python") { /* ... (tentukan executor/args python) ... */
             executor = originalExecutor; // Sudah path ke python venv
            args = $"-u \"{pyWrapperFileName}\" {originalArgs}";
        }
        else if (bot.Type == "javascript") { /* ... (tentukan executor/args js) ... */
            executor = "node"; // Wrapper pakai node global
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
                    AnsiConsole.MarkupLine($"[red]✗ Tidak bisa otomatis mendeteksi file JS utama untuk 'npm start' di {bot.Name}. Capture mungkin gagal. Mencoba 'index.js'.[/]");
                    targetScriptArg = "index.js";
                }
            } else { targetScriptArg = originalArgs; }
            args = $"\"{jsWrapperFileName}\" \"{targetScriptArg}\"";
        }
        else { /* ... (handle error tipe tidak dikenal) ... */
              AnsiConsole.MarkupLine($"[red]✗ Tipe bot tidak dikenal: {bot.Type}[/]");
             return null;
        }

        cancellationToken.ThrowIfCancellationRequested(); // Cek sebelum run

        // === MODIFIKASI: Kirim CancellationToken ke RunInteractive ===
        // Tidak perlu reset flag di sini, biarkan CancellationToken yang bekerja
        await ShellHelper.RunInteractive(executor, args, absoluteBotPath, cancellationToken);
        // Jika cancel, exception akan dilempar dari sini
        // =============================================================

        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");

        // Baca file .tmp *setelah* proses selesai (atau dibatalkan dan exception ditangkap di atas)
        // Logika pembacaan file .tmp tetap sama seperti sebelumnya
         if (File.Exists(inputCapturePath)) {
            try {
                var json = await File.ReadAllTextAsync(inputCapturePath);
                try {
                     inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                } catch (JsonException jsonEx) {
                     AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse capture file: {jsonEx.Message}[/]");
                     inputs = new Dictionary<string, string>();
                }
                File.Delete(inputCapturePath);
                 AnsiConsole.MarkupLine($"[green]✓ Input capture file processed and deleted.[/]");
            } catch (IOException ioEx) {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not process/delete capture file: {ioEx.Message}[/]");
                 inputs = new Dictionary<string, string>();
            }
        } else {
             // Jika file tidak ada setelah proses selesai (dan tidak dibatalkan), itu aneh
             if (!cancellationToken.IsCancellationRequested) {
                 AnsiConsole.MarkupLine($"[yellow]Warning: Input capture file not found at {inputCapturePath} after execution.[/]");
             } else {
                  AnsiConsole.MarkupLine($"[grey]Input capture file not created (expected due to cancellation).[/]");
             }
             inputs = new Dictionary<string, string>(); // Anggap kosong
        }


        // Cleanup wrapper
        try {
            if (File.Exists(pyWrapperFullPath)) File.Delete(pyWrapperFullPath);
            if (File.Exists(jsWrapperFullPath)) File.Delete(jsWrapperFullPath);
        } catch { /* Ignore cleanup */ }

        return inputs; // Return input (bisa kosong)
    }


    // === METHOD PYTHON WRAPPER (FIXED ESCAPING v2) ===
    private static async Task CreatePythonCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        // Gunakan @"" dan gandakan SEMUA kurung kurawal internal {{ }}
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
# Flag to indicate shutdown initiated by signal
_shutdown_initiated = False
def handle_signal(sig, frame):
    global _shutdown_initiated
    if not _shutdown_initiated:
        _shutdown_initiated = True
        print(f'\nCapture wrapper info: Received signal {{{{sig}}}}. Cleaning up and exiting...', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
        # The finally block will handle saving. Exit with signal code.
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
    print(f'Capture wrapper warning: Could not reconfigure stdio encoding: {{{{enc_err}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}

# --- Input Capture Logic ---
captured = {{{{'}}}} # Python dict init -> {{{{{{...}}}}}}
_original_input = builtins.input

def capturing_input(prompt=''):
    global captured, _shutdown_initiated
    if _shutdown_initiated: # Prevent input prompts after signal
         raise KeyboardInterrupt(""Shutdown initiated"")
    try:
        print(prompt, end='', flush=True)
        response = sys.stdin.readline()
        if response is None:
             raise EOFError(""Stdin closed during input"")
        response = response.rstrip('\n\r')

        key_base = str(prompt).strip().rstrip(':').strip()
        key = key_base or f'input_{{{{{{len(captured)}}}}}}' # Python f-string -> {{{{{{...}}}}}} {{{{{{...}}}}}}

        count = 1
        final_key = key
        while final_key in captured:
            count += 1
            final_key = f'{{{{key}}}}_{{{{count}}}}' # Python f-string -> {{{{{{...}}}}}} {{{{{{...}}}}}}

        captured[final_key] = response
        return response
    except EOFError:
        print('Capture wrapper warning: EOFError detected during input.', file=sys.stderr)
        raise
    except KeyboardInterrupt:
        print('\nCapture wrapper info: KeyboardInterrupt during input.', file=sys.stderr)
        raise
    except Exception as input_err:
        print(f'Capture wrapper error during input call: {{{{input_err}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
        return ''

builtins.input = capturing_input

# --- Target Script Determination ---
script_to_run = None
original_argv = list(sys.argv)

if len(original_argv) > 1:
    script_arg = original_argv[1]
    script_path = os.path.abspath(script_arg)
    if os.path.exists(script_path):
        script_to_run = script_path
    else:
        print(f'Capture wrapper error: Target script specified but not found: {{{{script_path}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
else:
    entry_points = ['run.py', 'main.py', 'bot.py']
    for entry in entry_points:
        entry_path = os.path.abspath(entry)
        if os.path.exists(entry_path):
            print(f'Capture wrapper info: No script argument, found {{{{entry}}}}. Running it.', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
            script_to_run = entry_path
            break
    if script_to_run is None:
         print('Capture wrapper error: No script argument and no common entry point found.', file=sys.stderr)

# --- Script Execution and Cleanup ---
script_executed_successfully = False
exit_code = 1
abs_output_path = os.path.abspath(CAPTURE_OUTPUT_PATH)

try: # Outer try
    if _shutdown_initiated: sys.exit(1) # Exit early if signal received before execution
    if script_to_run:
        try: # Inner try
            sys.argv = [script_to_run] + original_argv[2:]
            script_dir = os.path.dirname(script_to_run)
            print(f'--- Starting Target Script: {{{{os.path.basename(script_to_run)}}}} ---', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}

            with open(script_to_run, 'r', encoding='utf-8') as f:
                source = f.read()
                code = compile(source, script_to_run, 'exec')
                script_globals = {{{{'__name__': '__main__', '__file__': script_to_run}}}} # Python dict -> {{{{{{...}}}}}}
                exec(code, script_globals)
                script_executed_successfully = True # Assume success if exec completes
                # If script uses sys.exit(), it's caught below. If it just ends, exit_code remains 0 if set here.
                exit_code = 0
                print(f'\n--- Target Script Finished ---', file=sys.stderr)

        except SystemExit as sysexit:
            # Respect the script's exit code
            exit_code = sysexit.code if isinstance(sysexit.code, int) else (0 if sysexit.code is None else 1)
            print(f'Capture wrapper info: Script exited via SystemExit with code {{{{exit_code}}}}.', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
            script_executed_successfully = (exit_code == 0)
        except KeyboardInterrupt:
             # This might be caught if signal comes during non-input execution
             if not _shutdown_initiated: # Check if signal handler already ran
                 _shutdown_initiated = True
                 print(f'\nCapture wrapper info: KeyboardInterrupt caught during script execution.', file=sys.stderr)
                 exit_code = 130 # Standard exit code for SIGINT
             # If signal handler ran, exit_code is already set, don't override
        except Exception as e:
            print(f'Capture wrapper FATAL ERROR during script execution: {{{{e}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
            traceback.print_exc(file=sys.stderr)
            exit_code = 1 # Execution error
    else:
         print(f'Capture wrapper warning: No script was executed.', file=sys.stderr)
         exit_code = 1 # No script is an error

finally:
    # --- SAVE CAPTURED DATA ---
    # Executes on normal exit, exception, SystemExit, or after signal handler calls sys.exit
    print(f'Capture wrapper info: Entering finally block. Saving captured data...', file=sys.stderr)
    try:
        output_dir = os.path.dirname(abs_output_path)
        os.makedirs(output_dir, exist_ok=True)
        with open(abs_output_path, 'w', encoding='utf-8') as f:
            serializable_captured = {{{{'k: str(v) for k, v in captured.items()}}}} # Python dict comp -> {{{{{{...}}}}}}
            json.dump(serializable_captured, f, indent=2, ensure_ascii=False)
        print(f'Capture wrapper info: Captured data saved to {{{{abs_output_path}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
    except Exception as save_err:
        print(f'Capture wrapper FATAL ERROR: Failed to write capture file {{{{abs_output_path}}}}: {{{{save_err}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
        # Ensure exit code reflects save failure if script itself succeeded
        if exit_code == 0: exit_code = 1

# If script finished normally OR exited via SystemExit, exit with its code.
# If signal occurred, the signal handler's sys.exit() takes precedence.
# If exec failed with exception, exit_code is 1.
# If save failed, exit_code is 1.
# print(f'Capture wrapper info: Exiting wrapper process with code {{{{exit_code}}}}.', file=sys.stderr)
# sys.exit(exit_code) # Let C# detect process exit normally

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }


    // === METHOD JAVASCRIPT WRAPPER (FIXED ESCAPING v2) ===
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

// === FIX ESCAPING DI SINI ===
const captured = {{}}; // JS object literal -> {{{{}}}}
let rl = null;
const absOutputPath = path.resolve('{escapedOutputPath}'); // C# Interpolation
let isExiting = false;

// --- Save Function ---
function saveCaptureData() {{
    if (isExiting) return;
    // JS template literal -> `$${{{{{{...}}}}}}`, escape $ with $$
    // console.log(`Debug JS: Attempting save to $$${{{{{{absOutputPath}}}}}}`);
    try {{
        const outputDir = path.dirname(absOutputPath);
        if (!fs.existsSync(outputDir)) {{
            // JS object literal -> {{{{{{}}}}}}
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
    // JS template literal -> `$${{{{{{...}}}}}}`
    // console.log(`Debug JS: Initiating exit with code/signal: $$${{{{{{signalOrCode}}}}}}`);
    saveCaptureData();
    if (rl && !rl.closed) {{
        // console.log('Debug JS: Closing readline.');
        rl.close();
    }}
    if (typeof signalOrCode === 'number') {{
         process.exitCode = signalOrCode;
    }}
    // Force exit after delay
    setTimeout(() => {{ process.exit(process.exitCode || 0); }}, 200); // JS Arrow function -> () => {{{{...}}}}
}}

// JS Arrow function -> (...) => {{{{...}}}}
process.on('exit', (code) => {{
    // JS template literal -> `$${{{{{{...}}}}}}`
    // console.log(`Debug JS: 'exit' event triggered with code $$${{{{{{code}}}}}}.`);
    if (!isExiting) {{
         saveCaptureData();
    }}
}});
// JS Arrow function -> () => {{{{...}}}}
process.on('SIGINT', () => {{ /* console.log('Debug JS: SIGINT received.');*/ gracefulExit(130); }});
process.on('SIGTERM', () => {{ /* console.log('Debug JS: SIGTERM received.');*/ gracefulExit(143); }});


// --- Input Capture Setup ---
try {{
    // JS object literal -> {{{{{{}}}}}}
    const nullStream = new Writable({{{{ write(chunk, encoding, callback) {{ callback(); }} }}}});

    // JS object literal -> {{{{{{}}}}}}
    rl = readline.createInterface({{{{
        input: process.stdin,
        output: nullStream,
        prompt: ''
    }}}});

    const originalQuestion = rl.question;

    // JS function definition -> function(...) {{{{...}}}}
    rl.question = function(query, optionsOrCallback, callback) {{
        if (isExiting) return; // Prevent new questions after exit initiated
        let actualCallback = callback;
        let actualOptions = optionsOrCallback;

        if (typeof optionsOrCallback === 'function') {{
            actualCallback = optionsOrCallback;
            actualOptions = {{{{'}}}}; // JS object literal -> {{{{{{}}}}}}
        }} else if (typeof optionsOrCallback !== 'object' || optionsOrCallback === null) {{
            actualOptions = {{{{'}}}}; // JS object literal -> {{{{{{}}}}}}
        }}

        process.stdout.write(String(query));

        // JS Arrow function -> (...) => {{{{...}}}}
        originalQuestion.call(rl, '', actualOptions, (answer) => {{
             if (isExiting) return; // Prevent processing answer after exit initiated
            process.stdout.write(answer + '\n');

            const keyBase = String(query).trim().replace(/[:?]/g, '').trim();
            // JS template literal -> `$${{{{{{...}}}}}}`, escape $ with $$
            let key = keyBase || `input_$$${{{{{{Object.keys(captured).length}}}}}}`;


            let count = 1;
            const originalKey = key;
            while (captured.hasOwnProperty(key)) {{
                count++;
                // JS template literal -> `$${{{{{{...}}}}}}`
                key = `$$${{{{{{originalKey}}}}}}_$$${{{{{{count}}}}}}`;
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
    if (isExiting) throw new Error(""Exiting due to signal before script execution.""); // Check exit flag

    const scriptRelativePath = process.argv[2];
    if (!scriptRelativePath) {{
        throw new Error('No target script provided.');
    }}
    const scriptAbsolutePath = path.resolve(process.cwd(), scriptRelativePath);

    if (!fs.existsSync(scriptAbsolutePath)) {{
        // JS template literal -> `$${{{{{{...}}}}}}`
         throw new Error(`Target script not found at $$${{{{{{scriptAbsolutePath}}}}}}`);
    }}
    process.argv = [process.argv[0], scriptAbsolutePath, ...process.argv.slice(3)];

    // console.log(`Debug JS: Executing target script: $$${{{{{{scriptAbsolutePath}}}}}}`);
    // console.log(`Debug JS: Target script argv: $$${{{{{{JSON.stringify(process.argv)}}}}}}`);

    require(scriptAbsolutePath);
    // console.log(`Debug JS: Target script $$${{{{{{scriptAbsolutePath}}}}}} finished synchronous execution.`);

    // If script finishes synchronously, we might exit too early.
    // Check if readline is still active (implies async ops or waiting for input)
    // If rl exists and is not closed, assume script is running async and DON'T exit wrapper yet.
    // If rl is null or closed, and script finished, we can likely exit.
    // This is still imperfect. Best is if target script manages its own lifecycle.
    // Let's rely on signal/exit handlers.

}} catch (e) {{
    console.error('Capture wrapper FATAL ERROR during script execution:', e);
    gracefulExit(1); // Exit with error code
}}

// console.log(""Debug JS: Capture wrapper script finished synchronous execution."");

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }


} // End of class InteractiveProxyRunner
