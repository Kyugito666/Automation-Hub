using System.Text.Json;
using Spectre.Console;
using System.Runtime.InteropServices; // Ditambahkan untuk OSPlatform

namespace Orchestrator;

public static class TmuxRunner
{
    private const string ConfigFile = "../config/bots_config.json";
    private const string SessionName = "automation-hub";

    public static async Task RunAllBotsInTmux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            AnsiConsole.MarkupLine("[red]Tmux mode hanya tersedia di Linux.[/]");
            return;
        }

        // Check tmux installed
        if (!IsCommandAvailable("tmux"))
        {
            AnsiConsole.MarkupLine("[red]tmux tidak terinstall. Install: sudo apt install tmux[/]");
            return;
        }

        var config = LoadConfig();
        if (config == null) return;

        var botsOnly = config.BotsAndTools
            .Where(b => b.Path.Contains("/privatekey/") || b.Path.Contains("/token/"))
            .Where(b => b.Enabled)
            .ToList();

        if (!botsOnly.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Tidak ada bot aktif.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Membuat tmux session '{SessionName}'...[/]");

        // Kill existing session
        await ShellHelper.RunStream("tmux", $"kill-session -t {SessionName}", null);

        // Create new session with first bot
        var firstBot = botsOnly.First();
        var firstPath = Path.GetFullPath(Path.Combine("..", firstBot.Path));
        var (firstExec, firstArgs) = GetRunCommand(firstPath, firstBot.Type);

        await ShellHelper.RunStream("tmux", 
            $"new-session -d -s {SessionName} -n {firstBot.Name} -c {firstPath} '{firstExec} {firstArgs}'", 
            null);

        AnsiConsole.MarkupLine($"[green]✓ {firstBot.Name}[/]");

        // Create window for each remaining bot
        foreach (var bot in botsOnly.Skip(1))
        {
            var botPath = Path.GetFullPath(Path.Combine("..", bot.Path));
            var (executor, args) = GetRunCommand(botPath, bot.Type);

            if (string.IsNullOrEmpty(executor))
            {
                AnsiConsole.MarkupLine($"[red]✗ {bot.Name}: No run file[/]");
                continue;
            }

            await ShellHelper.RunStream("tmux",
                $"new-window -t {SessionName} -n {bot.Name} -c {botPath} '{executor} {args}'",
                null);

            AnsiConsole.MarkupLine($"[green]✓ {bot.Name}[/]");
        }

        AnsiConsole.MarkupLine($"\n[bold green]✅ Semua bot berjalan di tmux session '{SessionName}'[/]");
        AnsiConsole.MarkupLine("[yellow]Perintah berguna:[/]");
        AnsiConsole.MarkupLine($"[dim]  tmux attach -t {SessionName}     # Attach ke session[/]");
        AnsiConsole.MarkupLine($"[dim]  tmux ls                          # List sessions[/]");
        AnsiConsole.MarkupLine($"[dim]  tmux kill-session -t {SessionName}  # Stop semua bot[/]");
        AnsiConsole.MarkupLine($"[dim]  Ctrl+B lalu D                    # Detach dari session[/]");
    }

    private static (string executor, string args) GetRunCommand(string botPath, string type)
    {
        if (type == "python")
        {
            if (File.Exists(Path.Combine(botPath, "run.py"))) return ("python3", "run.py");
            if (File.Exists(Path.Combine(botPath, "main.py"))) return ("python3", "main.py");
        }
        if (type == "javascript")
        {
            if (File.Exists(Path.Combine(botPath, "index.js"))) return ("node", "index.js");
            if (File.Exists(Path.Combine(botPath, "main.js"))) return ("node", "main.js");
        }
        return (string.Empty, string.Empty);
    }

    private static bool IsCommandAvailable(string cmd)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "which",
                Arguments = cmd,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static BotConfig? LoadConfig()
    {
        if (!File.Exists(ConfigFile))
        {
            AnsiConsole.MarkupLine($"[red]Error: '{ConfigFile}' tidak ditemukan.[/]");
            return null;
        }
        return JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(ConfigFile), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
}
