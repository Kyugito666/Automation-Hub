using Spectre.Console;

namespace Orchestrator;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        // Initialize GitHub API client
        GitHubDispatcher.Initialize();

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
            AnsiConsole.MarkupLine("[grey]Remote Orchestrator - GitHub Actions Edition[/]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MENU UTAMA[/]")
                    .PageSize(12)
                    .AddChoices(new[]
                    {
                        "1. (LOCAL) Clone/Update Semua Bot & Tools",
                        "2. (LOCAL) Deploy Proxies",
                        "3. (LOCAL) Tampilkan Konfigurasi Bot",
                        "---",
                        "4. (REMOTE) Trigger ALL Bots di GitHub Actions",
                        "5. (REMOTE) Trigger Single Bot di GitHub Actions",
                        "6. (REMOTE) Lihat Status Workflow Runs",
                        "---",
                        "7. (FULL) Setup + Trigger Workflow",
                        "8. Keluar"
                    }));

            switch (choice.Split('.')[0])
            {
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
                    await GitHubDispatcher.TriggerAllBotsWorkflow();
                    break;
                case "5":
                    await TriggerSingleBotMenu();
                    break;
                case "6":
                    await GitHubDispatcher.GetWorkflowRuns();
                    break;
                case "7":
                    await FullSetupAndTrigger();
                    break;
                case "8":
                    AnsiConsole.MarkupLine("[green]Sampai jumpa![/]");
                    return;
                case "---":
                    continue;
            }

            if (choice != "---")
            {
                AnsiConsole.MarkupLine("\n[grey]Tekan Enter untuk kembali ke menu...[/]");
                Console.ReadLine();
            }
        }
    }

    private static async Task TriggerSingleBotMenu()
    {
        var config = BotUpdater.LoadConfig();
        if (config == null) return;

        var bots = config.BotsAndTools
            .Where(b => b.Enabled && (b.Path.Contains("/privatekey/") || b.Path.Contains("/token/")))
            .ToList();

        if (!bots.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Tidak ada bot aktif.[/]");
            return;
        }

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Pilih bot untuk di-trigger:[/]")
                .PageSize(15)
                .UseConverter(b => b.Name)
                .AddChoices(bots));

        await GitHubDispatcher.TriggerSingleBot(
            selectedBot.Name,
            selectedBot.Path,
            selectedBot.RepoUrl,
            selectedBot.Type
        );
    }

    private static async Task FullSetupAndTrigger()
    {
        AnsiConsole.MarkupLine("[bold cyan]=== FULL SETUP & TRIGGER ===[/]");
        
        AnsiConsole.MarkupLine("\n[yellow]Step 1/2: Update Bot & Tools (Lokal)[/]");
        await BotUpdater.UpdateAllBots();
        
        AnsiConsole.MarkupLine("\n[yellow]Step 2/2: Trigger Workflow (Remote)[/]");
        await GitHubDispatcher.TriggerAllBotsWorkflow();
        
        AnsiConsole.MarkupLine("\n[bold green]✅ SETUP & TRIGGER SELESAI[/]");
        AnsiConsole.MarkupLine("[dim]Workflow akan berjalan di GitHub Actions dalam beberapa detik...[/]");
    }
}
