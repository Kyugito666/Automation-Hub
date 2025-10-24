using System.Text.Json;
using Spectre.Console;
using System.Runtime.InteropServices;
using System.IO;

namespace Orchestrator;

public static class BotRunner
{
    private const string ConfigFile = "../config/bots_config.json";
    private const string VenvDirName = ".venv"; // Nama folder venv standar

    // ... (RunAllBots, RunSequential tidak berubah) ...
     public static async Task RunAllBots()
    {
        var config = LoadConfig();
        if (config == null) return;

        AnsiConsole.MarkupLine("[bold cyan]--- Menjalankan Semua Bot ---[/]");

        var botsOnly = config.BotsAndTools
            .Where(b => b.Path.Contains("/privatekey/") || b.Path.Contains("/token/"))
            .Where(b => b.Enabled)
            .ToList();

        if (!botsOnly.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Tidak ada bot yang aktif.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]Bot aktif: {botsOnly.Count}[/]\n");

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Pilih mode eksekusi:[/]")
                 .WrapAround() // Keep wrap around
                .AddChoices(new[]
                {
                    "1. Sequential (Satu per satu, buka terminal baru)",
                    "2. Parallel (Buka semua terminal sekaligus)",
                    "3. Background (Tanpa UI, log ke console)",
                    "0. [[Back]] Kembali" // Consistent back option
                }));

        switch (choice.Split('.')[0])
        {
            case "1":
                await RunSequential(botsOnly);
                break;
            case "2":
                 // === FIX: Perlu await karena RunParallel jadi async ===
                await RunParallel(botsOnly);
                break;
            case "3":
                 // === FIX: Perlu await karena RunBackground jadi async ===
                await RunBackground(botsOnly);
                break;
            case "0": // Handle back
                return;
        }

         // Only pause if not going back
         if (choice.Split('.')[0] != "0")
         {
            AnsiConsole.MarkupLine("\n[bold green]✅ Eksekusi selesai.[/]");
            Pause(); // Use the Pause helper
         }
    }
     private static async Task RunSequential(List<BotEntry> bots)
    {
        foreach (var bot in bots)
        {
            AnsiConsole.MarkupLine($"\n[cyan]▶ {bot.Name}[/]");

            var botPath = Path.GetFullPath(Path.Combine("..", bot.Path)); // Get full path
            if (!Directory.Exists(botPath))
            {
                AnsiConsole.MarkupLine($"[red]  ✗ Folder tidak ditemukan: {botPath}[/]");
                continue;
            }

            // Install deps (will handle venv)
            await InstallDependencies(botPath, bot.Type);

            // Determine run command (will use venv python if applicable)
            var (executor, args) = GetRunCommand(botPath, bot.Type);
            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]  ✗ Tidak ada run file, npm start, atau venv python[/]"); // Updated message
                continue;
            }

            // Open terminal
            AnsiConsole.MarkupLine($"[green]  ✓ Membuka terminal untuk '{Path.GetFileName(executor)} {args}'...[/]");
            ShellHelper.RunInNewTerminal(executor, args, botPath);

            AnsiConsole.MarkupLine($"[dim]  Tekan Enter untuk lanjut ke bot berikutnya...[/]");
            Console.ReadLine();
        }
    }

    // === FIX: Tambahkan async Task ===
    private static async Task RunParallel(List<BotEntry> bots)
    {
        AnsiConsole.MarkupLine("[yellow]Membuka semua bot dalam terminal terpisah...[/]");

        AnsiConsole.MarkupLine("[cyan]Menginstall dependensi (mungkin perlu waktu)...[/]");
        bool firstInstall = true;
        foreach (var bot in bots) {
             var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
             if (Directory.Exists(botPath)) {
                 if (!firstInstall) AnsiConsole.Write(".");
                 // === FIX: Gunakan await, bukan .Result ===
                 await InstallDependencies(botPath, bot.Type);
                 firstInstall = false;
             }
        }
        AnsiConsole.WriteLine("\n[green]Instalasi dependensi selesai.[/]");


        AnsiConsole.MarkupLine("[yellow]Meluncurkan bot secara paralel...[/]");
        foreach (var bot in bots)
        {
            var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
            if (!Directory.Exists(botPath))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Folder tidak ditemukan[/]");
                continue;
            }

            var (executor, args) = GetRunCommand(botPath, bot.Type);
            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Tidak ada run file, npm start, atau venv python[/]");
                continue;
            }

            AnsiConsole.MarkupLine($"[cyan]✓ Launching {bot.Name} ('{Path.GetFileName(executor)} {args}')...[/]");
            ShellHelper.RunInNewTerminal(executor, args, botPath);

            Thread.Sleep(500);
        }

        AnsiConsole.MarkupLine("\n[dim]Semua bot telah diluncurkan di terminal terpisah.[/]");
    }

    // === FIX: Tambahkan async Task ===
    private static async Task RunBackground(List<BotEntry> bots)
    {
        var processes = new List<System.Diagnostics.Process>();

        AnsiConsole.MarkupLine("[yellow]Menjalankan bot di background (non-interactive)...[/]");
        AnsiConsole.MarkupLine("[red]WARNING: Bot dengan menu interaktif akan hang![/]\n");

        AnsiConsole.MarkupLine("[cyan]Menginstall dependensi (mungkin perlu waktu)...[/]");
        bool firstInstallBg = true;
        foreach (var bot in bots) {
             var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
             if (Directory.Exists(botPath)) {
                  if (!firstInstallBg) AnsiConsole.Write(".");
                 // === FIX: Gunakan await, bukan .Result ===
                 await InstallDependencies(botPath, bot.Type);
                  firstInstallBg = false;
             }
        }
        AnsiConsole.WriteLine("\n[green]Instalasi dependensi selesai.[/]");


        AnsiConsole.MarkupLine("[yellow]Memulai bot di background...[/]");
        foreach (var bot in bots)
        {
            var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
            if (!Directory.Exists(botPath))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Folder tidak ditemukan[/]");
                continue;
            }

            var (executor, args) = GetRunCommand(botPath, bot.Type);
            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Tidak ada run file, npm start, atau venv python[/]");
                continue;
            }

            AnsiConsole.MarkupLine($"[cyan]✓ Starting {bot.Name} ('{Path.GetFileName(executor)} {args}')...[/]");

             var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executor,
                    Arguments = args,
                    WorkingDirectory = botPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

            var process = new System.Diagnostics.Process { StartInfo = startInfo };

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    AnsiConsole.MarkupLineInterpolated($"[[{bot.Name}]] [grey]{e.Data.EscapeMarkup()}[/]");
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    AnsiConsole.MarkupLineInterpolated($"[[{bot.Name}]] [yellow]{e.Data.EscapeMarkup()}[/]");
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                processes.Add(process);
                AnsiConsole.MarkupLine($"[green]  Started (PID: {process.Id})[/]");
            }
            catch (Exception ex)
            {
                 AnsiConsole.MarkupLine($"[red]  ✗ Gagal start PID: {ex.Message}[/]");
            }

            await Task.Delay(1000);
        }

        AnsiConsole.MarkupLine("\n[yellow]Semua bot berjalan. Tekan Enter untuk stop semua...[/]");
        Console.ReadLine();

        AnsiConsole.MarkupLine("[yellow]Stopping all background processes...[/]");
        foreach (var p in processes)
        {
            try
            {
                if (!p.HasExited)
                {
                    AnsiConsole.MarkupLine($"[dim]  Stopping PID {p.Id}...[/]");
                    p.Kill(true);
                }
            }
            catch (Exception ex)
            {
                 AnsiConsole.MarkupLine($"[red]  Error stopping PID {p.Id}: {ex.Message}[/]");
            }
        }

        AnsiConsole.MarkupLine("[green]✓ Semua bot dihentikan.[/]");
    }

    public static async Task InstallDependencies(string botPath, string type)
    {
        string botName = Path.GetFileName(botPath);
        if (type == "python" && File.Exists(Path.Combine(botPath, "requirements.txt")))
        {
            string venvPath = Path.Combine(botPath, VenvDirName);
            string? pythonExe = GetPythonExecutableInVenv(botPath); // Sekarang nullable
            string? pipExe = GetPipExecutableInVenv(botPath);     // Sekarang nullable

            if (!Directory.Exists(venvPath))
            {
                AnsiConsole.MarkupLine($"[dim]  Membuat venv untuk '{botName}'...[/]");
                await ShellHelper.RunStream("python", $"-m venv \"{VenvDirName}\"", botPath);
                pythonExe = GetPythonExecutableInVenv(botPath); // Coba lagi ambil path setelah dibuat
                pipExe = GetPipExecutableInVenv(botPath);
            }

            if (!string.IsNullOrEmpty(pipExe))
            {
                 AnsiConsole.MarkupLine($"[dim]  Installing Python deps for '{botName}' using venv...[/]");
                await ShellHelper.RunStream($"\"{pipExe}\"", "install --no-cache-dir -q -r requirements.txt", botPath);
            } else {
                 AnsiConsole.MarkupLine($"[red]  ✗ Tidak bisa menemukan pip di venv untuk '{botName}'. Instalasi dependensi mungkin gagal.[/]");
            }

        }
        else if (type == "javascript" && File.Exists(Path.Combine(botPath, "package.json")))
        {
            AnsiConsole.MarkupLine($"[dim]  Installing Node deps for '{botName}'...[/]");
            if (Directory.Exists(Path.Combine(botPath, "node_modules")) && File.Exists(Path.Combine(botPath, "package-lock.json"))) {
                 await ShellHelper.RunStream("npm", "ci --silent --no-progress", botPath);
            } else {
                 await ShellHelper.RunStream("npm", "install --silent --no-progress", botPath);
            }
        }
    }

    public static (string executor, string args) GetRunCommand(string botPath, string type)
    {
        if (type == "python")
        {
            string? pythonInVenv = GetPythonExecutableInVenv(botPath); // Sekarang nullable
            if (!string.IsNullOrEmpty(pythonInVenv))
            {
                if (File.Exists(Path.Combine(botPath, "run.py")))
                    return (pythonInVenv, "run.py");
                if (File.Exists(Path.Combine(botPath, "main.py")))
                    return (pythonInVenv, "main.py");
                if (File.Exists(Path.Combine(botPath, "bot.py")))
                    return (pythonInVenv, "bot.py");
                AnsiConsole.MarkupLine($"[yellow]Warning: Venv ditemukan di '{Path.GetFileName(botPath)}' tapi tidak ada run.py/main.py/bot.py.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Venv tidak ditemukan atau gagal dibuat untuk '{Path.GetFileName(botPath)}'. Mencoba python global (mungkin gagal).[/]");
                if (File.Exists(Path.Combine(botPath, "run.py"))) return ("python", "run.py");
                if (File.Exists(Path.Combine(botPath, "main.py"))) return ("python", "main.py");
                if (File.Exists(Path.Combine(botPath, "bot.py"))) return ("python", "bot.py");
            }
        }
        else if (type == "javascript")
        {
            // ... (Logika npm start tidak berubah) ...
             string packageJsonPath = Path.Combine(botPath, "package.json");
            if (File.Exists(packageJsonPath))
            {
                try
                {
                    var jsonContent = File.ReadAllText(packageJsonPath);
                    using var doc = JsonDocument.Parse(jsonContent);
                    if (doc.RootElement.TryGetProperty("scripts", out var scripts) &&
                        scripts.TryGetProperty("start", out var startScript) &&
                        startScript.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(startScript.GetString()))
                    {
                        return ("npm", "start");
                    }
                }
                catch (JsonException ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning: Gagal parse package.json di {botPath}: {ex.Message}[/]");
                }
            }
            if (File.Exists(Path.Combine(botPath, "index.js"))) return ("node", "index.js");
            if (File.Exists(Path.Combine(botPath, "main.js"))) return ("node", "main.js");
            if (File.Exists(Path.Combine(botPath, "bot.js"))) return ("node", "bot.js");
        }

        return (string.Empty, string.Empty);
    }

    // === FIX: Return type jadi string? (nullable) ===
    private static string? GetPythonExecutableInVenv(string botPath)
    {
        string venvPath = Path.Combine(botPath, VenvDirName);
        string exePath;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            exePath = Path.Combine(venvPath, "Scripts", "python.exe");
        }
        else // Linux or macOS
        {
            exePath = Path.Combine(venvPath, "bin", "python");
             // Juga cek python3 di Linux/Mac venv
             if (!File.Exists(exePath)) {
                 exePath = Path.Combine(venvPath, "bin", "python3");
             }
        }
        return File.Exists(exePath) ? Path.GetFullPath(exePath) : null; // Return null jika tidak ada
    }

    // === FIX: Return type jadi string? (nullable) ===
    private static string? GetPipExecutableInVenv(string botPath)
    {
        string venvPath = Path.Combine(botPath, VenvDirName);
        string exePath;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            exePath = Path.Combine(venvPath, "Scripts", "pip.exe");
        }
        else // Linux or macOS
        {
            exePath = Path.Combine(venvPath, "bin", "pip");
             // Juga cek pip3
             if (!File.Exists(exePath)) {
                 exePath = Path.Combine(venvPath, "bin", "pip3");
             }
        }
         return File.Exists(exePath) ? Path.GetFullPath(exePath) : null; // Return null jika tidak ada
    }


    private static BotConfig? LoadConfig()
    {
        // ... (Tidak berubah) ...
        if (!File.Exists(ConfigFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: '{ConfigFile}' tidak ditemukan.[/]");
            return null;
        }

        var json = File.ReadAllText(ConfigFile);
        try
        {
             return JsonSerializer.Deserialize<BotConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error parsing bots_config.json: {ex.Message}[/]");
            return null;
        }
    }

    private static void Pause()
    {
        // ... (Tidak berubah) ...
        AnsiConsole.MarkupLine("\n[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }
}
