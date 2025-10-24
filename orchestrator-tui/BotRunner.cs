using System.Text.Json;
using Spectre.Console;
using System.Runtime.InteropServices;
using System.IO; // <-- Tambahkan using ini

namespace Orchestrator;

public static class BotRunner
{
    private const string ConfigFile = "../config/bots_config.json";

    // ... (RunAllBots, RunSequential, RunParallel, RunBackground, InstallDependencies tidak berubah) ...
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
                RunParallel(botsOnly);
                break;
            case "3":
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

            var botPath = Path.Combine("..", bot.Path);
            if (!Directory.Exists(botPath))
            {
                AnsiConsole.MarkupLine($"[red]  ✗ Folder tidak ditemukan: {botPath}[/]");
                continue;
            }

            // Install deps
            await InstallDependencies(botPath, bot.Type);

            // Determine run command
            var (executor, args) = GetRunCommand(botPath, bot.Type);
            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]  ✗ Tidak ada run file atau npm start[/]"); // Updated message
                continue;
            }

            // Open terminal
            AnsiConsole.MarkupLine($"[green]  ✓ Membuka terminal untuk '{executor} {args}'...[/]");
            ShellHelper.RunInNewTerminal(executor, args, botPath);

            AnsiConsole.MarkupLine($"[dim]  Tekan Enter untuk lanjut ke bot berikutnya...[/]");
            Console.ReadLine();
        }
    }
     private static void RunParallel(List<BotEntry> bots)
    {
        AnsiConsole.MarkupLine("[yellow]Membuka semua bot dalam terminal terpisah...[/]");

        foreach (var bot in bots)
        {
            var botPath = Path.Combine("..", bot.Path);
            if (!Directory.Exists(botPath))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Folder tidak ditemukan[/]");
                continue;
            }

            // Install dependencies first (run silently in background)
            // Note: This might cause issues if multiple npm installs run in parallel? Consider sequential install first.
            // For now, let's keep it simple.
            _ = InstallDependencies(botPath, bot.Type); // Fire and forget install

            var (executor, args) = GetRunCommand(botPath, bot.Type);
            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Tidak ada run file atau npm start[/]");
                continue;
            }

            AnsiConsole.MarkupLine($"[cyan]✓ Launching {bot.Name} ('{executor} {args}')...[/]");
            ShellHelper.RunInNewTerminal(executor, args, botPath);

            Thread.Sleep(2000); // Delay between launches
        }

        AnsiConsole.MarkupLine("\n[dim]Semua bot telah diluncurkan di terminal terpisah.[/]");
    }
     private static async Task RunBackground(List<BotEntry> bots)
    {
        var processes = new List<System.Diagnostics.Process>();

        AnsiConsole.MarkupLine("[yellow]Menjalankan bot di background (non-interactive)...[/]");
        AnsiConsole.MarkupLine("[red]WARNING: Bot dengan menu interaktif akan hang![/]\n");

        foreach (var bot in bots)
        {
            var botPath = Path.Combine("..", bot.Path);
            if (!Directory.Exists(botPath))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Folder tidak ditemukan[/]");
                continue;
            }

            // Install deps first
            await InstallDependencies(botPath, bot.Type);

            var (executor, args) = GetRunCommand(botPath, bot.Type);
            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Tidak ada run file atau npm start[/]");
                continue;
            }

            AnsiConsole.MarkupLine($"[cyan]✓ Starting {bot.Name} ('{executor} {args}')...[/]");

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    // Use ShellHelper's logic to handle cmd/bash execution
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash",
                    Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? $"/c \"{executor} {args}\""
                        : $"-c \"{executor} {args}\"",
                    WorkingDirectory = botPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    // Use AnsiConsole for thread-safe writing potentially
                    AnsiConsole.MarkupLineInterpolated($"[[{bot.Name}]] [grey]{e.Data.EscapeMarkup()}[/]");
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    AnsiConsole.MarkupLineInterpolated($"[[{bot.Name}]] [yellow]{e.Data.EscapeMarkup()}[/]");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            processes.Add(process);
            AnsiConsole.MarkupLine($"[green]  Started (PID: {process.Id})[/]");

            await Task.Delay(3000); // Stagger start
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
                    p.Kill(true); // Force kill process tree
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
        if (type == "python" && File.Exists(Path.Combine(botPath, "requirements.txt")))
        {
            AnsiConsole.MarkupLine($"[dim]  Installing Python deps for '{Path.GetFileName(botPath)}'...[/]");
            await ShellHelper.RunStream("pip", "install --no-cache-dir -q -r requirements.txt", botPath);
        }

        if (type == "javascript" && File.Exists(Path.Combine(botPath, "package.json")))
        {
            AnsiConsole.MarkupLine($"[dim]  Installing Node deps for '{Path.GetFileName(botPath)}'...[/]");
            // Cek jika node_modules sudah ada dan package-lock.json ada, mungkin `npm ci` lebih cepat?
            if (Directory.Exists(Path.Combine(botPath, "node_modules")) && File.Exists(Path.Combine(botPath, "package-lock.json"))) {
                 await ShellHelper.RunStream("npm", "ci --silent", botPath);
            } else {
                 await ShellHelper.RunStream("npm", "install --silent", botPath);
            }
        }
    }

    // === METHOD YANG DIMODIFIKASI ===
    public static (string executor, string args) GetRunCommand(string botPath, string type)
    {
        if (type == "python")
        {
            if (File.Exists(Path.Combine(botPath, "run.py")))
                return ("python", "run.py");
            if (File.Exists(Path.Combine(botPath, "main.py")))
                return ("python", "main.py");
            if (File.Exists(Path.Combine(botPath, "bot.py")))
                return ("python", "bot.py");
        }
        else if (type == "javascript")
        {
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
                        // Ditemukan "scripts": { "start": "..." }
                        return ("npm", "start");
                    }
                }
                catch (JsonException ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning: Gagal parse package.json di {botPath}: {ex.Message}[/]");
                    // Lanjutkan ke fallback filename check
                }
            }

            // Fallback: Cari file spesifik jika npm start tidak ada
            if (File.Exists(Path.Combine(botPath, "index.js")))
                return ("node", "index.js");
            if (File.Exists(Path.Combine(botPath, "main.js")))
                return ("node", "main.js");
            if (File.Exists(Path.Combine(botPath, "bot.js")))
                return ("node", "bot.js");
        }

        // Tidak ada yang ditemukan
        return (string.Empty, string.Empty);
    }
    // =============================

    private static BotConfig? LoadConfig()
    {
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
     // Helper function for pausing
    private static void Pause()
    {
        AnsiConsole.MarkupLine("\n[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }
}
