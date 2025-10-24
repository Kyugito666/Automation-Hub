using Spectre.Console;
using System.Linq;
using System; // <-- Tambahkan using System

namespace Orchestrator;

internal static class Program
{
    // Flag untuk menandakan Ctrl+C ditekan saat bot interaktif jalan
    private static bool _interactiveBotCancelled = false;

    public static async Task Main(string[] args)
    {
        // --- TAMBAHKAN HANDLER CTRL+C ---
        Console.CancelKeyPress += (sender, e) =>
        {
            AnsiConsole.MarkupLine("\n[yellow]Ctrl+C terdeteksi. Menghentikan bot saat ini...[/]");
            // Mencegah aplikasi utama terminate
            e.Cancel = true;
            // Set flag (meskipun mungkin tidak langsung digunakan, bagus untuk state)
            _interactiveBotCancelled = true;
            // Proses anak (bot) akan menerima sinyalnya dan berhenti,
            // ShellHelper.RunInteractive akan selesai, loop akan lanjut.
        };
        // --------------------------------

        TokenManager.Initialize();

        if (args.Length > 0)
        {
            await RunTask(args[0]);
            return;
        }

        await RunInteractive();
    }

    private static async Task RunTask(string task)
    {
        // ... (Tidak berubah) ...
        switch (task.ToLower())
        {
            case "--update-bots":
                await BotUpdater.UpdateAllBots();
                break;
            case "--deploy-proxies":
                await ProxyManager.DeployProxies();
                break;
            case "--trigger-all":
                await GitHubDispatcher.TriggerAllBotsWorkflow();
                break;
            case "--status":
                await GitHubDispatcher.GetWorkflowRuns();
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown task: {task}[/]");
                break;
        }
    }

    // =================================================================
    // NAVIGASI MENU UTAMA
    // =================================================================

    private static async Task RunInteractive()
    {
       // ... (Tidak berubah) ...
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Interactive Proxy Orchestrator - Local Control, Remote Execution[/]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MAIN MENU[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. [[SETUP]] Configuration & Token Management",
                        "2. [[LOCAL]] Bot & Proxy Management",
                        "3. [[HYBRID]] Interactive Remote Execution",
                        "4. [[REMOTE]] GitHub Actions Control",
                        "5. [[DEBUG]] Local Bot Testing",
                        "0. Exit"
                    }));

            if (choice.StartsWith("0"))
            {
                AnsiConsole.MarkupLine("[green]Goodbye![/]");
                return;
            }

            // Reset flag cancel sebelum masuk submenu yang mungkin menjalankan bot
             _interactiveBotCancelled = false;

            switch (choice.Split('.')[0])
            {
                case "1": await ShowSetupMenu(); break;
                case "2": await ShowLocalMenu(); break;
                case "3": await ShowHybridMenu(); break;
                case "4": await ShowRemoteMenu(); break;
                case "5": await ShowDebugMenu(); break;
            }
        }
    }

    // =================================================================
    // SUB-MENU HANDLERS
    // =================================================================

    private static async Task ShowSetupMenu()
    {
        // ... (Tidak berubah) ...
         while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold yellow]SETUP & CONFIGURATION[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Validate Tokens & Get Usernames",
                        "2. Invite Collaborators",
                        "3. Accept Invitations",
                        "4. Show Token/Proxy Status",
                        "5. [[SYSTEM]] Refresh All Configs (Reload files)",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
             // Reset flag cancel sebelum eksekusi
             _interactiveBotCancelled = false;
            switch (selection)
            {
                case "1": await CollaboratorManager.ValidateAllTokens(); break;
                case "2": await CollaboratorManager.InviteCollaborators(); break;
                case "3": await CollaboratorManager.AcceptInvitations(); break;
                case "4": TokenManager.ShowStatus(); break;
                case "5": TokenManager.ReloadAllConfigs(); break;
                default: pause = false; break;
            }

            if (pause) Pause();
        }
    }

    private static async Task ShowLocalMenu()
    {
        // ... (Tidak berubah) ...
         while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Local").Centered().Color(Color.Green));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold green]LOCAL BOT & PROXY MANAGEMENT[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Update All Bots & Tools",
                        "2. Deploy Proxies",
                        "3. Show Bot Config",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
             // Reset flag cancel sebelum eksekusi
             _interactiveBotCancelled = false;
            switch (selection)
            {
                case "1": await BotUpdater.UpdateAllBots(); break;
                case "2": await ProxyManager.DeployProxies(); break;
                case "3": BotUpdater.ShowConfig(); break;
                default: pause = false; break;
            }

            if (pause) Pause();
        }
    }

    private static async Task ShowHybridMenu()
    {
       // ... (Tidak berubah) ...
         while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Hybrid").Centered().Color(Color.Blue));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold blue]HYBRID INTERACTIVE EXECUTION[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Run Interactive Bot -> Remote",
                        "2. Run ALL Interactive Bots -> Remote",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
             // Reset flag cancel sebelum eksekusi
             _interactiveBotCancelled = false;
            switch (selection)
            {
                case "1": await RunSingleInteractiveBot(); break;
                case "2": await RunAllInteractiveBots(); break;
                default: pause = false; break;
            }

            // Pause hanya jika tidak cancel dan tidak kembali
            if (pause && !_interactiveBotCancelled) Pause();
             _interactiveBotCancelled = false; // Reset lagi setelah pause
        }
    }

    private static async Task ShowRemoteMenu()
    {
       // ... (Tidak berubah) ...
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Remote").Centered().Color(Color.Red));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold red]GITHUB ACTIONS CONTROL[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Trigger ALL Bots (Workflow)",
                        "2. View Workflow Status",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
             // Reset flag cancel sebelum eksekusi
             _interactiveBotCancelled = false;
            switch (selection)
            {
                case "1": await GitHubDispatcher.TriggerAllBotsWorkflow(); break;
                case "2": await GitHubDispatcher.GetWorkflowRuns(); break;
                default: pause = false; break;
            }

            if (pause) Pause();
        }
    }

    private static async Task ShowDebugMenu()
    {
        // ... (Tidak berubah) ...
         while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold grey]DEBUG & LOCAL TESTING[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Test Local Bot (No Remote)",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
             // Reset flag cancel sebelum eksekusi
             _interactiveBotCancelled = false;
            switch (selection)
            {
                case "1": await TestLocalBot(); break;
                default: pause = false; break;
            }

             // Pause hanya jika tidak cancel dan tidak kembali
            if (pause && !_interactiveBotCancelled) Pause();
             _interactiveBotCancelled = false; // Reset lagi setelah pause
        }
    }

    private static void Pause()
    {
        AnsiConsole.MarkupLine("\n[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }

    // =================================================================
    // METHOD HELPER
    // =================================================================

    // Objek dummy untuk representasi "Back"
    private static readonly BotEntry BackOption = new() { Name = "[[Back]] Kembali", Type = "SYSTEM" };

    private static async Task RunSingleInteractiveBot()
    {
        // ... (Tidak berubah) ...
        var config = BotConfig.Load();
        if (config == null) return;

        var bots = config.BotsAndTools
            .Where(b => b.Enabled && b.IsBot)
            .OrderBy(b => b.Name)
            .ToList();

        if (!bots.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No active bots found.[/]");
            return;
        }

        var choices = bots.ToList();
        choices.Add(BackOption);

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for interactive run:[/]")
                .PageSize(20)
                .WrapAround()
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices));

        if (selectedBot == BackOption) return;

        // Reset flag sebelum menjalankan bot
        _interactiveBotCancelled = false;
        await InteractiveProxyRunner.CaptureAndTriggerBot(selectedBot);
        // Tidak perlu cek _interactiveBotCancelled di sini, biarkan kembali ke menu
    }

    private static async Task RunAllInteractiveBots()
    {
        // ... (Bagian awal tidak berubah) ...
         var config = BotConfig.Load();
        if (config == null) return;

        var bots = config.BotsAndTools
            .Where(b => b.Enabled && b.IsBot)
            .OrderBy(b => b.Name)
            .ToList();

        if (!bots.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No active bots found.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Found {bots.Count} active bots (Sorted Alphabetically)[/]");
        AnsiConsole.MarkupLine("[yellow]WARNING: This will run all bots locally for input capture.[/]");
        AnsiConsole.MarkupLine("[grey]You can press Ctrl+C during a bot's run to skip to the next.[/]"); // Hint Ctrl+C

        if (!AnsiConsole.Confirm("Continue?"))
            return;

        var successCount = 0;
        var failCount = 0;

        foreach (var bot in bots)
        {
            // Reset flag _sebelum_ menjalankan setiap bot
            _interactiveBotCancelled = false;

            AnsiConsole.Write(new Rule($"[cyan]{bot.Name}[/]").Centered());

            try
            {
                // CaptureAndTriggerBot akan selesai jika bot exit ATAU Ctrl+C ditekan
                await InteractiveProxyRunner.CaptureAndTriggerBot(bot);

                // Cek flag *setelah* bot selesai/di-cancel
                if (!_interactiveBotCancelled)
                {
                    // Hanya hitung sukses jika tidak di-cancel
                    successCount++;
                } else {
                    // Jika di-cancel, anggap gagal/skipped
                    failCount++;
                    AnsiConsole.MarkupLine("[yellow]Skipped due to Ctrl+C.[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
                failCount++;
            }

            // Tampilkan prompt 'next' hanya jika TIDAK di-cancel DAN bukan bot terakhir
            if (!_interactiveBotCancelled && bot != bots.Last())
            {
                AnsiConsole.MarkupLine("\n[dim]Press Enter for next bot...[/]");
                Console.ReadLine();
            }
            else if (_interactiveBotCancelled)
            {
                // Jika di-cancel, langsung lanjut ke bot berikutnya tanpa enter
                AnsiConsole.MarkupLine("\n[grey]Continuing to next bot...[/]");
                await Task.Delay(1000); // Jeda singkat
            }
             // Jika bot terakhir (baik sukses maupun cancel), loop akan selesai
        }

        AnsiConsole.MarkupLine($"\n[bold]Summary:[/]");
        AnsiConsole.MarkupLine($"[green]✓ Completed/Attempted: {successCount}[/]"); // Ubah label sukses
        AnsiConsole.MarkupLine($"[yellow]✗ Skipped/Failed: {failCount}[/]"); // Ubah label gagal
    }


    private static async Task TestLocalBot()
    {
        // ... (Tidak berubah) ...
        var config = BotConfig.Load();
        if (config == null) return;

        var bots = config.BotsAndTools
            .Where(b => b.Enabled && b.IsBot)
            .OrderBy(b => b.Name)
            .ToList();

        if (!bots.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No active bots found.[/]");
            return;
        }

        var choices = bots.ToList();
        choices.Add(BackOption);

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for local test:[/]")
                .PageSize(20)
                .WrapAround()
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices));

        if (selectedBot == BackOption) return;

         // Reset flag sebelum menjalankan bot
        _interactiveBotCancelled = false;

        var botPath = Path.Combine("..", selectedBot.Path);
        if (!Directory.Exists(botPath))
        {
            AnsiConsole.MarkupLine($"[red]Bot path not found: {botPath}[/]");
            return;
        }

        await BotRunner.InstallDependencies(botPath, selectedBot.Type);

        var (executor, args) = BotRunner.GetRunCommand(botPath, selectedBot.Type);
        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine("[red]No run file found[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]Running {selectedBot.Name} locally...[/]");
        AnsiConsole.MarkupLine("[dim]This is a test run. No remote execution.[/]\n");

        await ShellHelper.RunInteractive(executor, args, botPath);
         // Setelah selesai (atau di-cancel), biarkan kembali ke menu debug
    }
}
