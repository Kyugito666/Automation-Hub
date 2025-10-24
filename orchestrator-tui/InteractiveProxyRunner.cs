using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

public static class InteractiveProxyRunner
{
    private const string InputsDir = "../.bot-inputs";
    private const string VenvDirName = ".venv";

    public static async Task CaptureAndTriggerBot(BotEntry bot, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine($"[bold cyan]=== Interactive Proxy Mode: {bot.Name} ===[/]");
        AnsiConsole.MarkupLine("[yellow]Step 1: Capturing inputs locally...[/]");

        // Cek cancel sebelum mulai
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(InputsDir);
        var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
        if (!Directory.Exists(botPath)) { /* handle error */ return; }

        await BotRunner.InstallDependencies(botPath, bot.Type);
        cancellationToken.ThrowIfCancellationRequested(); // Cek lagi setelah install

        Dictionary<string, string>? capturedInputs = null;
        // Exception OperationCanceledException akan dilempar dari RunBotInCaptureMode jika Ctrl+C ditekan
        // dan akan ditangkap oleh method pemanggil (RunAllInteractiveBots / ShowHybridMenu)
        capturedInputs = await RunBotInCaptureMode(botPath, bot, cancellationToken);

        // --- Alur Normal (Tidak ada cancellation exception) ---

        if (capturedInputs == null || capturedInputs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No inputs captured. Bot may not be interactive, or capture failed.[/]");
            // Tampilkan Confirm HANYA jika tidak ada pembatalan terjadi (exception tidak dilempar)
            var runDirect = AnsiConsole.Confirm("Run directly on GitHub Actions without inputs?", defaultValue: false); // Default false lebih aman
            if (!runDirect)
            {
                 AnsiConsole.MarkupLine("[yellow]Skipping remote trigger for this bot.[/]");
                 return; // Keluar jika user pilih 'n'
            }
            capturedInputs = new Dictionary<string, string>();
        }

        // Simpan file input (bisa kosong jika runDirect=true)
        var inputsFile = Path.Combine(InputsDir, $"{bot.Name}.json");
        var inputsJson = JsonSerializer.Serialize(capturedInputs, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(inputsFile, inputsJson);
        AnsiConsole.MarkupLine($"[green]✓ Inputs saved to: {inputsFile}[/]");

        if (capturedInputs.Any()) { /* Tampilkan tabel */
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
            AnsiConsole.MarkupLine("[yellow]Cancelled remote trigger.[/]");
            return;
        }

        await GitHubDispatcher.TriggerBotWithInputs(bot, capturedInputs);
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

        // Hapus file lama
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

        AnsiConsole.MarkupLine("[dim]Running bot interactively (local device)... (Press Ctrl+C to skip)[/]"); // Tambah hint Ctrl+C
        AnsiConsole.MarkupLine("[dim]Answer all prompts normally. Inputs will be captured.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(absoluteBotPath, bot.Type);
        string executor;
        string args;

        if (string.IsNullOrEmpty(originalExecutor)) { /* handle error */ return null; }

        // --- Pastikan Executor dan Args sudah benar ---
        if (bot.Type == "python")
        {
            executor = originalExecutor; // Path ke python venv
             // Cek jika originalArgs kosong (jika GetRunCommand tidak menemukan run.py dkk)
             if (string.IsNullOrEmpty(originalArgs)) {
                 AnsiConsole.MarkupLine($"[red]✗ Tidak ada file entry point (run.py/main.py/bot.py) ditemukan untuk {bot.Name}.[/]");
                 return null;
             }
            args = $"-u \"{pyWrapperFileName}\" {originalArgs}"; // Wrapper + script asli
        }
        else if (bot.Type == "javascript")
        {
            executor = "node"; // Wrapper pakai node global
            string targetScriptArg;
            if (originalExecutor == "npm" && originalArgs == "start") {
                 // Tebak script target
                string mainJs = File.Exists(Path.Combine(absoluteBotPath, "index.js")) ? "index.js"
                              : File.Exists(Path.Combine(absoluteBotPath, "main.js")) ? "main.js"
                              : File.Exists(Path.Combine(absoluteBotPath, "bot.js")) ? "bot.js"
                              : "";
                if (!string.IsNullOrEmpty(mainJs)) {
                    targetScriptArg = mainJs;
                    AnsiConsole.MarkupLine($"[grey]Detected 'npm start', assuming target script for capture is '{targetScriptArg}'[/]");
                } else {
                    AnsiConsole.MarkupLine($"[red]✗ Tidak bisa otomatis mendeteksi file JS utama untuk 'npm start' di {bot.Name}. Capture gagal.[/]");
                    return null; // Gagal jika tidak bisa tebak
                }
            } else {
                 targetScriptArg = originalArgs; // Harusnya nama file .js
                 // Validasi sederhana
                 if (string.IsNullOrEmpty(targetScriptArg) || !(targetScriptArg.EndsWith(".js") || targetScriptArg.EndsWith(".cjs") || targetScriptArg.EndsWith(".mjs"))) {
                      AnsiConsole.MarkupLine($"[red]✗ Argumen script tidak valid ('{targetScriptArg}') untuk {bot.Name}.[/]");
                      return null;
                 }
            }
            args = $"\"{jsWrapperFileName}\" \"{targetScriptArg}\""; // Wrapper + script target
        }
        else { /* handle error tipe tidak dikenal */ return null; }

        cancellationToken.ThrowIfCancellationRequested(); // Cek sebelum run

        // === Jalankan dengan CancellationToken ===
        // Exception OperationCanceledException akan dilempar jika Ctrl+C ditekan
        await ShellHelper.RunInteractive(executor, args, absoluteBotPath, cancellationToken);
        // =======================================

        AnsiConsole.MarkupLine("\n[grey]─────────────────────────────────────[/]");

        // Baca file .tmp HANYA jika tidak ada cancellation exception
        // Jika ada cancel, file mungkin tidak lengkap/tidak ada, biarkan pemanggil handle
        inputs = ReadAndDeleteCaptureFile(inputCapturePath); // Helper baru

        // Cleanup wrapper
        try {
            if (File.Exists(pyWrapperFullPath)) File.Delete(pyWrapperFullPath);
            if (File.Exists(jsWrapperFullPath)) File.Delete(jsWrapperFullPath);
        } catch { /* Ignore cleanup */ }

        return inputs; // Kembalikan input (bisa kosong)
    }

    // Helper baru untuk membaca dan menghapus file capture
    private static Dictionary<string, string> ReadAndDeleteCaptureFile(string filePath) {
        Dictionary<string, string> data = new Dictionary<string, string>();
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath); // Baca sinkron saja
                try {
                     data = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                              ?? new Dictionary<string, string>();
                     AnsiConsole.MarkupLine($"[green]✓ Input capture file processed.[/]");
                } catch (JsonException jsonEx) {
                     AnsiConsole.MarkupLine($"[yellow]Warning: Could not parse capture file: {jsonEx.Message}[/]");
                }
                File.Delete(filePath); // Hapus setelah dibaca/dicoba
            }
            catch (IOException ioEx)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not process/delete capture file: {ioEx.Message}[/]");
            }
        } else {
            // Ini normal jika dibatalkan sebelum file dibuat
            // AnsiConsole.MarkupLine($"[grey]Input capture file not found at {filePath}.[/]");
        }
        return data;
    }


    // === METHOD PYTHON WRAPPER (FIXED ESCAPING v3) ===
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
_shutdown_initiated = False
def handle_signal(sig, frame):
    global _shutdown_initiated
    if not _shutdown_initiated:
        _shutdown_initiated = True
        print(f'\nCapture wrapper info: Received signal {{{{sig}}}}. Cleaning up and exiting...', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
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
    if _shutdown_initiated: raise KeyboardInterrupt(""Shutdown initiated"")
    try:
        print(prompt, end='', flush=True)
        response = sys.stdin.readline()
        if response is None: raise EOFError(""Stdin closed during input"")
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
    if os.path.exists(script_path): script_to_run = script_path
    else: print(f'Capture wrapper error: Target script specified but not found: {{{{script_path}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
else:
    entry_points = ['run.py', 'main.py', 'bot.py']
    for entry in entry_points:
        entry_path = os.path.abspath(entry)
        if os.path.exists(entry_path):
            print(f'Capture wrapper info: No script argument, found {{{{entry}}}}. Running it.', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
            script_to_run = entry_path; break
    if script_to_run is None: print('Capture wrapper error: No script argument and no common entry point found.', file=sys.stderr)

# --- Script Execution and Cleanup ---
exit_code = 1
abs_output_path = os.path.abspath(CAPTURE_OUTPUT_PATH)

try: # Outer try
    if _shutdown_initiated: sys.exit(1)
    if script_to_run:
        try: # Inner try
            sys.argv = [script_to_run] + original_argv[2:]
            print(f'--- Starting Target Script: {{{{os.path.basename(script_to_run)}}}} ---', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
            with open(script_to_run, 'r', encoding='utf-8') as f:
                source = f.read()
                code = compile(source, script_to_run, 'exec')
                script_globals = {{{{'__name__': '__main__', '__file__': script_to_run}}}} # Python dict -> {{{{{{...}}}}}}
                exec(code, script_globals)
                exit_code = 0
                print(f'\n--- Target Script Finished ---', file=sys.stderr)
        except SystemExit as sysexit:
            exit_code = sysexit.code if isinstance(sysexit.code, int) else (0 if sysexit.code is None else 1)
            print(f'Capture wrapper info: Script exited via SystemExit with code {{{{exit_code}}}}.', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
        except KeyboardInterrupt:
             if not _shutdown_initiated: _shutdown_initiated = True; print(f'\nCapture wrapper info: KeyboardInterrupt caught.', file=sys.stderr); exit_code = 130
        except Exception as e:
            print(f'Capture wrapper FATAL ERROR during script execution: {{{{e}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
            traceback.print_exc(file=sys.stderr); exit_code = 1
    else: print(f'Capture wrapper warning: No script was executed.', file=sys.stderr); exit_code = 1
finally:
    if not _shutdown_initiated: # Only save if not already exiting due to signal
        print(f'Capture wrapper info: Entering finally block. Saving captured data...', file=sys.stderr)
        try:
            output_dir = os.path.dirname(abs_output_path); os.makedirs(output_dir, exist_ok=True)
            with open(abs_output_path, 'w', encoding='utf-8') as f:
                serializable_captured = {{{{'k: str(v) for k, v in captured.items()}}}} # Python dict comp -> {{{{{{...}}}}}}
                json.dump(serializable_captured, f, indent=2, ensure_ascii=False)
            print(f'Capture wrapper info: Captured data saved to {{{{abs_output_path}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
        except Exception as save_err:
            print(f'Capture wrapper FATAL ERROR: Failed to write capture file {{{{abs_output_path}}}}: {{{{save_err}}}}', file=sys.stderr) # Python f-string -> {{{{{{...}}}}}}
            if exit_code == 0: exit_code = 1
";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }


    // === METHOD JAVASCRIPT WRAPPER (FIXED ESCAPING v3) ===
    private static async Task CreateJavaScriptCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        // GANDAKAN SEMUA {{ }} dan escape $ menjadi $$
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
    // JS template literal -> `$${{{{{{...}}}}}}`
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
        if (isExiting) return;
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
             if (isExiting) return;
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
    if (isExiting) throw new Error(""Exiting due to signal before script execution."");

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

}} catch (e) {{
    console.error('Capture wrapper FATAL ERROR during script execution:', e);
    gracefulExit(1);
}}

// console.log(""Debug JS: Capture wrapper script finished synchronous execution."");

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }

} // End of class InteractiveProxyRunner
