using System.Text.Json;
using Spectre.Console;

namespace Orchestrator;

public static class BotRunner
{
    private const string ConfigFile = "../config/bots_config.json";
    private static readonly List<System.Diagnostics.Process> RunningProcesses = new();

    public static async Task RunAllBots()
    {
        var config = LoadConfig();
        if (config == null) return;

        AnsiConsole.MarkupLine("[bold cyan]--- Menjalankan Semua Bot ---[/]");
        AnsiConsole.MarkupLine($"[dim]Total bot: {config.BotsAndTools.Count}[/]\n");

        var botsOnly = config.BotsAndTools
            .Where(b => b.Path.Contains("/privatekey/") || b.Path.Contains("/token/"))
            .Where(b => b.Enabled)
            .ToList();

        if (!botsOnly.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Tidak ada bot yang aktif.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]Bot aktif: {botsOnly.Count}[/]");
        AnsiConsole.MarkupLine("[dim]Menekan Ctrl+C akan menghentikan semua bot[/]\n");

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            StopAllBots();
        };

        foreach (var bot in botsOnly)
        {
            await RunBot(bot);
            await Task.Delay(3000); // Delay 3 detik antar bot
        }

        AnsiConsole.MarkupLine("\n[bold green]✅ Semua bot berhasil dijalankan.[/]");
        AnsiConsole.MarkupLine("[yellow]Bot berjalan di background. Tekan Enter untuk stop semua...[/]");
        Console.ReadLine();

        StopAllBots();
    }

    private static async Task RunBot(BotEntry bot)
    {
        var botPath = Path.Combine("..", bot.Path);

        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Folder tidak ditemukan[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]▶ Menjalankan: {bot.Name}[/]");

        // Install dependencies
        if (File.Exists(Path.Combine(botPath, "requirements.txt")))
        {
            AnsiConsole.MarkupLine($"[dim]  Installing Python deps...[/]");
            await ShellHelper.RunStream("pip", "install --no-cache-dir -q -r requirements.txt", botPath);
        }

        if (File.Exists(Path.Combine(botPath, "package.json")))
        {
            AnsiConsole.MarkupLine($"[dim]  Installing Node deps...[/]");
            await ShellHelper.RunStream("npm", "install --silent", botPath);
        }

        // Run bot
        var runFile = "";
        var executor = "";
        var args = "";

        if (File.Exists(Path.Combine(botPath, "run.py")))
        {
            runFile = "run.py";
            executor = "python";
            args = "run.py";
        }
        else if (File.Exists(Path.Combine(botPath, "index.js")))
        {
            runFile = "index.js";
            executor = "node";
            args = "index.js";
        }
        else if (File.Exists(Path.Combine(botPath, "main.py")))
        {
            runFile = "main.py";
            executor = "python";
            args = "main.py";
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]  ✗ Tidak ditemukan run.py/index.js/main.py[/]");
            return;
        }

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executor,
                Arguments = args,
                WorkingDirectory = botPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine($"[{bot.Name}] {e.Data}");
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.Error.WriteLine($"[{bot.Name}] ERROR: {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        RunningProcesses.Add(process);
        AnsiConsole.MarkupLine($"[green]  ✓ Started (PID: {process.Id})[/]");
    }

    private static void StopAllBots()
    {
        AnsiConsole.MarkupLine("\n[yellow]Menghentikan semua bot...[/]");

        foreach (var process in RunningProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    AnsiConsole.MarkupLine($"[dim]  Stopped PID {process.Id}[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]  Error stopping PID {process.Id}: {ex.Message}[/]");
            }
        }

        RunningProcesses.Clear();
        AnsiConsole.MarkupLine("[green]✓ Semua bot dihentikan.[/]");
    }

    private static BotConfig? LoadConfig()
    {
        if (!File.Exists(ConfigFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: File '{ConfigFile}' tidak ditemukan.[/]");
            return null;
        }

        var json = File.ReadAllText(ConfigFile);
        return JsonSerializer.Deserialize<BotConfig>(json);
    }
}
