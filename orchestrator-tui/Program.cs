using Spectre.Console;

namespace Orchestrator;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        // Tetap sinkron, cache di-load di sini
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
                    .PageSize(20) // Ditambah
                    .AddChoices(new[]
                    {
                        // --- MENU SETUP BARU ---
                        "A. (SETUP) Validate Tokens & Get Usernames",
                        "B. (SETUP) Invite Collaborators",
                        "C. (SETUP) Accept Invitations",
                        "---",
                        // --- Menu Lama ---
                        "1. (LOCAL) Update All Bots & Tools",
                        "2. (LOCAL) Deploy Proxies",
                        "3. (LOCAL) Show Bot Config",
                        "4. (LOCAL) Show Token/Proxy Status", // Tambahan
                        "---",
                        "5. (HYBRID) Run Interactive Bot → Remote Execution",
                        "6. (HYBRID) Run All Interactive Bots → Remote Execution",
                        "---",
                        "7. (REMOTE) Trigger ALL Bots (No Interaction)",
                        "8. (REMOTE) View Workflow Status",
                        "---",
                        "9. (DEBUG) Test Local Bot (No Remote)",
                        "0. Exit"
                    }));

            switch (choice.Split('.')[0].ToUpper())
            {
                // --- HANDLER BARU ---
                case "A":
                    await CollaboratorManager.ValidateAllTokens();
                    break;
                case "B":
                    await CollaboratorManager.InviteCollaborators();
                    break;
                case "C":
                    await CollaboratorManager.AcceptInvitations();
                    break;
                
                // --- HANDLER LAMA ---
                case "1":
                    await BotUpdater.UpdateAllBots();
                    break;
                case "2":
                    await ProxyManager.DeployProxies();
                    break;
                case "3":
                    BotUpdater.ShowConfig();
                    break;
                case "4":
                    TokenManager.ShowStatus(); // Tambahan
                    break;
                case "5":
                    await RunSingleInteractiveBot();
                    break;
                case "6":
                    await RunAllInteractiveBots();
                    break;
                case "7":
                    await GitHubDispatcher.TriggerAllBotsWorkflow();
                    break;
                case "8":
                    await GitHubDispatcher.GetWorkflowRuns();
                    break;
                case "9":
                    await TestLocalBot();
                    break;
                case "0":
                    AnsiConsole.MarkupLine("[green]Goodbye![/]");
                    return;
                case "---":
                    continue;
            }

            if (choice != "---")
            {
                AnsiConsole.MarkupLine("\n[grey]Press Enter to continue...[/]");
                Console.ReadLine();
            }
        }
    }

    private static async Task RunSingleInteractiveBot()
    {
        var config = BotConfig.Load();
        if (config == null) return;

        var bots = config.BotsAndTools
            .Where(b => b.Enabled && b.IsBot)
            .ToList();

        if (!bots.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No active bots found.[/]");
            return;
        }

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for interactive run:[/]")
                .PageSize(20)
                .UseConverter(b => $"{b.Name} ({b.Type})")
                .AddChoices(bots));

        await InteractiveProxyRunner.CaptureAndTriggerBot(selectedBot);
    }

    private static async Task RunAllInteractiveBots()
    {
        var config = BotConfig.Load();
        if (config == null) return;

        var bots = config.BotsAndTools
            .Where(b => b.Enabled && b.IsBot)
            .ToList();

        if (!bots.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No active bots found.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Found {bots.Count} active bots[/]");
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
            .ToList();

        if (!bots.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No active bots found.[/]");
            return;
        }

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for local test:[/]")
                .PageSize(20)
                .UseConverter(b => $"{b.Name} ({b.Type})")
                .AddChoices(bots));

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
