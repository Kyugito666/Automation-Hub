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

        AnsiConsole.MarkupLine("\n[bold green]✅ Bot triggered remotely![/]"); // Pesan disesuaikan
    }

    private static async Task<Dictionary<string, string>?> RunBotInCaptureMode(string botPath, BotEntry bot)
    {
        var inputs = new Dictionary<string, string>();
        var inputCapturePath = Path.Combine(botPath, ".input-capture.tmp");

        // === NAMA FILE WRAPPER UNTUK JS DIGANTI KE .cjs ===
        string jsWrapperFileName = "capture_wrapper.cjs";
        string jsWrapperFullPath = Path.Combine(botPath, jsWrapperFileName);
        // ===============================================

        if (bot.Type == "python")
        {
            await CreatePythonCaptureWrapper(botPath, inputCapturePath);
        }
        else if (bot.Type == "javascript")
        {
            await CreateJavaScriptCaptureWrapper(jsWrapperFullPath, inputCapturePath); // Path lengkap
        }

        AnsiConsole.MarkupLine("[dim]Running bot interactively (local device)...[/]");
        AnsiConsole.MarkupLine("[dim]Answer all prompts normally. Inputs will be captured.[/]");
        AnsiConsole.MarkupLine("[grey]─────────────────────────────────────[/]\n");

        // Ambil command asli (bisa jadi npm start atau node index.js)
        var (originalExecutor, originalArgs) = BotRunner.GetRunCommand(botPath, bot.Type);

        string executor;
        string args;

        // Inject capture flag/wrapper
        if (bot.Type == "python")
        {
             // Python tetap pakai .py wrapper
            executor = "python";
            args = $"-u capture_wrapper.py {originalArgs}"; // Asumsikan originalArgs adalah nama file .py
        }
        else if (bot.Type == "javascript")
        {
            // === GUNAKAN .cjs SAAT MENJALANKAN ===
            executor = "node"; // Kita *harus* panggil node langsung untuk wrapper
             // Argumen pertama adalah wrapper, sisanya adalah command asli
             // Jika command asli adalah 'npm start', kita perlu cari tahu script sebenarnya
             // Ini jadi kompleks. Simplifikasi: Asumsikan 'npm start' memanggil 'node nama_file.js'
             // Jika 'npm start' melakukan hal lain, capture mungkin gagal.
             // Kita akan coba teruskan saja argumen asli setelah wrapper.
             // Jika GetRunCommand mengembalikan ("npm", "start"), maka args jadi "capture_wrapper.cjs start"
             // Ini tidak akan jalan. Kita perlu argumen sebenarnya yg dipanggil npm start.
             // SOLUSI SEMENTARA: Jika npm start, kita coba tebak file utama (index.js/main.js)
            if (originalExecutor == "npm" && originalArgs == "start")
            {
                // Coba tebak file utama yang mungkin dipanggil 'npm start'
                string mainJs = File.Exists(Path.Combine(botPath, "index.js")) ? "index.js"
                              : File.Exists(Path.Combine(botPath, "main.js")) ? "main.js"
                              : File.Exists(Path.Combine(botPath, "bot.js")) ? "bot.js"
                              : ""; // Gagal menebak
                if (!string.IsNullOrEmpty(mainJs))
                {
                    args = $"{jsWrapperFileName} {mainJs}"; // Jalankan wrapper dengan file tebakan
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗ Tidak bisa otomatis mendeteksi file JS utama untuk 'npm start' di {bot.Name}. Capture mungkin gagal.[/]");
                    args = $"{jsWrapperFileName} {originalArgs}"; // Coba teruskan saja, mungkin gagal
                }
            }
            else // Jika command asli adalah "node file.js"
            {
                 args = $"{jsWrapperFileName} {originalArgs}"; // Jalankan wrapper dengan file asli
            }
            // ====================================
        }
        else // Tipe bot tidak dikenal
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
            if (bot.Type == "python" && File.Exists(Path.Combine(botPath, "capture_wrapper.py")))
                File.Delete(Path.Combine(botPath, "capture_wrapper.py"));

            // === HAPUS FILE .cjs ===
            if (bot.Type == "javascript" && File.Exists(jsWrapperFullPath))
                File.Delete(jsWrapperFullPath);
            // ========================
        }
        catch { /* Abaikan error cleanup */ }

        return inputs;
    }

    private static async Task CreatePythonCaptureWrapper(string botPath, string outputPath)
    {
        var wrapperFileName = "capture_wrapper.py";
        var wrapperFullPath = Path.Combine(botPath, wrapperFileName);
        // Escape outputPath for the generated Python string
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        var wrapper = @$"#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import sys
import json
import builtins
import os
import io # Required for reconfigure

# Set encoding explicitly (safer for Windows)
if sys.stdout.encoding != 'utf-8':
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
if sys.stdin.encoding != 'utf-8':
    sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding='utf-8')


captured = {{}}
_original_input = builtins.input

def capturing_input(prompt=''):
    # Ensure prompt is decoded correctly if needed, though usually str
    response = _original_input(prompt)
    key = prompt.strip().rstrip(':').strip() or f'input_{{len(captured)}}'
    captured[key] = response
    return response

builtins.input = capturing_input

script_executed = False
try:
    if len(sys.argv) > 1:
        script_arg = sys.argv[1]
        # Make sure path is absolute relative to current working directory (botPath)
        script_path = os.path.abspath(os.path.join(os.getcwd(), script_arg))

        if os.path.exists(script_path):
             # Execute in the context of the script's directory? No, CWD is already botPath.
            script_dir = os.path.dirname(script_path)
            # Add script dir to path *if* it's different from CWD? Might not be needed.
            # sys.path.insert(0, script_dir)

            # Change sys.argv for the target script
            original_argv = sys.argv
            sys.argv = [script_path] + original_argv[2:] # Target script sees its own name + remaining args

            with open(script_path, 'r', encoding='utf-8') as f:
                source = f.read()
                code = compile(source, script_path, 'exec')
                script_globals = {{'__name__': '__main__', '__file__': script_path}}
                exec(code, script_globals)
                script_executed = True
        else:
             print(f'Capture wrapper error: Script not found at {{script_path}}')
    else:
        # Try importing common entry points if no script specified
        entry_points = ['run', 'main', 'bot']
        for entry in entry_points:
             try:
                 print(f"Attempting to import {{entry}} module...")
                 __import__(entry)
                 script_executed = True
                 break # Stop after first successful import
             except ImportError:
                  print(f"Module {{entry}} not found.")
             except Exception as import_err:
                  print(f"Error importing module {{entry}}: {{import_err}}")
                  # Continue trying other entry points

        if not script_executed:
             print('Capture wrapper error: No script argument and run.py/main.py/bot.py not found or failed to import.')


except SystemExit:
    print("Capture wrapper: Script exited cleanly.")
    pass # Allow clean exits
except Exception as e:
    import traceback
    print(f'Capture wrapper error during script execution: {{e}}')
    traceback.print_exc() # Print full traceback
finally:
    # Use absolute path for output, ensure encoding
    abs_output_path = os.path.abspath('{escapedOutputPath}')
    try:
        with open(abs_output_path, 'w', encoding='utf-8') as f:
            json.dump(captured, f, indent=2, ensure_ascii=False)
        # print(f'Debug: Captured data written to {{abs_output_path}}') # Optional debug print
    except Exception as e:
        print(f'Capture wrapper error: Failed to write capture file: {{e}}')

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }


    // === PARAMETER wrapperFullPath SEKARANG LENGKAP ===
    private static async Task CreateJavaScriptCaptureWrapper(string wrapperFullPath, string outputPath)
    {
        // Escape outputPath for the generated JS string
        string escapedOutputPath = outputPath.Replace("\\", "\\\\");
        // ============================================
        var wrapper = @$"// Force CommonJS mode by using .cjs extension
const fs = require('fs');
const path = require('path'); // Added for path joining
const readline = require('readline');

const captured = {{}};
let rl; // Declare rl here

try {{
  rl = readline.createInterface({{
      input: process.stdin,
      output: process.stdout
  }});

  const originalQuestion = rl.question.bind(rl);

  rl.question = function(query, optionsOrCallback, callback) {{
      let actualCallback = callback;
      let actualOptions = optionsOrCallback;

      // Handle the overloaded signature: question(query, callback)
      if (typeof optionsOrCallback === 'function') {{
          actualCallback = optionsOrCallback;
          actualOptions = {{}}; // No options provided
      }}

      originalQuestion(query, actualOptions, (answer) => {{
          // More robust key generation
          const key = query.toString().trim().replace(/[:\?]/g, '').trim() || `input_${{Object.keys(captured).length}}`;
          captured[key] = answer;
          if(actualCallback) {{ // Check if callback exists
            actualCallback(answer);
          }} else {{
            // If no callback, maybe it was meant for async usage?
            // This part is tricky without knowing the target bot's input method.
            // For basic readline, a callback is usually expected.
          }}
      }});
  }};

}} catch (e) {{
    console.error("Capture wrapper error initializing readline:", e);
    process.exit(1);
}}


process.on('exit', (code) => {{
    // Use path.resolve to ensure absolute path
    const absOutputPath = path.resolve('{escapedOutputPath}');
    try {{
      // Ensure the directory exists before writing
      const outputDir = path.dirname(absOutputPath);
      if (!fs.existsSync(outputDir)){{
        fs.mkdirSync(outputDir, {{ recursive: true }});
      }}
      fs.writeFileSync(absOutputPath, JSON.stringify(captured, null, 2));
       // console.log(`Debug: Captured data written to ${{absOutputPath}} with code ${{code}}`); // Optional debug
    }} catch (e) {{
      console.error('Capture wrapper error: Failed to write capture file:', e);
    }} finally {{
       if (rl) rl.close(); // Close readline interface on exit
    }}
}});

// Gracefully handle termination signals
const handleExit = (signal) => {{
    // console.log(`Received ${{signal}}. Exiting capture wrapper.`); // Optional debug
    if (rl) rl.close(); // Ensure readline is closed before exit
    process.exit(0); // Trigger the 'exit' event handler
}};
process.on('SIGINT', handleExit);
process.on('SIGTERM', handleExit);

// --- Target Script Execution Logic ---
try {{
    // process.argv[0] is node, [1] is this script (.cjs), [2] is the target script argument from C#
    const scriptRelativePath = process.argv[2];
    if (!scriptRelativePath) {{
        console.error('Capture wrapper error: No target script provided.');
        process.exit(1);
    }}
     // Resolve the absolute path of the target script relative to the current working directory (botPath set by C#)
    const scriptAbsolutePath = path.resolve(process.cwd(), scriptRelativePath);

    if (!fs.existsSync(scriptAbsolutePath)) {{
         console.error(`Capture wrapper error: Target script not found at ${{scriptAbsolutePath}}`);
         process.exit(1);
    }}

    // Set argv for the target script: [node executable, target script path, ...original args...]
    // Original args start from process.argv[3]
    process.argv = [process.argv[0], scriptAbsolutePath, ...process.argv.slice(3)];

    // console.log(`Debug: Executing target script: ${{scriptAbsolutePath}}`); // Optional debug
    // console.log(`Debug: Target script argv: ${{JSON.stringify(process.argv)}}`); // Optional debug

    // Dynamically import if it's an ES module, require if it's CommonJS
    // Check package.json type field or file extension (though we forced .cjs wrapper)
    // For simplicity, let's stick to require, as the wrapper is CommonJS.
    // If the *target* script is ES module, 'require' might fail.
    // A more robust solution involves detecting module type.
    // Let's assume for now target scripts are compatible with being 'required' from CommonJS
    require(scriptAbsolutePath); // Execute the target script

}} catch (e) {{
    console.error('Capture wrapper error during script execution:', e);
    if (rl) rl.close(); // Close readline on error too
    // process.exit(1); // Exit with error code if script fails
}}

// Keep the process running if the required script initiates async operations
// This might be needed if the script doesn't explicitly keep the event loop alive.
// However, adding a long timeout can be problematic. Rely on the script's own lifecycle.
// setTimeout(() => {{}}, 1000 * 60 * 60); // Example: Keep alive for an hour (REMOVE THIS in production)

";
        await File.WriteAllTextAsync(wrapperFullPath, wrapper);
    }
}
