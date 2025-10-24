using Spectre.Console;
using System.Linq; // <-- Pastikan using ini ada

namespace Orchestrator;

internal static class Program
{
    // ... (Main, RunTask, Menu Navigasi tidak berubah) ...
    public static async Task Main(string[] args)
    {
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

    private static async Task RunInteractive()
    {
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
     private static async Task ShowSetupMenu()
    {
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
            switch (selection)
            {
                case "1": await RunSingleInteractiveBot(); break; // Panggil method yang diupdate
                case "2": await RunAllInteractiveBots(); break;
                default: pause = false; break;
            }

            if (pause) Pause();
        }
    }

    private static async Task ShowRemoteMenu()
    {
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
            switch (selection)
            {
                case "1": await TestLocalBot(); break; // Panggil method yang diupdate
                default: pause = false; break;
            }

            if (pause) Pause();
        }
    }

    private static void Pause()
    {
        AnsiConsole.MarkupLine("\n[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }


    // =================================================================
    // METHOD HELPER (UPDATED)
    // =================================================================

    // Objek dummy untuk representasi "Back"
    private static readonly BotEntry BackOption = new() { Name = "[[Back]] Kembali", Type = "SYSTEM" };

    private static async Task RunSingleInteractiveBot()
    {
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

        // --- TAMBAHKAN OPSI BACK ---
        var choices = bots.ToList(); // Buat salinan
        choices.Add(BackOption);     // Tambahkan opsi Back di akhir

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for interactive run:[/]")
                .PageSize(20)
                .WrapAround()
                 // Converter untuk menampilkan nama bot atau teks "Back"
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices)); // Gunakan list yang sudah ada opsi Back

        // --- CEK JIKA USER MEMILIH BACK ---
        if (selectedBot == BackOption)
        {
            return; // Kembali ke menu sebelumnya (Hybrid Menu)
        }

        // Lanjutkan jika bukan Back
        await InteractiveProxyRunner.CaptureAndTriggerBot(selectedBot);
    }

    private static async Task RunAllInteractiveBots()
    {
        // ... (Method ini tidak menampilkan pilihan bot, jadi tidak perlu diubah) ...
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

        if (!AnsiConsole.Confirm("Continue?"))
            return;

        var successCount = 0;
        var failCount = 0;

        foreach (var bot in bots)
        {
            AnsiConsole.Write(new Rule($"[cyan]{bot.Name}[/]").Centered());

            try
            {
                await InteractiveProxyRunner.CaptureAndTriggerBot(bot);
                successCount++;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
                failCount++;
            }

            if (bot != bots.Last())
            {
                AnsiConsole.MarkupLine("\n[dim]Press Enter for next bot...[/]");
                Console.ReadLine();
            }
        }

        AnsiConsole.MarkupLine($"\n[bold]Summary:[/]");
        AnsiConsole.MarkupLine($"[green]✓ Success: {successCount}[/]");
        AnsiConsole.MarkupLine($"[red]✗ Failed: {failCount}[/]");
    }

    private static async Task TestLocalBot()
    {
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

        // --- TAMBAHKAN OPSI BACK ---
        var choices = bots.ToList();
        choices.Add(BackOption);

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for local test:[/]")
                .PageSize(20)
                .WrapAround()
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices));

        // --- CEK JIKA USER MEMILIH BACK ---
        if (selectedBot == BackOption)
        {
            return; // Kembali ke menu sebelumnya (Debug Menu)
        }

        // Lanjutkan jika bukan Back
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
    }
}
