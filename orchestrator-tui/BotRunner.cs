using System.Text.Json;
using Spectre.Console;
using System.Runtime.InteropServices;

namespace Orchestrator;

public static class BotRunner
{
    private const string ConfigFile = "../config/bots_config.json";

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
                .AddChoices(new[]
                {
                    "1. Sequential (Satu per satu, buka terminal baru)",
                    "2. Parallel (Buka semua terminal sekaligus)",
                    "3. Background (Tanpa UI, log ke console)",
                    "4. Batal"
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
            case "4":
                return;
        }

        AnsiConsole.MarkupLine("\n[bold green]✅ Eksekusi selesai.[/]");
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
                AnsiConsole.MarkupLine($"[red]  ✗ Tidak ada run file[/]");
                continue;
            }

            // Open terminal
            AnsiConsole.MarkupLine($"[green]  ✓ Membuka terminal...[/]");
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

            var (executor, args) = GetRunCommand(botPath, bot.Type);
            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Tidak ada run file[/]");
                continue;
            }

            ShellHelper.RunInNewTerminal(executor, args, botPath);
            AnsiConsole.MarkupLine($"[green]✓ {bot.Name} launched[/]");
            
            Thread.Sleep(2000); // Delay 2 detik antar launch
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

            await InstallDependencies(botPath, bot.Type);

            var (executor, args) = GetRunCommand(botPath, bot.Type);
            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: Tidak ada run file[/]");
                continue;
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

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.WriteLine($"[{bot.Name}] {e.Data}");
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.Error.WriteLine($"[{bot.Name}] {e.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            processes.Add(process);
            AnsiConsole.MarkupLine($"[green]✓ {bot.Name} started (PID: {process.Id})[/]");
            
            await Task.Delay(3000);
        }

        AnsiConsole.MarkupLine("\n[yellow]Semua bot berjalan. Tekan Enter untuk stop semua...[/]");
        Console.ReadLine();

        foreach (var p in processes)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(true);
            }
            catch { }
        }

        AnsiConsole.MarkupLine("[green]✓ Semua bot dihentikan.[/]");
    }

    private static async Task InstallDependencies(string botPath, string type)
    {
        if (type == "python" && File.Exists(Path.Combine(botPath, "requirements.txt")))
        {
            AnsiConsole.MarkupLine($"[dim]  Installing Python deps...[/]");
            await ShellHelper.RunStream("pip", "install --no-cache-dir -q -r requirements.txt", botPath);
        }

        if (type == "javascript" && File.Exists(Path.Combine(botPath, "package.json")))
        {
            AnsiConsole.MarkupLine($"[dim]  Installing Node deps...[/]");
            await ShellHelper.RunStream("npm", "install --silent", botPath);
        }
    }

    private static (string executor, string args) GetRunCommand(string botPath, string type)
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

        if (type == "javascript")
        {
            if (File.Exists(Path.Combine(botPath, "index.js")))
                return ("node", "index.js");
            if (File.Exists(Path.Combine(botPath, "main.js")))
                return ("node", "main.js");
            if (File.Exists(Path.Combine(botPath, "bot.js")))
                return ("node", "bot.js");
        }

        return (string.Empty, string.Empty);
    }

    private static BotConfig? LoadConfig()
    {
        if (!File.Exists(ConfigFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: '{ConfigFile}' tidak ditemukan.[/]");
            return null;
        }

        var json = File.ReadAllText(ConfigFile);
        return JsonSerializer.Deserialize<BotConfig>(json);
    }
}
